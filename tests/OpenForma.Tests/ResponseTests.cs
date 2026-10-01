using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OpenForma.Tests;

[TestClass]
public sealed class ResponseTests
{
    private const string Specification = """
        openapi: 3.0.3
        paths:
          /pets:
            get:
              operationId: fetch
              responses:
                '200':
                  description: Found
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
                '202': { description: Accepted }
                '204': { description: No content }
                '205': { description: Reset content }
                '400':
                  description: Validation failed
                  content:
                    application/problem+json:
                      schema: { $ref: '#/components/schemas/Problem' }
                '404': { $ref: '#/components/responses/NotFound' }
                '4XX':
                  description: Client error
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Problem' }
                '5XX': { description: Server error }
                default:
                  description: Other response
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Problem' }
        components:
          responses:
            NotFound:
              description: Not found
              content:
                application/json:
                  schema:
                    type: object
                    properties:
                      missingId: { type: integer }
          schemas:
            Pet:
              type: object
              properties:
                name: { type: string }
            Problem:
              type: object
              properties:
                title: { type: string }
                status: { type: integer }
        """;

    private static readonly Lazy<Assembly> Generated = new(() => GeneratorTests.Compile(Specification));

    [TestMethod]
    [DataRow(200, """{"name":"Milo"}""", "Response200", "Name", "Milo")]
    [DataRow(201, """{"receipt":"created-1"}""", "Response201", "Receipt", "created-1")]
    public async Task DifferentSuccessfulSchemasHaveSeparateTypedPayloads(int status, string json, string property, string member, string expected)
    {
        using var fixture = new ClientFixture(Generated.Value, status, json);
        var response = await fixture.Call("FetchAsync");
        Assert.AreEqual((HttpStatusCode)status, Get(response, "StatusCode"));
        var payload = Get(response, property)!;
        Assert.AreEqual(expected, Get(payload, member));
        Assert.AreSame(payload, Get(response, "Body"));
    }

    [TestMethod]
    [DataRow(202)]
    [DataRow(204)]
    [DataRow(205)]
    public async Task EmptySuccessfulResponsesDoNotRequireA200Payload(int status)
    {
        using var fixture = new ClientFixture(Generated.Value, status, "");
        var response = await fixture.Call("FetchAsync");
        Assert.IsTrue((bool)Get(response, "IsSuccessStatusCode")!);
        Assert.AreEqual(status.ToString(), Get(response, "MatchedResponse"));
        Assert.IsNull(Get(response, "Body"));
        Assert.IsNull(Get(response, "DeserializationError"));
    }

    [TestMethod]
    [DataRow(400, "application/problem+json", "400", "Response400")]
    [DataRow(401, "application/json", "4XX", "Response4XX")]
    [DataRow(403, "application/json", "4XX", "Response4XX")]
    [DataRow(409, "application/json", "4XX", "Response4XX")]
    [DataRow(429, "application/json", "4XX", "Response4XX")]
    [DataRow(302, "application/json", "default", "ResponseDefault")]
    public async Task DeclaredErrorsAndFallbacksAreTypedWithoutThrowing(int status, string mediaType, string matched, string property)
    {
        const string body = """{"title":"Please try again","status":429}""";
        using var fixture = new ClientFixture(Generated.Value, status, body, mediaType);
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.AreEqual(matched, Get(response, "MatchedResponse"));
        Assert.AreEqual("Please try again", Get(Get(response, property)!, "Title"));
        Assert.AreEqual(body, Get(response, "RawBody"));
        Assert.IsFalse((bool)Get(response, "IsSuccessStatusCode")!);
        var headers = (IDictionary<string, string[]>)Get(response, "Headers")!;
        Assert.AreEqual("15", headers["retry-after"].Single());
        Assert.AreEqual("/pets/42", headers["location"].Single());
        StringAssert.Contains(headers["content-type"].Single(), mediaType);
        Assert.IsTrue(fixture.Handler.Content!.Disposed);
    }

    [TestMethod]
    public async Task ExactErrorTakesPrecedenceOverStatusRange()
    {
        using var fixture = new ClientFixture(Generated.Value, 404, """{"missingId":42}""");
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.AreEqual("404", Get(response, "MatchedResponse"));
        Assert.AreEqual(42, Get(Get(response, "Response404")!, "MissingId"));
        Assert.IsNull(Get(response, "Response4XX"));
    }

    [TestMethod]
    [DataRow(500)]
    [DataRow(502)]
    [DataRow(503)]
    public async Task BodylessServerErrorRetainsRawBody(int status)
    {
        using var fixture = new ClientFixture(Generated.Value, status, "upstream unavailable", "text/plain");
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.AreEqual("5XX", Get(response, "MatchedResponse"));
        Assert.AreEqual("upstream unavailable", Get(response, "RawBody"));
        Assert.IsNull(Get(response, "Body"));
    }

