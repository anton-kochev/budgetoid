using Api.Infrastructure;
using Application.Currencies;
using Application.Currencies.GetCurrencies;

namespace Api.Endpoints;

public static class CurrencyEndpoints
{
    public static IEndpointRouteBuilder MapCurrencyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Brings no account into existence, and cannot: RegisterAccountHandler is the only code that
        // does, behind /api/registration. AccountEndpoints carries the argument.
        //
        // This group used to carry a minting marker despite needing neither a user nor a budget —
        // currencies is shared reference data belonging to no tenant, SELECT only, with no
        // row-level-security policy (see app-role-grants.sql) — because a brand-new client's first
        // authenticated request could land here, and which route it lands on is not something the
        // server can constrain. Account creation being a consented act is what retired that argument:
        // there is no longer a "first authenticated request" that has to find an account missing.
        RouteGroupBuilder group = endpoints.MapGroup("/api/currencies");

        group.MapGet("/", async (GetCurrenciesHandler handler, CancellationToken cancellationToken) =>
        {
            CurrencyListResponse response = await handler.HandleAsync(new GetCurrenciesQuery(), cancellationToken);
            return TypedResults.Ok(response);
        });

        return endpoints;
    }
}
