using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace OpenForma.Tests;

[TestClass]
public sealed class ServerGenerationTests
{
    private const string Spec = """
        openapi: 3.0.3
        paths:
          /pets/{id}:
            parameters:
              - name: id
                in: path
                required: true
                schema: { type: integer, format: int64 }
            get:
              operationId: getPet
              parameters:
                - name: search
                  in: query
                  schema: { type: string }
                - name: X-Token
                  in: header
                  required: true
                  schema: { type: string }
              responses:
                '200':
                  description: Found
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Pet' }
                '404':
                  description: Missing
                  content:
                    application/problem+json:
                      schema: { $ref: '#/components/schemas/Problem' }
                '4XX':
                  description: Error
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Problem' }
                default:
                  description: Unexpected
                  content:
                    application/problem+json:
                      schema: { $ref: '#/components/schemas/Problem' }
            post:
              operationId: createPet
              requestBody:
                required: true
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/Pet' }
              responses:
                '200':
                  description: Updated
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Pet' }
                '201':
                  description: Created
                  content:
                    application/json:
                      schema:
                        type: object
                        properties:
                          receipt: { type: string }
            delete:
              operationId: deletePet
              responses:
                '204': { description: Deleted }
        components:
          schemas:
            Pet:
              type: object
              required: [id, name]
              properties:
                id: { type: integer, format: int64 }
                name: { type: string }
            Problem:
              type: object
              properties:
                title: { type: string }
        """;

    private const string Implementation = """
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Mvc;
        namespace Company.PetConsumer;
        public sealed class PetsController : PetConsumerAbstractController
        {
            protected override Task<ActionResult<Pet>> GetPetCoreAsync(long Id, string? Search, string XToken, CancellationToken cancellationToken)
            {
                ActionResult<Pet> result = Id == 404
                    ? NotFound(new Problem { Title = "Not found" })
                    : Ok(new Pet { Id = Id, Name = (Search ?? "Milo") + ":" + XToken });
                return Task.FromResult(result);
            }
            protected override Task<IActionResult> CreatePetCoreAsync(long Id, Pet body, CancellationToken cancellationToken)
                => Task.FromResult<IActionResult>(Created("/pets/" + Id, new { receipt = body.Name }));
            protected override Task<IActionResult> DeletePetCoreAsync(long Id, CancellationToken cancellationToken)
                => Task.FromResult<IActionResult>(NoContent());
        }
        """;

    private static readonly Lazy<Assembly> Assembly = new(CompileServer);

    [TestMethod]
    public void ServerModeSupportsJsonAndConsumingProjectNames()
    {
        var data = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(Spec);
        var json = JsonSerializer.Serialize(data);
        var (compilation, result) = GeneratorTests.Generate(json, "unrelated.json",
            rootNamespace: "Contoso.Orders", projectName: "Orders.Service", assemblyName: "Different.Assembly",
            generationMode: "Server");
        AssertCompiles(compilation, result);
        Assert.IsNotNull(compilation.GetTypeByMetadataName("Contoso.Orders.OrdersServiceAbstractController"));
        Assert.IsNotNull(compilation.GetTypeByMetadataName("Contoso.Orders.Pet"));
    }