    [TestMethod]
    public async Task ConvenienceMethodThrowsWithTypedErrorStatusHeadersAndRawBody()
    {
        const string body = """{"title":"Rate limited","status":429}""";
        using var fixture = new ClientFixture(Generated.Value, 429, body);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () => await fixture.Call("FetchAsync"));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, exception.StatusCode);
        var response = Get(exception, "Response")!;
        Assert.AreEqual("Rate limited", Get(Get(response, "Response4XX")!, "Title"));
        Assert.AreEqual(body, Get(exception, "RawBody"));
        Assert.AreEqual("15", ((IReadOnlyDictionary<string, string[]>)Get(exception, "Headers")!)["Retry-After"].Single());
        Assert.IsTrue(fixture.Handler.Content!.Disposed);
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(429)]
    public async Task MalformedJsonPreservesBodyAndStatus(int status)
    {
        using var fixture = new ClientFixture(Generated.Value, status, "{ invalid json");
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.IsInstanceOfType<JsonException>(Get(response, "DeserializationError"));
        Assert.AreEqual("{ invalid json", Get(response, "RawBody"));
        Assert.AreEqual((HttpStatusCode)status, Get(response, "StatusCode"));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () => await fixture.Call("FetchAsync"));
        Assert.AreEqual((HttpStatusCode)status, exception.StatusCode);
        Assert.IsInstanceOfType<JsonException>(exception.InnerException);
    }

    [TestMethod]
    public async Task UnexpectedContentTypePreservesHtmlError()
    {
        using var fixture = new ClientFixture(Generated.Value, 429, "<html>rate limited</html>", "text/html");
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.IsInstanceOfType<JsonException>(Get(response, "DeserializationError"));
        Assert.AreEqual("<html>rate limited</html>", Get(response, "RawBody"));
    }

    [TestMethod]
    public async Task EmptyBodyWithoutContentLengthDoesNotThrowJsonException()
    {
        using var fixture = new ClientFixture(Generated.Value, 200, "");
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.IsNull(Get(response, "Response200"));
        Assert.IsNull(Get(response, "DeserializationError"));
        Assert.AreEqual("", Get(response, "RawBody"));
    }

    [TestMethod]
    public async Task UnknownStatusPreservesRawResponseAndConvenienceMethodRejectsIt()
    {
        var spec = Specification.Replace("default:", "x-default:");
        using var fixture = new ClientFixture(GeneratorTests.Compile(spec), 299, """{"name":"unexpected"}""");
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.IsNull(Get(response, "MatchedResponse"));
        Assert.AreEqual("""{"name":"unexpected"}""", Get(response, "RawBody"));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () => await fixture.Call("FetchAsync"));
        Assert.AreEqual((HttpStatusCode)299, exception.StatusCode);
    }

    [TestMethod]
    [DataRow(201, "201")]
    [DataRow(226, "2XX")]
    [DataRow(418, "default")]
    public async Task ResponseMatchingUsesExactThenRangeThenDefault(int status, string matched)
    {
        const string spec = """
            openapi: 3.0.3
            paths:
              /pets:
                get:
                  operationId: fetch
                  responses:
                    default:
                      description: Fallback
                      content:
                        application/json:
                          schema: { type: integer }
                    '2XX':
                      description: Success range
                      content:
                        application/json:
                          schema: { type: string }
                    '201':
                      description: Created
                      content:
                        application/json:
                          schema: { type: boolean }
            """;
        var json = status == 201 ? "true" : status == 418 ? "42" : "\"partial\"";
        using var fixture = new ClientFixture(GeneratorTests.Compile(spec), status, json);
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.AreEqual(matched, Get(response, "MatchedResponse"));
        Assert.IsNotNull(Get(response, "Body"));
    }

    [TestMethod]
    public async Task TextAndProblemJsonResponsesAreSupported()
    {
        const string spec = """
            openapi: 3.0.3
            paths:
              /pets:
                get:
                  operationId: fetch
                  responses:
                    '201':
                      description: Text
                      content:
                        text/plain:
                          schema: { type: string }
                    default:
                      description: Problem
                      content:
                        application/problem+json:
                          schema:
                            type: object
                            properties:
                              detail: { type: string }
            """;
        using var fixture = new ClientFixture(GeneratorTests.Compile(spec), 201, "created", "text/plain");
        Assert.AreEqual("created", await fixture.Call("FetchAsync"));
        using var problemFixture = new ClientFixture(GeneratorTests.Compile(spec), 500, """{"detail":"service failed"}""", "application/problem+json");
        var problemResponse = await problemFixture.Call("FetchWithResponseAsync");
        Assert.AreEqual("service failed", Get(Get(problemResponse, "ResponseDefault")!, "Detail"));
    }

    [TestMethod]
    public async Task DefaultOnlySpecificationCompilesAndHandlesErrors()
    {
        const string spec = """
            openapi: 3.0.3
            paths:
              /pets:
                get:
                  operationId: fetch
                  responses:
                    default:
                      description: Any response
                      content:
                        application/json:
                          schema: { type: string }
            """;
        using var fixture = new ClientFixture(GeneratorTests.Compile(spec), 404, "\"not found\"");
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.AreEqual("not found", Get(response, "ResponseDefault"));
    }

    [TestMethod]
    [DataRow("head", 200)]
    [DataRow("get", 204)]
    [DataRow("get", 205)]
    [DataRow("get", 304)]
    public async Task HeadAndBodylessStatusesSkipBodyReading(string verb, int status)
    {
        var spec = Specification.Replace("    get:", "    " + verb + ":");
        using var fixture = new ClientFixture(GeneratorTests.Compile(spec), status, "should not be read");
        fixture.Handler.FailIfRead = true;
        var response = await fixture.Call("FetchWithResponseAsync");
        Assert.AreEqual("", Get(response, "RawBody"));
        Assert.IsNull(Get(response, "DeserializationError"));
        Assert.IsTrue(fixture.Handler.Content!.Disposed);
    }

    [TestMethod]
    public async Task TransportErrorsAreNotConvertedIntoHttpResponses()
    {
        using var fixture = new ClientFixture(Generated.Value, 200, "");
        fixture.Handler.TransportError = new HttpRequestException("connection failed");
        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () => await fixture.Call("FetchWithResponseAsync"));
        Assert.AreEqual("connection failed", exception.Message);
        Assert.IsNull(exception.StatusCode);
    }

    [TestMethod]
    public async Task CancellationIsPropagated()
    {
        using var fixture = new ClientFixture(Generated.Value, 200, "");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await fixture.Call("FetchWithResponseAsync", cancellation.Token));
    }

    [TestMethod]
    [DataRow("600")]
    [DataRow("2xy")]
    public void InvalidResponseStatusReportsDiagnostic(string status)
    {
        var (_, result) = GeneratorTests.Generate(Specification.Replace("'200':", "'" + status + "':"));
        Assert.AreEqual("OAG001", result.Diagnostics.Single().Id);
    }

    [TestMethod]
    public async Task JsonRequestAndResponseBodiesCanBeUsedTogether()
    {
        const string spec = """
            openapi: 3.0.3
            paths:
              /pets:
                post:
                  operationId: fetch
                  requestBody:
                    required: true
                    content:
                      application/json:
                        schema: { type: string }
                  responses:
                    '201':
                      description: Created
                      content:
                        application/json:
                          schema: { type: integer }
            """;
        var assembly = GeneratorTests.Compile(spec);
        using var http = new HttpClient(new ResponseHandler(201, "0", "application/json"))
        { BaseAddress = new Uri("https://example.test/") };
        var type = assembly.GetType("Company.PetConsumer.PetConsumerClient")!;
        var client = Activator.CreateInstance(type, http)!;
        var task = (Task)type.GetMethod("FetchAsync")!.Invoke(client, ["Milo", CancellationToken.None])!;
        await task;
        Assert.AreEqual(0, Get(task, "Result"));
    }

    [TestMethod]
    [DataRow("text/plain", "created")]
    [DataRow("application/json", "\"created\"")]
    public async Task SameResponseSupportsJsonOrTextBasedOnContentType(string mediaType, string body)
    {
        const string spec = """
            openapi: 3.0.3
            paths:
              /pets:
                get:
                  operationId: fetch
                  responses:
                    '201':
                      description: Created
                      content:
                        application/json:
                          schema: { type: string }
                        text/plain:
                          schema: { type: string }
            """;
        using var fixture = new ClientFixture(GeneratorTests.Compile(spec), 201, body, mediaType);
        Assert.AreEqual("created", await fixture.Call("FetchAsync"));
    }
    private static object? Get(object instance, string property)
        => (instance.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? instance.GetType().GetProperty(property))!.GetValue(instance);

    private sealed class ClientFixture : IDisposable
    {
        public ResponseHandler Handler { get; }
        private readonly HttpClient http;
        private readonly object client;
        private readonly Type clientType;
        public ClientFixture(Assembly assembly, int status, string body, string mediaType = "application/json")
        {
            Handler = new ResponseHandler(status, body, mediaType);
            http = new HttpClient(Handler) { BaseAddress = new Uri("https://example.test/") };
            clientType = assembly.GetType("Company.PetConsumer.PetConsumerClient")!;
            client = Activator.CreateInstance(clientType, http)!;
        }

        public async Task<object> Call(string method, CancellationToken cancellationToken = default)
        {
            var task = (Task)clientType.GetMethod(method)!.Invoke(client, [cancellationToken])!;
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task)!;
        }

        public void Dispose() => http.Dispose();
    }

    private sealed class ResponseHandler(int status, string body, string mediaType) : HttpMessageHandler
    {
        public UnknownLengthContent? Content { get; private set; }
        public bool FailIfRead { get; set; }
        public HttpRequestException? TransportError { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TransportError != null) throw TransportError;
            Content = new UnknownLengthContent(body, FailIfRead);
            Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = Content };
            response.Headers.TryAddWithoutValidation("Retry-After", "15");
            response.Headers.Location = new Uri("/pets/42", UriKind.Relative);
            return Task.FromResult(response);
        }
    }

    private sealed class UnknownLengthContent(string text, bool failIfRead) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            if (failIfRead) throw new InvalidOperationException("Body must not be read.");
            return stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}



