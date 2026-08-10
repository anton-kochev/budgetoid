using System.Text.Json;
using Application.Passkeys.Reauthentication;
using Application.Passkeys.RevokePasskey;
using Application.Users.ListCredentials;

namespace Api.Endpoints;

public static class CredentialEndpoints
{
    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A third group over "/api/me", beside erasure and the export, for the reason
        // DataExportEndpoints states: "/api/me" is the current principal's namespace, and a credential
        // is something the principal holds. It is deliberately not under "/api/passkeys" — that prefix
        // is the two ceremonies, which a caller runs to establish something, and this removes one.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me");

        // POST to a named sub-resource rather than DELETE on the credential, the same shape and for
        // the same reason as the erasure: the request carries a proof in its body that has to be
        // verified, and a body on a DELETE is a shape intermediaries are free to strip.
        //
        // No metadata of its own. Authentication comes from the application's fallback policy, which
        // covers every route declaring nothing, so restating RequireAuthorization here would make the
        // one line that defines the anonymous surface stop being the only one — and AllowAnonymous
        // must never appear. No ProvisionsUser either, and it must never gain one: minting an account
        // to answer a revocation would let a provider token that outlives an erasure bring the account
        // back as an empty shell, and an authenticated subject with no account is refused instead.
        //
        // ==================================================================================
        // TWO ID SPACES SHARE ONE WORD IN THIS REQUEST, AND THEY ARE NEVER COMPARED.
        //
        //   route {credentialId}  — a `credentials.id` GUID: WHAT IS BEING REMOVED.
        //   body  credentialId    — the WebAuthn credential HANDLE of the authenticator that signed
        //                           the assertion: WHAT PROVES PRESENCE.
        //
        // They are not interchangeable and neither is derivable from the other. A person may
        // legitimately prove with the very passkey they are removing — that is the request somebody
        // makes on their last working device — so a route that "checked" the two against each other
        // would be refusing a correct request.
        // ==================================================================================
        group.MapPost("/credentials/{credentialId:guid}/revocation", async (
            Guid credentialId,
            RevocationRequest request,
            RevokePasskeyHandler handler,
            CancellationToken cancellationToken) =>
        {
            PasskeyRevocation revocation = await handler.HandleAsync(
                new RevokePasskeyCommand(
                    credentialId,
                    new ReauthenticationAssertion(
                        request.CredentialId,
                        request.ClientDataJson,
                        request.AuthenticatorData,
                        request.Signature,
                        request.UserHandle)),
                cancellationToken);

            // 200 with a body rather than the erasure's 204: this act leaves an account standing, and
            // what it says about that account — how many sessions the revoked passkey took with it —
            // is something the caller cannot work out for itself.
            return TypedResults.Ok(revocation);
        });

        // Declares no metadata either, and for the reasons stated above the revocation: the fallback
        // policy authenticates it, and a ProvisionsUser marker here would let a provider token that
        // outlives an erasure — valid for up to an hour after it — resurrect the account as an empty
        // shell on a plain GET.
        group.MapGet("/credentials", async (
            ListCredentialsHandler handler,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<CredentialSummary> credentials = await handler.HandleAsync(
                new ListCredentialsQuery(),
                cancellationToken);

            // The type is camel-cased here rather than by a global naming policy. Program.cs registers
            // JsonStringEnumConverter with none, so CredentialType.Passkey would otherwise reach the
            // wire as "Passkey" and disagree with the type column, its check constraint and the export
            // document — and adding a policy would silently respell every other enum the API emits.
            //
            // This is the second endpoint hand-converting an enum, after PasskeyEndpoints' session
            // kind. A third should be the moment a shared helper is written, still at the endpoints —
            // not the moment the policy goes global.
            return TypedResults.Ok(credentials
                .Select(credential => new CredentialListEntry(
                    credential.Id,
                    JsonNamingPolicy.CamelCase.ConvertName(credential.Type.ToString()),
                    credential.CreatedAtUtc))
                .ToArray());
        });

        return endpoints;
    }

    /// <summary>
    /// One listed credential, carrying <c>id</c>, <c>type</c> and <c>createdAtUtc</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// A response record of its own rather than <see cref="CredentialSummary" /> serialized directly,
    /// because <c>type</c> has to leave as the schema's own spelling and the enum member is not it. What
    /// the three members deliberately exclude — a label, a last-used instant, an AAGUID, and above all
    /// the federated credential's provider <c>subject</c> — is argued on <see cref="CredentialSummary" />.
    /// </remarks>
    private sealed record CredentialListEntry(Guid Id, string Type, DateTime CreatedAtUtc);

    /// <summary>
    /// The assertion the revocation is authorized by, in the shape the erasure leg's own request
    /// record already uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The members are not <c>required</c>, deliberately and identically to that leg: every member
    /// bound to <see langword="null" /> — a body of <c>{}</c>, or one naming only some of them —
    /// reaches the gate's own decode, which answers the same 401 every other refusal on this endpoint
    /// answers. Marking them required would buy a framework 400 that tells a caller holding a stolen
    /// bearer token that its proof was the thing found wanting.
    /// </para>
    /// <para>
    /// Byte-identical to the erasure's body on purpose, so a caller comparing the two gates learns
    /// nothing from the difference between them. That envelope covers what binds, not what fails to:
    /// no body at all, a literal <c>null</c>, or a member of the wrong JSON type is a framework 400
    /// raised before this handler is entered, and the gap is accepted for the reason the erasure
    /// states — a deserialization failure is a fact about the caller's own request and says nothing
    /// about what credentials exist.
    /// </para>
    /// <para>
    /// <see cref="CredentialId" /> here is the WebAuthn <em>handle</em>, never the route's
    /// <c>credentials.id</c>. See the block comment above.
    /// </para>
    /// </remarks>
    private sealed record RevocationRequest(
        string CredentialId,
        string ClientDataJson,
        string AuthenticatorData,
        string Signature,
        string? UserHandle);
}
