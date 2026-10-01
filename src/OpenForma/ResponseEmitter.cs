using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace OpenForma;

public sealed partial class OpenApiSourceGenerator
{
    private sealed partial class Emitter
    {
        private readonly StringBuilder responseClasses = new StringBuilder();
        private readonly HashSet<string> reservedTypes = new HashSet<string> { "ApiResponse", "ApiException" };

        private sealed class ResponseDefinition
        {
            public string Key = "";
            public string? Type;
            public readonly List<string> MediaTypes = new List<string>();
            public string Property => "Response" + (Key == "default" ? "Default" : Key);
        }

        private List<ResponseDefinition> ReadResponses(YamlMappingNode operation, string operationName)
        {
            var definitions = new List<ResponseDefinition>();
            foreach (var pair in Entries(Map(operation, "responses")))
            {
                if (pair.Key.StartsWith("x-", StringComparison.Ordinal)) continue;
                var key = pair.Key;
                var exact = key.Length == 3 && key[0] >= '1' && key[0] <= '5' && key.Skip(1).All(ch => ch >= '0' && ch <= '9');
                var range = key.Length == 3 && key[0] >= '1' && key[0] <= '5' && key.Substring(1) == "XX";
                if (!exact && !range && key != "default")
                    throw new InvalidOperationException("Invalid response status: " + key);
                var definition = new ResponseDefinition { Key = key };
                var response = Resolve(AsMap(pair.Value));
                foreach (var media in Entries(Map(response, "content")))
                {
                    var isJson = media.Key == "application/json" || media.Key.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
                    if (media.Key.Contains("*") || (!isJson && media.Key != "text/plain"))
                        throw new InvalidOperationException("Unsupported response media type: " + media.Key);
                    var schema = Map(AsMap(media.Value), "schema");
                    var type = Type(schema, operationName + "Response" + (key == "default" ? "Default" : key) + "Body");
                    if (media.Key == "text/plain" && type != "string")
                        throw new InvalidOperationException("text/plain response schemas must be strings.");
                    if (definition.Type != null && definition.Type != type)
                        throw new InvalidOperationException("Different media types for one response must use the same schema.");
                    definition.Type = type;
                    definition.MediaTypes.Add(media.Key);
                }
                definitions.Add(definition);
            }
            if (definitions.Count == 0) throw new InvalidOperationException("Operation must declare at least one response.");
            return definitions.OrderBy(d => d.Key == "default" ? 2 : d.Key.EndsWith("XX", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(d => d.Key, StringComparer.Ordinal).ToList();
        }

        private void EmitResponseClass(string name, List<ResponseDefinition> definitions)
        {
            if (!reservedTypes.Add(name) || models.ContainsKey(name))
                throw new InvalidOperationException("Conflicting response type name: " + name);
            responseClasses.AppendLine("public sealed class " + name + " : ApiResponse\n{");
            foreach (var definition in definitions.Where(d => d.Type != null))
                responseClasses.AppendLine("public " + Nullable(definition.Type!) + " " + definition.Property + " { get; internal set; }");
            responseClasses.AppendLine("}");
        }

        private void EmitResponseHandling(string responseClass, List<ResponseDefinition> definitions, string verb)
        {
            code.AppendLine("var result = new " + responseClass + " { StatusCode = response.StatusCode };");
            code.AppendLine("foreach (var header in response.Headers) result.Headers[header.Key] = System.Linq.Enumerable.ToArray(header.Value);");
            code.AppendLine("foreach (var header in response.Content.Headers) result.Headers[header.Key] = System.Linq.Enumerable.ToArray(header.Value);");
            code.AppendLine("var status = (int)response.StatusCode;");
            var noBody = "status == 204 || status == 205 || status == 304";
            if (verb != "head") code.AppendLine("if (!(" + noBody + ")) result.RawBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);");
            for (int index = 0; index < definitions.Count; index++)
            {
                var definition = definitions[index];
                var condition = definition.Key == "default" ? "true"
                    : definition.Key.EndsWith("XX", StringComparison.Ordinal) ? "status / 100 == " + definition.Key[0]
                    : "status == " + definition.Key;
                code.AppendLine((index == 0 ? "if" : "else if") + " (" + condition + ")\n{");
                code.AppendLine("result.MatchedResponse = " + Literal(definition.Key) + ";");
                if (definition.Type != null)
                {
                    code.AppendLine("if (result.RawBody.Length > 0)\n{\ntry\n{");
                    code.AppendLine("var mediaType = response.Content.Headers.ContentType?.MediaType ?? " + Literal(definition.MediaTypes[0]) + ";");
                    code.AppendLine("if (!(" + string.Join(" || ", definition.MediaTypes.Select(m =>
                        "string.Equals(mediaType, " + Literal(m) + ", StringComparison.OrdinalIgnoreCase)")) + "))");
                    code.AppendLine("throw new System.Text.Json.JsonException(\"Unexpected response content type: \" + mediaType);");
                    if (definition.MediaTypes.Contains("text/plain"))
                    {
                        code.AppendLine("if (string.Equals(mediaType, \"text/plain\", StringComparison.OrdinalIgnoreCase))");
                        code.AppendLine("result." + definition.Property + " = result.RawBody;");
                        code.AppendLine("else");
                    }
                    code.AppendLine("result." + definition.Property + " = System.Text.Json.JsonSerializer.Deserialize<" + Nullable(definition.Type) +
                        ">(result.RawBody, ApiResponse.JsonOptions);");
                    code.AppendLine("result.Body = result." + definition.Property + ";");
                    code.AppendLine("}\ncatch (System.Text.Json.JsonException exception) { result.DeserializationError = exception; }\n}");
                }
                code.AppendLine("}");
            }
            code.AppendLine("return result;\n}");
        }

        private void EmitConvenienceMethod(string method, string responseMethod, string responseClass,
            List<ResponseDefinition> definitions, List<string> declarations, List<string> arguments)
        {
            var successes = definitions.Where(d => d.Key[0] == '2').ToList();
            var types = successes.Where(d => d.Type != null).Select(d => d.Type!).Distinct().ToList();
            var returnsResult = types.Count > 1 || successes.Count == 0;
            var returnType = returnsResult ? "Task<" + responseClass + ">" : types.Count == 0 ? "Task" : "Task<" + Nullable(types[0]) + ">";
            code.AppendLine("public async " + returnType + " " + method + "(" + string.Join(", ", declarations) + ")\n{");
            code.AppendLine("var result = await " + responseMethod + "(" + string.Join(", ", arguments) + ").ConfigureAwait(false);");
            code.AppendLine("if (!result.IsSuccessStatusCode || result.DeserializationError != null)");
            code.AppendLine("throw new ApiException<" + responseClass + ">(\"HTTP response could not be completed successfully.\", result);");
            code.AppendLine("if (result.MatchedResponse == null && result.StatusCode != System.Net.HttpStatusCode.NoContent && result.StatusCode != System.Net.HttpStatusCode.ResetContent)");
            code.AppendLine("throw new ApiException<" + responseClass + ">(\"Undocumented HTTP response status.\", result);");
            if (returnsResult) code.AppendLine("return result;");
            else if (types.Count > 0)
            {
                code.AppendLine("if (result.Body == null) return default;");
                code.AppendLine("if (result.Body is " + types[0] + " responseBody) return responseBody;");
                code.AppendLine("throw new ApiException<" + responseClass + ">(\"Response schema does not match the convenience method return type. Use " + responseMethod + ".\", result);");
            }
            code.AppendLine("}");
        }

        private void EmitResponseInfrastructure()
        {
            code.AppendLine(@"
public abstract class ApiResponse
{
    internal static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    public System.Net.HttpStatusCode StatusCode { get; internal set; }
    public bool IsSuccessStatusCode => (int)StatusCode >= 200 && (int)StatusCode <= 299;
    public string? MatchedResponse { get; internal set; }
    public Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string RawBody { get; internal set; } = string.Empty;
    public object? Body { get; internal set; }
    public System.Text.Json.JsonException? DeserializationError { get; internal set; }
}
public class ApiException : HttpRequestException
{
    public ApiResponse Response { get; }
    public string RawBody => Response.RawBody;
    public IReadOnlyDictionary<string, string[]> Headers => Response.Headers;
    public ApiException(string message, ApiResponse response)
        : base(message + "" Status: "" + (int)response.StatusCode + ""."", response.DeserializationError, response.StatusCode)
    { Response = response; }
}
public sealed class ApiException<TResponse> : ApiException where TResponse : ApiResponse
{
    public new TResponse Response => (TResponse)base.Response;
    public ApiException(string message, TResponse response) : base(message, response) {}
}");
            code.Append(responseClasses);
        }
    }
}



