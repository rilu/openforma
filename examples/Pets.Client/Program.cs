using OpenForma.Examples.Client;
using System.Net;
using System.Text;

var handler = new StubHandler();
using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
var client = new PetsClientClient(http);
var pet = await client.GetPetAsync(42);
if (pet?.Id != 42 || pet.Name != "Milo") throw new Exception("Unexpected generated client response.");

handler.Status = HttpStatusCode.NotFound;
var missing = await client.GetPetWithResponseAsync(42);
if (missing.Response404?.Title != "Not found" || missing.StatusCode != HttpStatusCode.NotFound)
    throw new Exception("Expected a typed 404 response.");

try
{
    await client.GetPetAsync(42);
    throw new Exception("Expected an API exception.");
}
catch (ApiException<GetPetResponse> exception) when (exception.StatusCode == HttpStatusCode.NotFound)
{
    if (exception.Response.Response404?.Title != "Not found") throw new Exception("Missing typed error payload.");
}

handler.Status = HttpStatusCode.TooManyRequests;
var limited = await client.GetPetWithResponseAsync(42);
if (limited.Response4XX?.Title != "Rate limited" || limited.Headers["Retry-After"].Single() != "15")
    throw new Exception("Expected a typed rate-limit response and retry header.");

Console.WriteLine("Client example verified: 200 model, typed 404, typed exception, and 429 with Retry-After.");

sealed class StubHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = Status == HttpStatusCode.OK ? """{"id":42,"name":"Milo"}"""
            : Status == HttpStatusCode.NotFound ? """{"title":"Not found","status":404}"""
            : """{"title":"Rate limited","status":429}""";
        var response = new HttpResponseMessage(Status)
        {
            Content = new StringContent(json, Encoding.UTF8, Status == HttpStatusCode.OK ? "application/json" : "application/problem+json")
        };
        response.Headers.TryAddWithoutValidation("Retry-After", "15");
        return Task.FromResult(response);
    }
}



