using Application.Passkeys.Reauthentication;
using Application.Passkeys.RevokePasskey;
using Application.Users.ListCredentials;
using Domain.Users;

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
        // must never appear.
        //
        // IT MINTS NO ACCOUNT, and that is a compile-time fact: RegisterAccountHandler is the only
        // code that brings one into existence, the only caller of the only factory the domain offers,
        // and it is reachable only from POST /api/registration/registration — the provider scheme, a
        // verified passkey attestation, and a challenge from the AccountRegistration pool. The reason
        // it must stay that way: minting to answer a revocation would let a provider token outliving
        // an erasure bring the account back as an empty shell.
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
        // policy authenticates it, and this read creates nothing — a plain GET able to resurrect an
        // account for a provider token that outlives an erasure by up to an hour would be the worst
        // shape of that mistake, and registration being the only creating path is what rules it out.
        group.MapGet("/credentials", async (
            ListCredentialsHandler handler,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<CredentialSummary> credentials = await handler.HandleAsync(
                new ListCredentialsQuery(),
                cancellationToken);

            // The type is spelled by CredentialTypeSpelling rather than by a global naming policy or
            // by anything derived from the member's name. Program.cs registers JsonStringEnumConverter
            // with no policy, so CredentialType.Passkey handed straight to the serializer would reach
            // the wire as "Passkey" and disagree with the type column and its check constraint — and
            // adding a policy would silently respell every other enum the API emits.
            //
            // What used to stand here was a camel-case naming policy over ToString(), and it agreed
            // with the column only while every member was one word: RecoveryCodes camel-cases to
            // "recoveryCodes" where the column says "recovery_codes". The endpoint no longer derives a
            // spelling at all — it reads the one the persistence configurations write, which is what
            // makes "the wire agrees with the column" a fact rather than two hand-conversions that
            // happen to match.
            return TypedResults.Ok(credentials
                .Select(credential => new CredentialListEntry(
                    credential.Id,
                    CredentialTypeSpelling.Of(credential.Type),
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