    [TestMethod]
    public void ChangingModeUpdatesIncrementalOutput()
    {
        var (existing, _) = GeneratorTests.Generate(Spec);
        var compilation = CSharpCompilation.Create("Consumer", references: existing.References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OpenApiSourceGenerator().AsSourceGenerator()],
            additionalTexts: [new SpecificationFile()],
            optionsProvider: new GeneratorTests.ProjectOptions("Company.PetConsumer", "PetConsumer", "Client"));
        driver = driver.RunGenerators(compilation);
        driver = driver.WithUpdatedAnalyzerConfigOptions(new GeneratorTests.ProjectOptions("Company.PetConsumer", "PetConsumer", "Server"));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var server, out _);
        AssertCompiles(server, driver.GetRunResult());
        Assert.IsNotNull(server.GetTypeByMetadataName("Company.PetConsumer.PetConsumerAbstractController"));
        Assert.IsNull(server.GetTypeByMetadataName("Company.PetConsumer.PetConsumerClient"));
        driver = driver.WithUpdatedAnalyzerConfigOptions(new GeneratorTests.ProjectOptions("Company.PetConsumer", "PetConsumer", "Client"));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var client, out _);
        AssertCompiles(client, driver.GetRunResult());
        Assert.IsNotNull(client.GetTypeByMetadataName("Company.PetConsumer.PetConsumerClient"));
        Assert.IsNull(client.GetTypeByMetadataName("Company.PetConsumer.PetConsumerAbstractController"));
    }
    [TestMethod]
    [DataRow("Server")]
    [DataRow("server")]
    public void ServerModeGeneratesAbstractControllerAndModels(string mode)
    {
        var (compilation, result) = GeneratorTests.Generate(Spec, generationMode: mode);
        AssertCompiles(compilation, result);
        var controller = compilation.GetTypeByMetadataName("Company.PetConsumer.PetConsumerAbstractController")!;
        Assert.IsTrue(controller.IsAbstract);
        Assert.AreEqual("Microsoft.AspNetCore.Mvc.ControllerBase", controller.BaseType!.ToDisplayString());
        Assert.IsNotNull(compilation.GetTypeByMetadataName("Company.PetConsumer.Pet"));
        Assert.IsNotNull(compilation.GetTypeByMetadataName("Company.PetConsumer.Problem"));
        Assert.IsNotNull(compilation.GetTypeByMetadataName("Company.PetConsumer.CreatePetResponse201Body"));
        Assert.IsNull(compilation.GetTypeByMetadataName("Company.PetConsumer.PetConsumerClient"));
        Assert.IsNull(compilation.GetTypeByMetadataName("Company.PetConsumer.ApiResponse"));
        Assert.IsNull(compilation.GetTypeByMetadataName("Company.PetConsumer.ApiException"));
    }

    [TestMethod]
    public void DerivedControllerDiscoversInheritedActionsAndBindingMetadata()
    {
        using var provider = Services();
        var actions = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().ToArray();
        Assert.HasCount(3, actions);
        var get = actions.Single(a => a.MethodInfo.Name == "GetPetAsync");
        Assert.AreEqual("PetsController", get.ControllerTypeInfo.Name);
        Assert.AreEqual("pets/{id}", get.AttributeRouteInfo!.Template);
        Assert.AreEqual("id", get.Parameters.Single(p => p.Name == "Id").BindingInfo!.BinderModelName);
        Assert.AreEqual("search", get.Parameters.Single(p => p.Name == "Search").BindingInfo!.BinderModelName);
        Assert.AreEqual("X-Token", get.Parameters.Single(p => p.Name == "XToken").BindingInfo!.BinderModelName);
        Assert.IsTrue(get.MethodInfo.GetCustomAttributes<ProducesResponseTypeAttribute>().Any(a => a.StatusCode == 404 && a.Type.Name == "Problem"));
        Assert.IsNotNull(get.MethodInfo.GetCustomAttribute<ProducesDefaultResponseTypeAttribute>());
        Assert.IsFalse(actions.Any(a => a.MethodInfo.Name.EndsWith("CoreAsync")));
    }

    [TestMethod]
    public async Task MvcBindsRouteQueryHeaderAndSerializesSuccess()
    {
        var (status, body, _) = await Invoke("GetPetAsync", "GET", "42", "?search=red%20fox", token: "abc");
        Assert.AreEqual(200, status);
        using var document = JsonDocument.Parse(body);
        Assert.AreEqual(42, document.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual("red fox:abc", document.RootElement.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task MvcReturnsDeclaredErrorPayload()
    {
        var (status, body, _) = await Invoke("GetPetAsync", "GET", "404", token: "abc");
        Assert.AreEqual(404, status);
        using var document = JsonDocument.Parse(body);
        Assert.AreEqual("Not found", document.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task MvcRejectsMissingRequiredHeader()
    {
        var (status, _, _) = await Invoke("GetPetAsync", "GET", "42");
        Assert.AreEqual(400, status);
    }

    [TestMethod]
    public async Task MvcBindsBodyAndReturnsCreatedWithLocation()
    {
        var (status, body, location) = await Invoke("CreatePetAsync", "POST", "42", requestBody: """{"id":42,"name":"Milo"}""");
        Assert.AreEqual(201, status);
        Assert.AreEqual("/pets/42", location);
        using var document = JsonDocument.Parse(body);
        Assert.AreEqual("Milo", document.RootElement.GetProperty("receipt").GetString());
    }

    [TestMethod]
    public async Task MvcRejectsMissingRequiredBody()
    {
        var (status, _, _) = await Invoke("CreatePetAsync", "POST", "42", requestBody: "");
        Assert.AreEqual(400, status);
    }

    [TestMethod]
    public async Task MvcReturnsNoContent()
    {
        var (status, body, _) = await Invoke("DeletePetAsync", "DELETE", "42");
        Assert.AreEqual(204, status);
        Assert.AreEqual("", body);
    }

    [TestMethod]
    [DataRow("get", "HttpGetAttribute")]
    [DataRow("post", "HttpPostAttribute")]
    [DataRow("put", "HttpPutAttribute")]
    [DataRow("patch", "HttpPatchAttribute")]
    [DataRow("delete", "HttpDeleteAttribute")]
    [DataRow("head", "HttpHeadAttribute")]
    [DataRow("options", "HttpOptionsAttribute")]
    [DataRow("trace", "AcceptVerbsAttribute")]
    public void ServerSupportsHttpVerbs(string verb, string attribute)
    {
        var spec = "openapi: 3.0.3\npaths:\n  /test:\n    " + verb + ":\n      responses:\n        '204': { description: Done }";
        var (compilation, result) = GeneratorTests.Generate(spec, generationMode: "Server");
        AssertCompiles(compilation, result);
        var controller = compilation.GetTypeByMetadataName("Company.PetConsumer.PetConsumerAbstractController")!;
        Assert.IsTrue(controller.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a => a.AttributeClass!.Name == attribute)));
    }

    [TestMethod]
    public void OptionalBodyAllowsEmptyRequestAndIsNullable()
    {
        var (compilation, result) = GeneratorTests.Generate(Spec.Replace("requestBody:\n        required: true", "requestBody:\n        required: false"), generationMode: "Server");
        AssertCompiles(compilation, result);
        var source = result.Results.Single().GeneratedSources.Single().SourceText.ToString();
        StringAssert.Contains(source, "EmptyBodyBehavior.Allow");
        StringAssert.Contains(source, "Pet? body");
    }

    [TestMethod]
    public async Task MvcAcceptsOptionalEmptyBody()
    {
        var optionalSpec = Spec.Replace("requestBody:\n        required: true", "requestBody:\n        required: false");
        var optionalImplementation = Implementation.Replace("long Id, Pet body,", "long Id, Pet? body,")
            .Replace("receipt = body.Name", "receipt = body?.Name ?? \"empty\"");
        var (compilation, result) = GeneratorTests.Generate(optionalSpec, generationMode: "Server", additionalSource: optionalImplementation);
        AssertCompiles(compilation, result);
        using var stream = new MemoryStream();
        Assert.IsTrue(compilation.Emit(stream).Success);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var (status, body, _) = await Invoke("CreatePetAsync", "POST", "42", requestBody: "", assembly: assembly);
        Assert.AreEqual(201, status);
        StringAssert.Contains(body, "empty");
    }
    [TestMethod]
    public void InvalidModeReportsDiagnostic()
    {
        var (_, result) = GeneratorTests.Generate(Spec, generationMode: "Invalid");
        Assert.AreEqual("OAG001", result.Diagnostics.Single().Id);
        StringAssert.Contains(result.Diagnostics.Single().GetMessage(), "Client or Server");
    }

    [TestMethod]
    public void ServerModeRequiresAspNetCoreReference()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OpenApiSourceGenerator().AsSourceGenerator()],
            additionalTexts: [new SpecificationFile()],
            optionsProvider: new GeneratorTests.ProjectOptions("Company.PetConsumer", "PetConsumer", "Server"));
        driver = driver.RunGenerators(CSharpCompilation.Create("Consumer"));
        var result = driver.GetRunResult();
        Assert.AreEqual("OAG001", result.Diagnostics.Single().Id);
        StringAssert.Contains(result.Diagnostics.Single().GetMessage(), "Microsoft.AspNetCore.App");
    }

    private static Assembly CompileServer()
    {
        var (compilation, result) = GeneratorTests.Generate(Spec, generationMode: "Server", additionalSource: Implementation);
        AssertCompiles(compilation, result);
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return System.Reflection.Assembly.Load(stream.ToArray());
    }

    private static void AssertCompiles(Compilation compilation, GeneratorDriverRunResult result)
    {
        Assert.IsEmpty(result.Diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
    }

    private static ServiceProvider Services(Assembly? assembly = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new System.Diagnostics.DiagnosticListener("Microsoft.AspNetCore"));
        services.AddSingleton<System.Diagnostics.DiagnosticSource>(sp => sp.GetRequiredService<System.Diagnostics.DiagnosticListener>());
        services.AddSingleton<IWebHostEnvironment>(new WebEnvironment());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(sp => sp.GetRequiredService<IWebHostEnvironment>());
        services.AddControllers().AddApplicationPart(assembly ?? Assembly.Value);
        return services.BuildServiceProvider();
    }

    private static async Task<(int Status, string Body, string? Location)> Invoke(string action, string verb, string id,
        string query = "", string? token = null, string? requestBody = null, Assembly? assembly = null)
    {
        using var provider = Services(assembly);
        using var scope = provider.CreateScope();
        var descriptor = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().Single(a => a.MethodInfo.Name == action);
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.Method = verb;
        httpContext.Request.Path = "/pets/" + id;
        httpContext.Request.QueryString = new QueryString(query);
        if (token != null) httpContext.Request.Headers["X-Token"] = token;
        if (requestBody != null)
        {
            var bytes = Encoding.UTF8.GetBytes(requestBody);
            httpContext.Request.Body = new MemoryStream(bytes);
            httpContext.Request.ContentLength = bytes.Length;
            httpContext.Request.ContentType = "application/json";
        }
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;
        var routeData = new RouteData();
        routeData.Values["id"] = id;
        var context = new ActionContext(httpContext, routeData, descriptor);
        var invoker = scope.ServiceProvider.GetRequiredService<IActionInvokerFactory>().CreateInvoker(context)!;
        await invoker.InvokeAsync();
        return (httpContext.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()), httpContext.Response.Headers.Location);
    }

    private sealed class SpecificationFile : AdditionalText
    {
        public override string Path => "pets.yaml";
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(Spec);
    }

    private sealed class WebEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = typeof(ServerGenerationTests).Assembly.GetName().Name!;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}



