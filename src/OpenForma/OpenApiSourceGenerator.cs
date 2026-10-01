using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using YamlDotNet.RepresentationModel;

namespace OpenForma;

[Generator(LanguageNames.CSharp)]
public sealed partial class OpenApiSourceGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidSpec = new DiagnosticDescriptor(
        "OAG001", "Cannot generate OpenAPI types", "{0}", "OpenForma", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider.Where(file =>
            new[] { ".json", ".yaml", ".yml" }.Contains(Path.GetExtension(file.Path).ToLowerInvariant()));
        var projectOptions = context.AnalyzerConfigOptionsProvider.Select((provider, _) =>
        {
            provider.GlobalOptions.TryGetValue("build_property.RootNamespace", out var rootNamespace);
            provider.GlobalOptions.TryGetValue("build_property.MSBuildProjectName", out var projectName);
            provider.GlobalOptions.TryGetValue("build_property.OpenApiGenerationMode", out var generationMode);
            return (RootNamespace: rootNamespace, ProjectName: projectName, GenerationMode: generationMode);
        });
        var compilationSettings = context.CompilationProvider.Select((compilation, _) =>
            (AssemblyName: compilation.AssemblyName ?? "Generated",
             HasMvc: compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ControllerBase") != null));
        var input = files.Collect().Combine(projectOptions.Combine(compilationSettings));
        context.RegisterSourceOutput(input, (output, item) =>
        {
            var specifications = item.Left;
            if (specifications.Length == 0) return;
            if (specifications.Length > 1)
            {
                output.ReportDiagnostic(Diagnostic.Create(InvalidSpec, Location.None,
                    "Supply one OpenAPI specification per consuming project."));
                return;
            }
            var file = specifications[0];
            try
            {
                var source = file.GetText(output.CancellationToken);
                if (source == null) throw new InvalidOperationException("Cannot read specification.");
                var settings = item.Right.Left;
                var projectName = string.IsNullOrWhiteSpace(settings.ProjectName) ? item.Right.Right.AssemblyName : settings.ProjectName!;
                var rootNamespace = string.IsNullOrWhiteSpace(settings.RootNamespace)
                    ? string.Join(".", item.Right.Right.AssemblyName.Split('.').Select(Emitter.ProjectIdentifier))
                    : settings.RootNamespace!;
                var mode = string.IsNullOrWhiteSpace(settings.GenerationMode) ? "Client" : settings.GenerationMode!.Trim();
                var generateServer = string.Equals(mode, "Server", StringComparison.OrdinalIgnoreCase);
                if (!generateServer && !string.Equals(mode, "Client", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("OpenApiGenerationMode must be Client or Server.");
                if (generateServer && !item.Right.Right.HasMvc)
                    throw new InvalidOperationException("Server generation requires Microsoft.NET.Sdk.Web or a FrameworkReference to Microsoft.AspNetCore.App.");
                var emitter = new Emitter(source.ToString(), projectName, rootNamespace, generateServer);
                output.AddSource(emitter.Name + ".g.cs", SourceText.From(emitter.Generate(), Encoding.UTF8));
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                output.ReportDiagnostic(Diagnostic.Create(InvalidSpec, Location.None, file.Path + ": " + exception.Message));
            }
        });
    }
    private sealed partial class Emitter
    {
        private readonly YamlMappingNode root;
        private readonly StringBuilder code = new StringBuilder();
        private readonly Dictionary<string, YamlMappingNode> models = new Dictionary<string, YamlMappingNode>();
        private readonly HashSet<string> methods = new HashSet<string>();
        public string Name { get; }
        private readonly string generatedNamespace;

        public Emitter(string text, string name, string rootNamespace, bool generateServer)
        {
            Name = Identifier(name);
            this.generateServer = generateServer;
            if (generateServer)
            {
                reservedTypes.Clear();
                reservedTypes.Add(Name + "AbstractController");
            }
            generatedNamespace = string.Join(".", rootNamespace.Split('.').Select(segment =>
            {
                var identifier = segment.StartsWith("@", StringComparison.Ordinal) ? segment.Substring(1) : segment;
                if (SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None && !SyntaxFacts.IsValidIdentifier(identifier))
                    throw new InvalidOperationException("Invalid consuming project RootNamespace: " + rootNamespace);
                return SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : segment;
            }));
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            if (yaml.Documents.Count != 1 || !(yaml.Documents[0].RootNode is YamlMappingNode map))
                throw new InvalidOperationException("Expected one OpenAPI document.");
            root = map;
            if (!Value(root, "openapi").StartsWith("3.0.", StringComparison.Ordinal))
                throw new InvalidOperationException("Only OpenAPI 3.0.x is currently supported.");
        }

        public string Generate()
        {
            code.AppendLine("// <auto-generated/>\n#nullable enable");
            code.AppendLine("using System;\nusing System.Collections.Generic;\nusing System.Net.Http;\nusing System.Net.Http.Json;\nusing System.Text.Json.Serialization;\nusing System.Threading;\nusing System.Threading.Tasks;");
            code.AppendLine("namespace " + generatedNamespace + "\n{");
            foreach (var schema in Entries(Map(Map(root, "components"), "schemas")))
            {
                var node = Resolve(AsMap(schema.Value));
                CheckSchema(node);
                if (Value(node, "type") == "object" || Get(node, "properties") != null)
                    AddModel(Identifier(schema.Key), node);
            }
            if (generateServer)
                code.AppendLine("[global::Microsoft.AspNetCore.Mvc.ApiController]\npublic abstract class " + Name + "AbstractController : global::Microsoft.AspNetCore.Mvc.ControllerBase\n{");
            else
            {
                code.AppendLine("public sealed class " + Name + "Client\n{\nprivate readonly HttpClient _httpClient;");
                code.AppendLine("public " + Name + "Client(HttpClient httpClient) { _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient)); }");
            }
            foreach (var path in Entries(Map(root, "paths")))
            {
                var item = Resolve(AsMap(path.Value));
                foreach (var operation in Entries(item).Where(pair => new[] { "get", "post", "put", "patch", "delete", "head", "options", "trace" }.Contains(pair.Key)))
                {
                    if (generateServer) EmitServerOperation(path.Key, operation.Key, item, AsMap(operation.Value));
                    else EmitOperation(path.Key, operation.Key, item, AsMap(operation.Value));
                }
            }
            code.AppendLine("}");
            if (!generateServer) EmitResponseInfrastructure();
            // Inline object schemas can discover further models while being emitted.
            var emitted = new HashSet<string>();
            while (models.Keys.Any(key => !emitted.Contains(key)))
            {
                var name = models.Keys.First(key => !emitted.Contains(key));
                emitted.Add(name);
                EmitModel(name, models[name]);
            }
            code.AppendLine("}");
            return code.ToString();
        }

        private void EmitModel(string name, YamlMappingNode schema)
        {
            CheckSchema(schema);
            if (Get(schema, "properties") == null && Get(schema, "additionalProperties") is YamlMappingNode values)
            {
                code.AppendLine("public sealed class " + name + " : Dictionary<string, " + Type(values, name + "Value") + "> {}");
                return;
            }
            code.AppendLine("public sealed class " + name + "\n{");
            var required = Sequence(schema, "required").Select(Scalar).ToHashSetCompat();
            var names = new HashSet<string>();
            foreach (var property in Entries(Map(schema, "properties")))
            {
                var propertyName = Identifier(property.Key);
                if (propertyName == name || !names.Add(propertyName))
                    throw new InvalidOperationException("Conflicting property name: " + property.Key);
                var propertySchema = Resolve(AsMap(property.Value));
                var type = Type(AsMap(property.Value), name + propertyName);
                var nullable = !required.Contains(property.Key) || Value(propertySchema, "nullable") == "true";
                code.AppendLine("[JsonPropertyName(" + Literal(property.Key) + ")]");
                code.AppendLine("public " + (nullable ? Nullable(type) : type) + " " + propertyName + " { get; set; }" + (nullable ? "" : " = default!;"));
            }
            code.AppendLine("}");
        }

        private void EmitOperation(string path, string verb, YamlMappingNode pathItem, YamlMappingNode operation)
        {
            var method = Identifier(Value(operation, "operationId", verb + "_" + path)) + "Async";
            if (!methods.Add(method)) throw new InvalidOperationException("Duplicate operation name: " + method);
            var parameters = ReadOperationParameters(pathItem, operation);
            var declarations = new List<string>();
            var arguments = new List<string>();
            var pathLines = new List<string>();
            var queryLines = new List<string>();
            var headerLines = new List<string>();
            var argumentNames = new HashSet<string> { "body", "cancellationToken", "url", "query", "request", "response", "result" };
            foreach (var parameter in parameters.Values.OrderByDescending(p => Value(p, "required") == "true"))
            {
                var wireName = Value(parameter, "name");
                var name = Identifier(wireName);
                if (!argumentNames.Add(name)) throw new InvalidOperationException("Conflicting parameter name: " + wireName);
                var location = Value(parameter, "in");
                var schema = Map(parameter, "schema");
                var type = Type(schema, method + name);
                if (type.EndsWith("[]", StringComparison.Ordinal) || type.StartsWith("Dictionary", StringComparison.Ordinal) || models.ContainsKey(type))
                    throw new InvalidOperationException("Only scalar operation parameters are supported.");
                if (Get(parameter, "style") != null || Get(parameter, "explode") != null)
                    throw new InvalidOperationException("Explicit parameter serialization styles are not yet supported.");
                var required = Value(parameter, "required") == "true" || location == "path";
                declarations.Add((required ? type : Nullable(type)) + " " + name);
                arguments.Add(name);
                var converted = "Convert.ToString(" + name + ", System.Globalization.CultureInfo.InvariantCulture)!";
                if (type == "bool") converted = name + ".ToString()!.ToLowerInvariant()";
                if (type == "DateOnly" || type == "DateTimeOffset")
                    converted = (required ? name : name + ".Value") + ".ToString(" + Literal(type == "DateOnly" ? "yyyy-MM-dd" : "O") + ", System.Globalization.CultureInfo.InvariantCulture)";
                if (location == "path")
                    pathLines.Add("url = url.Replace(" + Literal("{" + wireName + "}") + ", Uri.EscapeDataString(" + converted + "));");
                else if (location == "query")
                    queryLines.Add((required ? "" : "if (" + name + " != null) ") + "query.Add(" + Literal(Uri.EscapeDataString(wireName) + "=") + " + Uri.EscapeDataString(" + converted + "));");
                else if (location == "header")
                    headerLines.Add((required ? "" : "if (" + name + " != null) ") + "request.Headers.Add(" + Literal(wireName) + ", " + converted + ");");
                else throw new InvalidOperationException("Unsupported parameter location: " + location);
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
                var bodySchema = JsonSchema(body);
                var bodyType = Type(bodySchema, method + "Body");
                declarations.Add((Value(body, "required") == "true" ? bodyType : Nullable(bodyType)) + " body");
                arguments.Add("body");
            }
            var operationName = method.Substring(0, method.Length - "Async".Length);
            var responseMethod = operationName + "WithResponseAsync";
            if (!methods.Add(responseMethod)) throw new InvalidOperationException("Duplicate operation name: " + responseMethod);
            var responseClass = operationName + "Response";
            var responses = ReadResponses(operation, operationName);
            EmitResponseClass(responseClass, responses);
            declarations.Add("CancellationToken cancellationToken = default");
            arguments.Add("cancellationToken");
            EmitConvenienceMethod(method, responseMethod, responseClass, responses, declarations, arguments);
            code.AppendLine("public async Task<" + responseClass + "> " + responseMethod + "(" + string.Join(", ", declarations) + ")\n{");
            code.AppendLine("var url = " + Literal(path.TrimStart('/')) + ";");
            foreach (var line in pathLines) code.AppendLine(line);
            code.AppendLine("var query = new List<string>();");
            foreach (var line in queryLines) code.AppendLine(line);
            code.AppendLine("if (query.Count > 0) url += \"?\" + string.Join(\"&\", query);");
            code.AppendLine("using var request = new HttpRequestMessage(new HttpMethod(" + Literal(verb.ToUpperInvariant()) + "), url);");
            foreach (var line in headerLines) code.AppendLine(line);
            if (body != null) code.AppendLine("if (body != null) request.Content = JsonContent.Create(body);");
            code.AppendLine("using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);");
            EmitResponseHandling(responseClass, responses, verb);
        }
        private YamlMappingNode JsonSchema(YamlMappingNode node)
        {
            var content = Map(node, "content");
            var media = Get(content, "application/json") as YamlMappingNode;
            if (media == null) throw new InvalidOperationException("Only application/json request bodies are supported.");
            return Map(media, "schema");
        }

        private string Type(YamlMappingNode schema, string suggested)
        {
            var reference = Value(schema, "$ref");
            if (reference.Length > 0)
            {
                var resolved = Resolve(schema);
                if (Value(resolved, "type") == "object" || Get(resolved, "properties") != null)
                {
                    var name = Identifier(reference.Split('/').Last().Replace("~1", "/").Replace("~0", "~"));
                    AddModel(name, resolved);
                    return name;
                }
                return Type(resolved, suggested);
            }
            CheckSchema(schema);
            switch (Value(schema, "type"))
            {
                case "string":
                    switch (Value(schema, "format"))
                    {
                        case "date": return "DateOnly";
                        case "date-time": return "DateTimeOffset";
                        case "uuid": return "Guid";
                        case "byte": return "byte[]";
                        case "binary": throw new InvalidOperationException("Binary schemas are not supported.");
                        default: return "string";
                    }
                case "integer": return Value(schema, "format") == "int64" ? "long" : "int";
                case "number": return Value(schema, "format") == "float" ? "float" : Value(schema, "format") == "double" ? "double" : "decimal";
                case "boolean": return "bool";
                case "array":
                    var item = Map(schema, "items");
                    var itemType = Type(item, suggested + "Item");
                    return (Value(Resolve(item), "nullable") == "true" ? Nullable(itemType) : itemType) + "[]";
                case "object":
                case "":
                    if (Get(schema, "properties") != null)
                    {
                        AddModel(suggested, schema);
                        return suggested;
                    }
                    if (Get(schema, "additionalProperties") is YamlMappingNode additional)
                        return "Dictionary<string, " + Type(additional, suggested + "Value") + ">";
                    return "System.Text.Json.JsonElement";
                default: throw new InvalidOperationException("Unsupported schema type: " + Value(schema, "type"));
            }
        }

        private static void CheckSchema(YamlMappingNode schema)
        {
            foreach (var keyword in new[] { "allOf", "oneOf", "anyOf", "not", "discriminator" })
                if (Get(schema, keyword) != null) throw new InvalidOperationException("Unsupported schema keyword: " + keyword);
        }

        private void AddModel(string name, YamlMappingNode schema)
        {
            CheckSchema(schema);
            if (reservedTypes.Contains(name)) throw new InvalidOperationException("Schema name conflicts with generated type: " + name);
            if (!generateServer && name == Name + "Client") throw new InvalidOperationException("Schema name conflicts with client: " + name);
            if (models.TryGetValue(name, out var existing) && !ReferenceEquals(existing, schema))
                throw new InvalidOperationException("Conflicting schema name: " + name);
            models[name] = schema;
        }

        private YamlMappingNode Resolve(YamlMappingNode node)
        {
            var visited = new HashSet<string>();
            while (Get(node, "$ref") != null)
            {
                var reference = Value(node, "$ref");
                if (!reference.StartsWith("#/components/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Only local components references are supported: " + reference);
                if (!visited.Add(reference)) throw new InvalidOperationException("Circular reference alias: " + reference);
                YamlNode current = root;
                foreach (var part in reference.Substring(2).Split('/'))
                    current = Get(AsMap(current), part.Replace("~1", "/").Replace("~0", "~")) ?? throw new InvalidOperationException("Unresolved reference: " + reference);
                node = AsMap(current);
            }
            return node;
        }

        private static string Nullable(string type) => type.EndsWith("?", StringComparison.Ordinal) ? type : type + "?";
        private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
        public static string ProjectIdentifier(string value) => Identifier(value);

        private static string Identifier(string value)
        {
            var result = new StringBuilder();
            bool capitalize = true;
            foreach (var ch in value)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '_') { capitalize = true; continue; }
                if (result.Length == 0 && char.IsDigit(ch)) result.Append('_');
                result.Append(capitalize ? char.ToUpperInvariant(ch) : ch);
                capitalize = false;
            }
            if (result.Length == 0) result.Append("Generated");
            var name = result.ToString();
            return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "_" + name : name;
        }

        private static YamlMappingNode AsMap(YamlNode node) => node as YamlMappingNode ?? throw new InvalidOperationException("Expected an object.");
        private static YamlNode? Get(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
        private static YamlMappingNode Map(YamlMappingNode node, string key) => Get(node, key) is YamlMappingNode map ? map : new YamlMappingNode();
        private static string Value(YamlMappingNode node, string key, string fallback = "") => Get(node, key) is YamlScalarNode scalar ? scalar.Value ?? fallback : fallback;
        private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value ?? "";
        private static IEnumerable<KeyValuePair<string, YamlNode>> Entries(YamlMappingNode node) => node.Children.Select(pair => new KeyValuePair<string, YamlNode>(Scalar(pair.Key), pair.Value));
        private static IEnumerable<YamlNode> Sequence(YamlMappingNode node, string key) => Get(node, key) is YamlSequenceNode sequence ? sequence.Children : Enumerable.Empty<YamlNode>();
    }
}

internal static class CollectionExtensions
{
    public static HashSet<T> ToHashSetCompat<T>(this IEnumerable<T> values) => new HashSet<T>(values);
}










