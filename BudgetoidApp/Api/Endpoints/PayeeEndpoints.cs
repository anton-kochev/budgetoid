using Application.Payees;
using Application.Payees.GetPayees;

namespace Api.Endpoints;

public static class PayeeEndpoints
{
    public static IEndpointRouteBuilder MapPayeeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/payees");

        group.MapGet("/", async (GetPayeesHandler handler, CancellationToken cancellationToken) =>
        {
            PayeeListResponse response = await handler.HandleAsync(new GetPayeesQuery(), cancellationToken);
            return TypedResults.Ok(response);
        });

        return endpoints;
    }
}
