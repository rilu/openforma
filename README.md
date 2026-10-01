# OpenForma

Compile-time C# models, typed HTTP clients, and abstract ASP.NET Core controllers from an OpenAPI JSON or YAML file.
OpenForma.slnx uses the .NET 10 SDK and contains OpenForma, OpenForma.Tests, and the Pets.Client/Pets.Server examples in solution folders matching src, tests, and examples.
The generator targets netstandard2.0 for compatibility with Roslyn compiler hosts; generated code is intended for .NET 10 consumers.

## Build, test, and package

    dotnet build
    dotnet test
    dotnet pack src/OpenForma -c Release -o artifacts/packages

The package is local until you publish it to a NuGet feed. It contains the generator and YAML parser under
analyzers/dotnet/cs, and MSBuild integration under buildTransitive. No runtime generator dependency is needed.
The generator uses Roslyn incremental generation and reads compiler AdditionalFiles.

## Use in a .NET 10 project

Install the package from your feed, then add the specification to your project:

~~~xml
<ItemGroup>
  <PackageReference Include="OpenForma" Version="0.1.0" PrivateAssets="all" />
  <OpenApiReference Include="OpenApi/pets.yaml" />
</ItemGroup>
~~~

JSON, .yaml, and .yml are supported. An explicit AdditionalFiles item also works.
Only files supplied this way are read; ordinary JSON/YAML content files are unaffected.
Do not register the same file through both item types. Supply one OpenAPI specification per consuming project; multiple specifications produce an OAG001 diagnostic.

For a consuming project named Orders.Service.csproj with RootNamespace set to Company.Orders,
examples/pets.yaml generates its types directly in Company.Orders and its client is OrdersServiceClient:

~~~xml
<PropertyGroup>
  <RootNamespace>Company.Orders</RootNamespace>
</PropertyGroup>
~~~

~~~csharp
using Company.Orders;

using var http = new HttpClient
{
    BaseAddress = new Uri("https://api.example.com/v1/")
};
var client = new OrdersServiceClient(http);
Pet? pet = await client.GetPetAsync(42, cancellationToken);
~~~

The namespace comes from the consuming project's RootNamespace. The client name comes from
MSBuildProjectName (the .csproj filename without its extension), suffixed with Client. Separators in the project
name become word boundaries; Orders.Service and orders-service both produce OrdersServiceClient.
Model names still come from OpenAPI schemas. Renaming the specification file does not change generated types.

The NuGet targets expose these properties automatically through CompilerVisibleProperty.
If project metadata is unavailable (for example, a manually configured analyzer reference), the consuming
compilation's assembly name supplies the fallback namespace and client name. For a direct analyzer project
reference, expose RootNamespace and MSBuildProjectName as CompilerVisibleProperty items to use the project values.
Changing AssemblyName does not override an available MSBuildProjectName. Server controllers use the same project naming with an AbstractController suffix. Invalid namespace settings report OAG001.
operationId determines the method name, suffixed with Async; without operationId, the HTTP verb and path are used.
Wire property names are preserved with JsonPropertyName.

Supply an HttpClient with BaseAddress (including a trailing slash). Configure authentication, default headers,
timeouts, handlers, retries, or dependency injection on that client. Generated clients do not own or dispose it.
Paths are relative to BaseAddress; the specification's servers entries do not configure the client.
Methods accept CancellationToken, escape path/query values, serialize JSON request bodies,
deserialize responses, dispose requests/responses, and throw ApiException on HTTP errors.
Optional parameter/body values are nullable arguments; pass null to omit them.
Methods returning models return nullable results to accommodate HTTP 204 and empty content.

To inspect generated code:

~~~xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
~~~

## Generate an ASP.NET Core server

Set OpenApiGenerationMode to Server in the consuming project. Client is the default when the property is absent.

~~~xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Company.Orders</RootNamespace>
    <OpenApiGenerationMode>Server</OpenApiGenerationMode>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="OpenForma" Version="0.1.0" PrivateAssets="all" />
    <OpenApiReference Include="OpenApi/pets.yaml" />
  </ItemGroup>
</Project>
~~~

For Orders.Service.csproj this generates OrdersServiceAbstractController and the specification's models in
Company.Orders. Server mode emits the abstract controller and models; Client mode emits the HTTP client and models.
A server class library can use Microsoft.NET.Sdk with a FrameworkReference to Microsoft.AspNetCore.App.
A missing ASP.NET Core reference or an invalid generation mode produces OAG001.

