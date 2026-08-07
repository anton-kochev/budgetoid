using Api.Infrastructure;
using Application.Currencies;
using Application.Currencies.GetCurrencies;

namespace Api.Endpoints;

public static class CurrencyEndpoints
{
    public static IEndpointRouteBuilder MapCurrencyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // One of the six groups allowed to bring an account into existence; why the permission is
        // opt-in is in ProvisionsUserAttribute, why it sits on the group in AccountEndpoints.
        //
        // Marked even though this route needs neither a user nor a budget: currencies is shared
        // reference data belonging to no tenant, SELECT only, with no row-level-security policy (see
        // app-role-grants.sql). The marker is not a claim about what the handler reads. Which of the
        // six a client's first authenticated request lands on is not something the server can
        // constrain, so all six must be able to mint or the set is not a set.
        //
        // Not hypothetical: accounts.component.ts issues accounts.load() and getCurrencies() from one
        // ngOnInit with no ordering between them, so a brand-new user's currencies call can arrive
        // first. That is evidence, never the reason — a backend security marker justified by a
        // component's lifecycle hook is one a frontend edit can invalidate.
        //
        // First marker to drop when account creation becomes a consented act, because this is the one
        // route that never needed an identity.
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
