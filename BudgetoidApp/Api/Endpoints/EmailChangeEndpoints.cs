using Api.Infrastructure;
using Application.Passkeys.Reauthentication;
using Application.Users.ChangeEmail;

namespace Api.Endpoints;

public static class EmailChangeEndpoints
{
    public static IEndpointRouteBuilder MapEmailChangeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Another group over "/api/me", beside erasure, credentials and the export, for the reason
        // DataExportEndpoints states: "/api/me" is the current principal's namespace, and the address and
        // the Google identity are things the principal holds.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me");

        // THREE PROOFS, AND EACH HAS ONE OWNER.
        //
        //   the session       — the fallback policy. No RequireAuthorization, no scheme, no
        //                       AllowsLockedSessionAttribute: a session opened by the federated credential
        //                       is 403 here, like everywhere it has not been argued in, and must stay so —
        //                       the Google identity is exactly what such a session holds.
        //   the provider token — ProviderAuthorizationGate, added by RequireProviderAuthorization below,
        //                       which authenticates it separately and never merges it into the session's
        //                       principal. It runs before this delegate, so a refused token spends no
        //                       passkey challenge.
        //   the passkey       — the gate at the top of ChangeEmailHandler, over the body's assertion.
        //
        // IT MINTS NO ACCOUNT: RegisterAccountHandler is the only code that brings one into existence.
        // This moves an existing account to a new Google identity, and the provider scheme reaching it
        // does not make it a second registration path.
        group.MapPost("/email-change", async (
            EmailChangeRequest request,
            HttpContext httpContext,
            ChangeEmailHandler handler,
            CancellationToken cancellationToken) =>
        {
            // The subject and the address come from the provider's token, as the gate judged it, and from
            // nowhere else — never HttpContext.User, whose sub is the account id, and never the body,
            // which has no member for either.
            ProviderAuthorizationGate.VouchedIdentity vouched = ProviderAuthorizationGate.IdentityOf(httpContext);

            EmailChange change = await handler.HandleAsync(
                new ChangeEmailCommand(
                    vouched.Subject,
                    vouched.Email,
                    new ReauthenticationAssertion(
                        request.CredentialId,
                        request.ClientDataJson,
                        request.AuthenticatorData,
                        request.Signature,
                        request.UserHandle)),
                cancellationToken);

            // 200 with a body, the revocation's shape: the account stands, and how many sessions the
            // retired Google credential took with it is something the caller cannot work out for itself.
            // No address and no subject in it — the caller sent both.
            return TypedResults.Ok(change);
        })
            .RequireProviderAuthorization();

        return endpoints;
    }

    /// <summary>
    /// The assertion the change is authorized by, in the shape the erasure's request record already uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No <c>sub</c> and no <c>email</c>, and neither may ever be added</b>, for the reason
    /// <c>RegistrationEndpoints.RegistrationRequest</c> gives: a member here would be a value the server
    /// would have to either ignore or trust, and trusting one would file the account under an identity
    /// nobody's provider vouched for. A body that sends them anyway has them ignored, as every unknown
    /// member is.
    /// </para>
    /// <para>
    /// The members are not <c>required</c>, identically to the erasure: a member bound to
    /// <see langword="null" /> reaches the passkey gate's own decode and its one 401, rather than a
    /// framework 400 that says which part of the proof was found wanting.
    /// </para>
    /// </remarks>
    private sealed record EmailChangeRequest(
        string CredentialId,
        string ClientDataJson,
        string AuthenticatorData,
        string Signature,
        string? UserHandle);
}
