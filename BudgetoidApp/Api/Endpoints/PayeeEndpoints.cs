using Api.Infrastructure;
using Application.Payees;
using Application.Payees.GetPayees;
using Application.Payees.RenamePayee;

namespace Api.Endpoints;

public static class PayeeEndpoints
{
    public static IEndpointRouteBuilder MapPayeeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // One of the six groups allowed to bring an account into existence; the reason the marker is
        // opt-in and sits on the group is written out in AccountEndpoints.
        RouteGroupBuilder group = endpoints.MapGroup("/api/payees")
            .WithMetadata(new ProvisionsUserAttribute());

        group.MapGet("/", async (GetPayeesHandler handler, CancellationToken cancellationToken) =>
        {
            PayeeListResponse response = await handler.HandleAsync(new GetPayeesQuery(), cancellationToken);
            return TypedResults.Ok(response);
        });

        group.MapPatch("/{id:guid}", async (
            Guid id,
            RenamePayeeRequest request,
            RenamePayeeHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(new RenamePayeeCommand(id, request.Name), cancellationToken);
            return TypedResults.NoContent();
        });

        return endpoints;
    }

    // Name is required rather than Optional<string>: a payee has one mutable field, so a body that
    // omits it is a malformed request, not a no-op the caller could have meant.
    private sealed record RenamePayeeRequest(string Name);
}
