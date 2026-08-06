using Application.Passkeys.Reauthentication;
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

        // POST to a named sub-resource rather than DELETE on the group, and the old DELETE /api/me is
        // removed rather than kept beside it: leaving it routed would leave the token-only erasure path
        // open, and every rule below would describe a door standing next to an open one. The verb is
        // POST because the request carries a body that has to be verified — a DELETE with a proof in
        // its body is a shape intermediaries are free to strip.
        //
        // Still no id in the route, and the body still names no account: the account erased is
        // whichever one the request is authenticated as, and the assertion in the body names a
        // credential handle that the owner-scoped lookup makes incapable of selecting anybody.
        // Authentication comes from the fallback policy, so this endpoint declares no metadata of its
        // own and must never declare AllowAnonymous.
        group.MapPost("/erasure", async (
            ErasureRequest request,
            EraseAccountHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new EraseAccountCommand(new ReauthenticationAssertion(
                    request.CredentialId,
                    request.ClientDataJson,
                    request.AuthenticatorData,
                    request.Signature,
                    request.UserHandle)),
                cancellationToken);

            // 204: the post-condition is stated, and there is nothing left to describe. A body would
            // have to be assembled from an account that no longer exists.
            return TypedResults.NoContent();
        });

        return endpoints;
    }

    /// <summary>
    /// The assertion the erasure is authorized by, in the shape the sign-in leg's own request record
    /// already uses.
    /// </summary>
    /// <remarks>
    /// The members are not <c>required</c>, deliberately and identically to that leg: a body of
    /// <c>{}</c> binds them all to <see langword="null" /> and reaches the gate's own decode, which
    /// answers the same 401 every other refusal on this endpoint answers. Marking them required would
    /// buy a framework 400 that tells a caller holding a stolen bearer token that its proof was the
    /// thing found wanting.
    /// </remarks>
    private sealed record ErasureRequest(
        string CredentialId,
        string ClientDataJson,
        string AuthenticatorData,
        string Signature,
        string? UserHandle);
}
