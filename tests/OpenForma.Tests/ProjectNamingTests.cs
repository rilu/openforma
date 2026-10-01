using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace OpenForma.Tests;

[TestClass]
public sealed class ProjectNamingTests
{
    private const string Spec = """
        openapi: 3.0.3
        paths:
          /pets:
            get:
              operationId: listPets
              responses:
                '200':
                  description: Pets
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Pet' }
        components:
          schemas:
            Pet:
              type: object
              properties:
                name: { type: string }
        """;

    [TestMethod]
    [DataRow("Company.Orders", "Orders.Service", "OrdersServiceClient")]
    [DataRow("company.orders", "orders-service", "OrdersServiceClient")]
    [DataRow("Company.@class", "123-Orders", "_123OrdersClient")]
    [DataRow("Company.class", "Orders", "OrdersClient")]
    public void NamespaceAndClientUseConsumingProjectProperties(string rootNamespace, string projectName, string clientName)
    {
        var (compilation, result) = GeneratorTests.Generate(Spec, "unrelated-file.yaml", rootNamespace, projectName, "Different.Assembly");
        Assert.IsEmpty(result.Diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
        var metadataNamespace = rootNamespace.Replace("@", "");
        Assert.IsNotNull(compilation.GetTypeByMetadataName(metadataNamespace + "." + clientName));
        Assert.IsNotNull(compilation.GetTypeByMetadataName(metadataNamespace + ".Pet"));
        Assert.DoesNotContain("OpenApi.Generated", result.Results.Single().GeneratedSources.Single().SourceText.ToString());
    }

    [TestMethod]
    public void SpecificationFilenameDoesNotChangeGeneratedTypeNames()
    {
        var (_, first) = GeneratorTests.Generate(Spec, "pets.yaml");
        var (_, second) = GeneratorTests.Generate(Spec, "renamed-openapi.json");
        Assert.AreEqual(first.Results.Single().GeneratedSources.Single().SourceText.ToString(),
            second.Results.Single().GeneratedSources.Single().SourceText.ToString());
    }

    [TestMethod]
    public void MissingProjectMetadataFallsBackToConsumingAssembly()
    {
        var (compilation, result) = GeneratorTests.Generate(Spec, rootNamespace: null, projectName: null, assemblyName: "Fallback.Consumer");
        Assert.IsEmpty(result.Diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
        Assert.IsNotNull(compilation.GetTypeByMetadataName("Fallback.Consumer.FallbackConsumerClient"));
    }

    [TestMethod]
    [DataRow("Company..Orders")]
    [DataRow("Company.bad-name")]
    public void InvalidRootNamespaceReportsDiagnostic(string rootNamespace)
    {
        var (_, result) = GeneratorTests.Generate(Spec, rootNamespace: rootNamespace);
        Assert.AreEqual("OAG001", result.Diagnostics.Single().Id);
        StringAssert.Contains(result.Diagnostics.Single().GetMessage(), "RootNamespace");
    }

    [TestMethod]
    public void ProjectPropertyChangesUpdateIncrementalOutput()
    {
        var (existing, _) = GeneratorTests.Generate(Spec);
        var compilation = CSharpCompilation.Create("Consumer", references: existing.References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OpenApiSourceGenerator().AsSourceGenerator()],
            additionalTexts: [new SpecFile("api.yaml")],
            optionsProvider: new GeneratorTests.ProjectOptions("Company.First", "First"));
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(driver.GetRunResult().Results.Single().GeneratedSources.Single().SourceText.ToString(), "class FirstClient");

        driver = driver.WithUpdatedAnalyzerConfigOptions(new GeneratorTests.ProjectOptions("Company.Second", "Second"));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        var result = driver.GetRunResult();
        Assert.IsEmpty(result.Diagnostics);
        var source = result.Results.Single().GeneratedSources.Single().SourceText.ToString();
        StringAssert.Contains(source, "namespace Company.Second");
        StringAssert.Contains(source, "class SecondClient");
        Assert.DoesNotContain("class FirstClient", source);
        Assert.IsEmpty(updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
    }

    [TestMethod]
    public void MultipleSpecificationsReportDiagnosticInsteadOfDuplicateProjectTypes()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OpenApiSourceGenerator().AsSourceGenerator()],
            additionalTexts: [new SpecFile("one.yaml"), new SpecFile("two.json")],
            optionsProvider: new GeneratorTests.ProjectOptions("Company.Consumer", "Consumer"));
        driver = driver.RunGenerators(CSharpCompilation.Create("Consumer"));
        var result = driver.GetRunResult();
        Assert.AreEqual("OAG001", result.Diagnostics.Single().Id);
        StringAssert.Contains(result.Diagnostics.Single().GetMessage(), "one OpenAPI specification");
        Assert.IsEmpty(result.Results.Single().GeneratedSources);
    }

    private sealed class SpecFile(string path) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(Spec);
    }
}


