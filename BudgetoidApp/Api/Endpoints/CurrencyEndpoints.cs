using Api.Infrastructure;
using Application.Currencies;
using Application.Currencies.GetCurrencies;

namespace Api.Endpoints;

public static class CurrencyEndpoints
{
    public static IEndpointRouteBuilder MapCurrencyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // One of the six groups allowed to bring an account into existence; the reason the marker is
        // opt-in and sits on the group is written out in AccountEndpoints. This group is in the list
        // even though the query below reads a reference table and needs no budget: what the marker
        // declares is that a client's first authenticated request may land here.
        RouteGroupBuilder group = endpoints.MapGroup("/api/currencies")
            .WithMetadata(new ProvisionsUserAttribute());

        group.MapGet("/", async (GetCurrenciesHandler handler, CancellationToken cancellationToken) =>
        {
            CurrencyListResponse response = await handler.HandleAsync(new GetCurrenciesQuery(), cancellationToken);
            return TypedResults.Ok(response);
        });

        return endpoints;
    }
}
