using Application.Currencies;
using Application.Currencies.GetCurrencies;

namespace Api.Endpoints;

public static class CurrencyEndpoints
{
    public static IEndpointRouteBuilder MapCurrencyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/currencies");

        group.MapGet("/", async (GetCurrenciesHandler handler, CancellationToken cancellationToken) =>
        {
            CurrencyListResponse response = await handler.HandleAsync(new GetCurrenciesQuery(), cancellationToken);
            return TypedResults.Ok(response);
        });

        return endpoints;
    }
}