The abstract controller derives from ControllerBase and has ApiController. Each operation has a public action
with the specified HTTP verb and absolute route, and a protected abstract CoreAsync method for your implementation.
Binding and route attributes stay on the inherited public actions, so implementations only provide business logic.
For the example pets.yaml, the generated action forwards GetPetAsync(long Id, CancellationToken) to
GetPetCoreAsync(long Id, CancellationToken).

~~~csharp
using Microsoft.AspNetCore.Mvc;

namespace Company.Orders;

public sealed class PetsController : OrdersServiceAbstractController
{
    protected override Task<ActionResult<Pet>> GetPetCoreAsync(long Id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionResult<Pet> result = Id == 42
            ? new Pet { Id = Id, Name = "Milo" }
            : new ObjectResult(new Problem { Title = "Pet not found", Status = 404 })
            {
                StatusCode = 404,
                ContentTypes = { "application/problem+json" }
            };
        return Task.FromResult(result);
    }
}
~~~

Register and map controllers in your application:

~~~csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var app = builder.Build();
app.MapControllers();
app.Run();
~~~

Generated server actions include:

- FromRoute, FromQuery, and FromHeader with the original wire names. Required scalar parameters use
  BindRequired and Required; optional parameters are nullable. Standard MVC validation handles missing/invalid input.
- FromBody with JSON consumption. Required bodies disallow empty content; optional bodies allow empty content
  and use a nullable model. Unsupported request media types receive ASP.NET Core's normal 415 response.
- ProducesResponseType for exact success/error codes, including types and media types, and
  ProducesDefaultResponseType for default responses. Range response schemas generate models, but MVC response
  metadata has no status-range representation, so ranges are not assigned a synthetic numeric code.
- Task<ActionResult<T>> when declared 2xx responses share one body type, or Task<IActionResult> when they have
  different body types or no body. The implementation chooses Ok, Created, Accepted, NoContent, error results,
  and response headers as appropriate.
- CancellationToken passed to the CoreAsync implementation.

Response metadata describes the contract; it does not restrict what an implementation returns.
Set the status, body, and media type to match the specification. Security schemes, authentication,
authorization policies, servers/base-path configuration, and schema validation constraints are not implemented
by the generated controller. Configure those on your ASP.NET Core application or derived controller.
Routes use the specification's paths, independent of the concrete controller name.

The solution includes runnable [client](examples/Pets.Client) and [server](examples/Pets.Server) examples.
Both use the local OpenForma project by default and build with the solution.
Their namespaces are OpenForma.Examples.Client and OpenForma.Examples.Server.

    dotnet run --project examples/Pets.Client
    dotnet run --project examples/Pets.Server -- --verify

Remove --verify to run the web server. To verify the packed NuGet instead of the project reference:

    dotnet pack src/OpenForma -c Release -o artifacts/packages
    dotnet restore examples/Pets.Server -p:UseOpenFormaPackage=true --source artifacts/packages --packages artifacts/nuget-openforma-example
    dotnet run --project examples/Pets.Server -p:UseOpenFormaPackage=true --no-restore -- --verify

