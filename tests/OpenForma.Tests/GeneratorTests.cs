using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace OpenForma.Tests;

[TestClass]
public sealed class GeneratorTests
{
    private const string Spec = """
        openapi: 3.0.3
        info:
          title: Pets
          version: 1.0.0
        paths:
          /pets/{id}:
            parameters:
              - name: id
                in: path
                required: true
                schema: { type: string }
            get:
              operationId: getPet
              parameters:
                - name: search
                  in: query
                  schema: { type: string }
                - name: X-Trace
                  in: header
                  schema: { type: string }
              responses:
                '200':
                  description: Found
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Pet' }
            put:
              operationId: updatePet
              requestBody:
                required: true
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/Pet' }
              responses:
                '204': { description: Updated }
        components:
          schemas:
            Pet:
              type: object
              required: [id, name]
              properties:
                id: { type: integer, format: int64 }
                name: { type: string }
                parent: { $ref: '#/components/schemas/Pet' }
                tags:
                  type: array
                  items: { type: string }
                birthday: { type: string, format: date }
        """;

    [TestMethod]
    [DataRow("pets.yaml")]
    [DataRow("pets.yml")]
    [DataRow("pets.json")]
    public void JsonAndYamlGenerateCompilableModelsAndClients(string filename)
    {
        var text = filename.EndsWith(".json") ? ToJson(Spec) : Spec;
        var (compilation, result) = Generate(text, filename);
        Assert.IsEmpty(result.Diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
        var source = result.Results.Single().GeneratedSources.Single().SourceText.ToString();
        StringAssert.Contains(source, "class Pet");
        StringAssert.Contains(source, "Pet? Parent");
        StringAssert.Contains(source, "GetPetAsync");
        StringAssert.Contains(source, "UpdatePetAsync");
    }

    [TestMethod]
    public async Task GeneratedClientEscapesParametersDeserializesModelsAndSendsJson()
    {
        var assembly = Compile(Spec);
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/api/") };
        var clientType = assembly.GetType("Company.PetConsumer.PetConsumerClient")!;
        var client = Activator.CreateInstance(clientType, http)!;
        var task = (Task)clientType.GetMethod("GetPetAsync")!.Invoke(client, ["a/b", "red & blue", "trace-1", CancellationToken.None])!;
        await task;
        var pet = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.AreEqual("https://example.test/api/pets/a%2Fb?search=red%20%26%20blue", handler.Uri!.AbsoluteUri);
        Assert.AreEqual("trace-1", handler.Trace);
        Assert.AreEqual("Milo", pet.GetType().GetProperty("Name")!.GetValue(pet));
        Assert.AreEqual(42L, pet.GetType().GetProperty("Id")!.GetValue(pet));

        handler.StatusCode = System.Net.HttpStatusCode.NoContent;
        await (Task)clientType.GetMethod("UpdatePetAsync")!.Invoke(client, ["42", pet, CancellationToken.None])!;
        Assert.AreEqual(HttpMethod.Put, handler.Method);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual("Milo", body.RootElement.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task GeneratedClientPropagatesHttpErrors()
    {
        var assembly = Compile(Spec);
        using var http = new HttpClient(new RecordingHandler { StatusCode = System.Net.HttpStatusCode.BadRequest })
        { BaseAddress = new Uri("https://example.test/") };
        var type = assembly.GetType("Company.PetConsumer.PetConsumerClient")!;
        var client = Activator.CreateInstance(type, http)!;
        var task = (Task)type.GetMethod("GetPetAsync")!.Invoke(client, ["42", null, null, CancellationToken.None])!;
        await Assert.ThrowsAsync<HttpRequestException>(async () => await task);
    }

    [TestMethod]
    [DataRow("openapi: 3.1.0", "Only OpenAPI 3.0")]
    [DataRow("openapi: [", "")]
    [DataRow("openapi: 3.0.3\ncomponents:\n  schemas:\n    Broken:\n      type: object\n      allOf: []", "allOf")]
    public void InvalidOrUnsupportedSpecificationsReportDiagnostic(string text, string expected)
    {
        var (_, result) = Generate(text);
        var diagnostic = result.Diagnostics.Single();
        Assert.AreEqual("OAG001", diagnostic.Id);
        StringAssert.Contains(diagnostic.GetMessage(), expected);
        Assert.IsEmpty(result.Results.Single().GeneratedSources);
    }

    [TestMethod]
    public void ExternalReferencesReportDiagnostic()
    {
        var (_, result) = Generate(Spec.Replace("#/components/schemas/Pet", "other.yaml#/Pet"));
        Assert.AreEqual("OAG001", result.Diagnostics.Single().Id);
    }

    [TestMethod]
    public void UnrelatedAdditionalFilesAreIgnored()
    {
        var (_, result) = Generate("not openapi", "notes.txt");
        Assert.IsEmpty(result.Diagnostics);
        Assert.IsEmpty(result.Results.Single().GeneratedSources);
    }

    [TestMethod]
    public void InlineObjectsAndTypedDictionariesCompile()
    {
        var spec = Spec.Replace("birthday: { type: string, format: date }", """
            birthday: { type: string, format: date }
                    metadata:
                      type: object
                      additionalProperties: { type: string }
                    address:
                      type: object
                      properties:
                        street: { type: string }
            """);
        var (compilation, result) = Generate(spec);
        Assert.IsEmpty(result.Diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
    }

    [TestMethod]
    public async Task DateParametersUseIsoFormat()
    {
        const string spec = """
            openapi: 3.0.3
            paths:
              /reports/{day}:
                get:
                  operationId: report
                  parameters:
                    - name: day
                      in: path
                      required: true
                      schema: { type: string, format: date }
                    - name: since
                      in: query
                      schema: { type: string, format: date-time }
                  responses:
                    '204': { description: Empty }
            """;
        var assembly = Compile(spec);
        var handler = new RecordingHandler { StatusCode = System.Net.HttpStatusCode.NoContent };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var type = assembly.GetType("Company.PetConsumer.PetConsumerClient")!;
        var client = Activator.CreateInstance(type, http)!;
        await (Task)type.GetMethod("ReportAsync")!.Invoke(client, [new DateOnly(2026, 10, 1), null, CancellationToken.None])!;
        Assert.AreEqual("https://example.test/reports/2026-10-01", handler.Uri!.AbsoluteUri);
        var timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await (Task)type.GetMethod("ReportAsync")!.Invoke(client, [new DateOnly(2026, 10, 1), timestamp, CancellationToken.None])!;
        StringAssert.Contains(handler.Uri!.AbsoluteUri, "since=2026-10-01T12%3A00%3A00.0000000%2B00%3A00");
    }

    [TestMethod]
    public void NamedRecursiveDictionaryCompiles()
    {
        const string spec = """
            openapi: 3.0.3
            components:
              schemas:
                Tree:
                  type: object
                  additionalProperties: { $ref: '#/components/schemas/Tree' }
            """;
        var (compilation, result) = Generate(spec);
        Assert.IsEmpty(result.Diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
        StringAssert.Contains(result.Results.Single().GeneratedSources.Single().SourceText.ToString(),
            "class Tree : Dictionary<string, Tree>");
    }
    internal static Assembly Compile(string text)
    {
        var (compilation, result) = Generate(text);
        Assert.IsEmpty(result.Diagnostics);
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }

    internal static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(string text, string path = "pets.yaml", string? rootNamespace = "Company.PetConsumer", string? projectName = "PetConsumer", string? assemblyName = null, string? generationMode = null, string? additionalSource = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(assemblyName ?? "Generated_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: additionalSource == null ? [] : [CSharpSyntaxTree.ParseText(additionalSource)],
            references: references, options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OpenApiSourceGenerator().AsSourceGenerator()],
            additionalTexts: [new TextFile(path, text)],
            optionsProvider: new ProjectOptions(rootNamespace, projectName, generationMode));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        return (updated, driver.GetRunResult());
    }

    private static string ToJson(string yaml)
    {
        var value = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(yaml);
        return JsonSerializer.Serialize(value);
    }

    internal sealed class ProjectOptions(string? rootNamespace, string? projectName, string? generationMode = null) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(rootNamespace, projectName, generationMode);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options(null, null);
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(null, null);
        private sealed class Options(string? rootNamespace, string? projectName, string? generationMode = null) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                var result = key == "build_property.RootNamespace" ? rootNamespace
                    : key == "build_property.MSBuildProjectName" ? projectName
                    : key == "build_property.OpenApiGenerationMode" ? generationMode : null;
                value = result ?? "";
                return result != null;
            }
        }
    }
    private sealed class TextFile(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Trace { get; private set; }
        public string? Body { get; private set; }
        public System.Net.HttpStatusCode StatusCode { get; set; } = System.Net.HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Method = request.Method;
            Trace = request.Headers.TryGetValues("X-Trace", out var values) ? values.Single() : null;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent("""{"id":42,"name":"Milo"}""", Encoding.UTF8, "application/json")
            };
        }
    }
}




