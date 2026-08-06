using Application.Users.EraseAccount;

namespace Api.Endpoints;

public static class AccountErasureEndpoints
{
    public static IEndpointRouteBuilder MapAccountErasureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // "/api/me" rather than "/api/account": account is already this codebase's word for
        // Domain.Accounts.Account, served at /api/accounts/{id} with the same verb and the same
        // status, and a client aiming at the wrong one would be indistinguishable in the logs.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me");

        // No id in the route and no body: the account erased is whichever one the request is
        // authenticated as. Authentication comes from the fallback policy, so this endpoint declares
        // no metadata of its own and must never declare AllowAnonymous.
        group.MapDelete("/", async (
            EraseAccountHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(new EraseAccountCommand(), cancellationToken);
            return TypedResults.NoContent();
        });

        return endpoints;
    }
}
