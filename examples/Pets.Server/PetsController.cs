using Microsoft.AspNetCore.Mvc;

namespace OpenForma.Examples.Server;

public sealed class PetsController : PetsServerAbstractController
{
    protected override Task<ActionResult<Pet>> GetPetCoreAsync(long Id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionResult<Pet> result = Id == 42
            ? new Pet { Id = Id, Name = "Milo", Tags = ["cat"] }
            : new ObjectResult(new Problem { Title = "Pet not found", Status = 404 })
            {
                StatusCode = 404,
                ContentTypes = { "application/problem+json" }
            };
        return Task.FromResult(result);
    }
}
