using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenForma;

public sealed partial class OpenApiSourceGenerator
{
    private sealed partial class Emitter
    {
        private const string Mvc = "global::Microsoft.AspNetCore.Mvc.";
        private readonly bool generateServer;

        private Dictionary<string, YamlMappingNode> ReadOperationParameters(YamlMappingNode pathItem, YamlMappingNode operation)
        {
            var parameters = new Dictionary<string, YamlMappingNode>();
            foreach (var parameter in Sequence(pathItem, "parameters").Concat(Sequence(operation, "parameters")))
            {
                var p = Resolve(AsMap(parameter));
                parameters[Value(p, "in") + ":" + Value(p, "name")] = p;
            }
            return parameters;
        }

        private void EmitServerOperation(string path, string verb, YamlMappingNode pathItem, YamlMappingNode operation)
        {
            if (!path.StartsWith("/", StringComparison.Ordinal))
                throw new InvalidOperationException("OpenAPI paths must start with '/'.");
            var operationName = Identifier(Value(operation, "operationId", verb + "_" + path));
            var method = operationName + "Async";
            var coreMethod = operationName + "CoreAsync";
            if (!methods.Add(method) || !methods.Add(coreMethod))
                throw new InvalidOperationException("Duplicate operation name: " + method);
            var parameters = ReadOperationParameters(pathItem, operation);
            var actionParameters = new List<string>();
            var coreParameters = new List<string>();
            var arguments = new List<string>();
            var names = new HashSet<string> { "body", "cancellationToken" };
            foreach (var parameter in parameters.Values)
            {
                var wireName = Value(parameter, "name");
                var name = Identifier(wireName);
                if (!names.Add(name)) throw new InvalidOperationException("Conflicting parameter name: " + wireName);
                var location = Value(parameter, "in");
                var schema = Map(parameter, "schema");
                var type = Type(schema, method + name);
                if (type.EndsWith("[]", StringComparison.Ordinal) || type.StartsWith("Dictionary", StringComparison.Ordinal)
                    || models.ContainsKey(type) || type == "System.Text.Json.JsonElement")
                    throw new InvalidOperationException("Only scalar operation parameters are supported.");
                if (Get(parameter, "style") != null || Get(parameter, "explode") != null)
                    throw new InvalidOperationException("Explicit parameter serialization styles are not yet supported.");
                var binding = location == "path" ? "FromRoute" : location == "query" ? "FromQuery" : location == "header" ? "FromHeader" : null;
                if (binding == null) throw new InvalidOperationException("Unsupported parameter location: " + location);
                var required = location == "path" || Value(parameter, "required") == "true";
                var nullable = !required || Value(Resolve(schema), "nullable") == "true";
                var declaration = (nullable ? Nullable(type) : type) + " " + name;
                var attributes = "[" + Mvc + binding + "(Name = " + Literal(wireName) + ")] ";
                if (required)
                {
                    attributes += "[" + Mvc + "ModelBinding.BindRequired] ";
                    if (!nullable) attributes += "[global::System.ComponentModel.DataAnnotations.Required] ";
                }
                actionParameters.Add(attributes + declaration);
                coreParameters.Add(declaration);
                arguments.Add(name);
            }
            foreach (var segment in path.Split('{').Skip(1))
            {
                var parameterName = segment.Split('}')[0];
                if (!parameters.ContainsKey("path:" + parameterName))
                    throw new InvalidOperationException("Missing path parameter: " + parameterName);
            }

            var body = Get(operation, "requestBody") as YamlMappingNode;
            if (body != null)
            {
                body = Resolve(body);
                var schema = JsonSchema(body);
                var required = Value(body, "required") == "true";
                var type = Type(schema, method + "Body");
                var declaration = (!required || Value(Resolve(schema), "nullable") == "true" ? Nullable(type) : type) + " body";
                actionParameters.Add("[" + Mvc + "FromBody(EmptyBodyBehavior = " + Mvc +
                    "ModelBinding.EmptyBodyBehavior." + (required ? "Disallow" : "Allow") + ")] " + declaration);
                coreParameters.Add(declaration);
                arguments.Add("body");
                code.AppendLine("[" + Mvc + "Consumes(\"application/json\")]");
            }
            var responses = ReadResponses(operation, operationName);
            foreach (var response in responses)
            {
                if (response.Key == "default")
                {
                    code.AppendLine("[" + Mvc + "ProducesDefaultResponseType(" +
                        (response.Type == null ? "" : "typeof(" + response.Type + ")") + ")]");
                }
                else if (!response.Key.EndsWith("XX", StringComparison.Ordinal))
                {
                    var argumentsText = response.Type == null ? response.Key
                        : "typeof(" + response.Type + "), " + response.Key +
                            (response.MediaTypes.Count == 0 ? "" : ", " + string.Join(", ", response.MediaTypes.Select(Literal)));
                    code.AppendLine("[" + Mvc + "ProducesResponseType(" + argumentsText + ")]");
                }
            }
            var successTypes = responses.Where(r => r.Key[0] == '2' && r.Type != null).Select(r => r.Type!).Distinct().ToList();
            var resultType = successTypes.Count == 1 ? Mvc + "ActionResult<" + successTypes[0] + ">" : Mvc + "IActionResult";
            var returnType = "global::System.Threading.Tasks.Task<" + resultType + ">";
            var httpAttributes = new Dictionary<string, string>
            {
                ["get"] = "HttpGet", ["post"] = "HttpPost", ["put"] = "HttpPut",
                ["patch"] = "HttpPatch", ["delete"] = "HttpDelete", ["head"] = "HttpHead", ["options"] = "HttpOptions"
            };
            code.AppendLine(verb == "trace"
                ? "[" + Mvc + "AcceptVerbs(\"TRACE\", Route = " + Literal(path) + ")]"
                : "[" + Mvc + httpAttributes[verb] + "(" + Literal(path) + ")]");
            actionParameters.Add("global::System.Threading.CancellationToken cancellationToken = default");
            coreParameters.Add("global::System.Threading.CancellationToken cancellationToken");
            arguments.Add("cancellationToken");
            code.AppendLine("public " + returnType + " " + method + "(" + string.Join(", ", actionParameters) + ")");
            code.AppendLine("    => " + coreMethod + "(" + string.Join(", ", arguments) + ");");
            code.AppendLine("protected abstract " + returnType + " " + coreMethod + "(" + string.Join(", ", coreParameters) + ");");
        }
    }
}