The client example supports the same UseOpenFormaPackage switch.
Restore from your configured feed when using a published package.
For ASP.NET Core controller behavior, see [controller routing](https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/routing?view=aspnetcore-10.0)
and [action return types](https://learn.microsoft.com/en-us/aspnet/core/web-api/action-return-types?view=aspnetcore-10.0).
## Handle different HTTP responses

In Client mode (the default), every operation generates two methods:

- FetchAsync is the convenience method. A single success body type keeps the original nullable model return
  type; bodyless successes return Task. Different success body types (or a specification with only default/error
  responses) return the operation response object. Non-2xx responses throw ApiException<FetchResponse>,
  which derives from HttpRequestException and includes StatusCode, Response, RawBody, and Headers.
- FetchWithResponseAsync always returns FetchResponse for a received HTTP response, including errors.
  It has StatusCode, IsSuccessStatusCode, MatchedResponse, RawBody, Headers, Body, DeserializationError, and
  nullable typed payload properties named Response200, Response201, Response404, Response4XX, ResponseDefault,
  etc., for the statuses with declared bodies. Names follow the operationId.

For a specification declaring 200 with a Pet, 404 with an error model, and 429 via a 4XX range:

~~~csharp
var response = await client.GetPetWithResponseAsync(42, cancellationToken);
switch (response.StatusCode)
{
    case System.Net.HttpStatusCode.OK:
        Console.WriteLine(response.Response200?.Name);
        break;
    case System.Net.HttpStatusCode.NotFound:
        Console.WriteLine(response.Response404?.Title);
        break;
    case System.Net.HttpStatusCode.TooManyRequests:
        if (response.Headers.TryGetValue("Retry-After", out var retryAfter))
            Console.WriteLine(string.Join(", ", retryAfter));
        break;
    default:
        Console.WriteLine($"HTTP {(int)response.StatusCode}: {response.RawBody}");
        break;
}
~~~

The specification determines which typed response properties exist. Headers are copied with case-insensitive
keys and remain available after the underlying HttpResponseMessage is disposed. This includes Location and
Retry-After. Response matching checks exact codes first, then ranges such as 2XX/4XX/5XX, then default,
following [OpenAPI response definitions](https://spec.openapis.org/oas/v3.0.3#responses-object).

201 Created and 202 Accepted are successful. 204 No Content, 205 Reset Content, 304 Not Modified,
and HEAD requests skip body reading/deserialization. A zero-length body, including a response with no
Content-Length, produces a null typed payload. 304 remains outside the 2xx success range; use WithResponseAsync
to handle it as a cache outcome. A received redirect is also available through WithResponseAsync;
disable automatic redirects on your HttpClientHandler if you want to see it.

Undocumented statuses retain the raw body, headers, and status with MatchedResponse set to null.
Convenience methods reject undocumented statuses (except 204/205), rather than assuming a response schema.
If a default response on a 2xx status has a different body type from the convenience method's return type,
use WithResponseAsync to receive that payload.

Malformed JSON or an unexpected Content-Type populates DeserializationError without losing the received
response. Convenience methods throw ApiException with that error as InnerException. The server must send a
declared response media type; when Content-Type is absent, the first declared media type is assumed.
Network failures and cancellation still throw from both methods. Configure retry policies on the supplied
HttpClient, including how to interpret Retry-After for 429/503.

~~~csharp
try
{
    var pet = await client.GetPetAsync(42, cancellationToken);
}
catch (ApiException<GetPetResponse> exception)
{
    Console.WriteLine($"HTTP {(int)exception.Response.StatusCode}: {exception.RawBody}");
    // exception.Response has the typed payload properties for this operation.
}
~~~
## Initial supported scope

- OpenAPI 3.0.x, JSON and YAML, local component references.
- Object models, recursive references, nested objects, arrays, typed dictionaries.
- Strings, booleans, integers, numbers, dates, timestamps, UUIDs, and base64 byte properties.
- Required/optional and nullable model properties. Required properties have non-nullable C# types;
  schema validation constraints and required presence are not enforced at runtime.
- String enums are represented as strings.
- HTTP operations with scalar path, query, and header parameters.
- application/json request bodies; JSON (including application/problem+json) and text/plain response bodies.
- Different typed success/error responses, response references, exact status codes, status ranges, and default responses.
- Response status, raw bodies, and copied response/content headers.

This is a starting implementation, not a complete OpenAPI implementation.
OpenAPI 3.1/Swagger 2.0, external references, composition (allOf/oneOf/anyOf),
discriminators, binary bodies, cookies, complex parameter serialization, and explicit serialization styles
produce OAG001 build errors. Operations need at least one declared response. Multiple media types on one response must use the same schema; other response media types are currently unsupported. Schema constraints such as enum, pattern, bounds, readOnly/writeOnly, and defaults
are not enforced. Extra properties of models with named properties are not retained.
Response headers are captured by name; typed header schemas, links, callbacks, and security flows are not generated.
Authentication belongs on the supplied HttpClient.

## Tests

MSTest compiles generated JSON/YAML sources with Roslyn and executes generated clients against an
in-memory HTTP handler. Tests cover escaping, headers, JSON bodies, deserialization, HTTP failures,
recursive/nested models, dictionaries, ignored files, and diagnostics. Response tests also execute distinct success/error payloads, status matching, problem JSON and text, headers, malformed/empty bodies, cancellation, and transport failures. Server tests compile concrete controller implementations and execute the ASP.NET Core MVC pipeline to verify inherited actions, binding, required/optional bodies, validation, and success/error serialization.





