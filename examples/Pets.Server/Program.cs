using OpenForma.Examples.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var app = builder.Build();
app.MapControllers();

if (args.Contains("--verify"))
{
    var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
        .OfType<ControllerActionDescriptor>().ToArray();
    if (actions.Length != 1 || actions[0].AttributeRouteInfo?.Template != "pets/{id}")
        throw new InvalidOperationException("Expected one inherited OpenAPI route.");
    var controller = new PetsController();
    var found = await controller.GetPetAsync(42);
    var missing = await controller.GetPetAsync(404);
    if (found.Value?.Name != "Milo" || missing.Result is not ObjectResult { StatusCode: 404 })
        throw new InvalidOperationException("Unexpected inherited controller action response.");
    Console.WriteLine("NuGet server consumer verified: generated models, inherited route, 200 model, and 404 response.");
    return;
}

app.Run();
