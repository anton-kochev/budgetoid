using System.Text.Json;
using Application.Passkeys.Reauthentication;
using Application.RecoveryCodes.CountRecoveryCodes;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Application.RecoveryCodes.RedeemRecoveryCode;

namespace Api.Endpoints;

public static class RecoveryCodeEndpoints
{
    public static IEndpointRouteBuilder MapRecoveryCodeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Another group over "/api/me", beside erasure, the export, the credential list and the signed-in
        // user, which is the shape PasskeyEndpoints already uses for one prefix and two groups. "/api/me"
        // is the current principal's namespace, and a set of recovery codes is something the principal
        // holds. It is deliberately not under "/api/passkeys": that prefix is where the WebAuthn
        // ceremonies are run, and neither route here runs one — the POST *consumes* a re-authentication
        // somebody else minted.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me");

        // NEITHER ROUTE BELOW DECLARES ANY METADATA, and that is two separate rules.
        //
        // No RequireAuthorization: the application's fallback policy already covers every route that
        // declares nothing, and restating it here would make the one line that defines the anonymous
        // surface stop being the only one. And never AllowAnonymous.
        //
        // No ProvisionsUser, and neither route may ever gain one. A provider id token stays valid for up
        // to an hour after the account it names is erased, so a route that minted an account in order to
        // answer a code generation would let that stale token bring the account back — as a shell HOLDING
        // RECOVERY CODES, which is strictly worse than the empty shell the erasure and export groups
        // argue about: a set of recovery codes is a full-session credential, so the resurrected account
        // comes back with a working way in, and it holds no passkey, so the re-authentication gate in
        // front of erasure can never remove it again. An authenticated subject with no account is refused
        // by UserProvisioningMiddleware instead. The GET is the easier of the two to mark by mistake,
        // because a read looks harmless.

        // POST to the collection rather than to a named sub-resource, and 200 rather than 201 on both a
        // first issue and a regeneration: the resource is THE ACCOUNT'S RECOVERY-CODE SET, singular —
        // IX_credentials_user_id_recovery_codes is what makes it singular — and a POST replaces it. A 201
        // on the first and a 200 on the second would make a client branch on which of two states its own
        // account was in before it asked, which is a fact it has no way to know and no use for. A 409 on
        // the second would refuse the request a person makes precisely when they need it most: the card
        // is lost, and the codes printed on it must stop working.
        //
        // The proof travels in the body of this request rather than being a separate call the endpoint
        // makes, so issuing without proof is unreachable rather than merely uncustomary — see
        // GenerateRecoveryCodesCommand. The handler runs the gate BEFORE it validates the set, which is a
        // rule rather than a reading preference; the argument is written out there.
        group.MapPost("/recovery-codes", async (
            RecoveryCodeGenerationRequest request,
            GenerateRecoveryCodesHandler handler,
            CancellationToken cancellationToken) =>
        {
            RecoveryCodesGeneration generation = await handler.HandleAsync(
                new GenerateRecoveryCodesCommand(
                    request.Verifiers,
                    new ReauthenticationAssertion(
                        request.CredentialId,
                        request.ClientDataJson,
                        request.AuthenticatorData,
                        request.Signature,
                        request.UserHandle)),
                cancellationToken);

            // 200 with a body rather than the erasure's 204: this act leaves an account standing, and
            // what it says about that account — how many sessions replacing the set ended — is the only
            // observable evidence that the sweep ran at all, since the delete's cascade would take those
            // rows either way. See RecoveryCodesGeneration.
            //
            // TypedResults.Ok rather than hand-serialized JSON, so camelCase comes from
            // ConfigureHttpJsonOptions like every other response instead of from this call site. Two
            // members and no code, no verifier and no id: the server never held a code, and the client
            // already has the codes it derived its verifiers from.
            //
            // A RESPONSE RECORD OF THIS LAYER'S OWN, unlike the count leg, for one reason: the kind is
            // converted here rather than left to the serializer. ConfigureHttpJsonOptions registers
            // JsonStringEnumConverter with no naming policy, so a SessionKind serialized straight out of
            // the application record would reach the wire as "Full" while the sessions.kind column, and
            // every other spelling of it in this product, reads "full". The assertion and redemption
            // legs make the same conversion at the same boundary.
            //
            // The session member is written as JSON null when nothing was re-established rather than
            // omitted — nothing configures DefaultIgnoreCondition, and that is the shape to keep: a
            // member that appears only sometimes makes "the server did not tell me" and "the server told
            // me no" the same observation for a client.
            return TypedResults.Ok(new RecoveryCodeGenerationResponse(
                generation.Session is { } session
                    ? new ReestablishedSessionResponse(
                        JsonNamingPolicy.CamelCase.ConvertName(session.Kind.ToString()),
                        session.ExpiresAtUtc)
                    : null,
                generation.SessionsEnded));
        });

        // NOT gated by re-authentication, and that is a decision rather than an omission. A count is not
        // destructive — it names no code and unlocks nothing — and the settings screen reads it on load,
        // so gating it would mint a re-authentication nonce on every page view: a live nonce for a
        // ceremony nobody intends to complete, behind a WebAuthn prompt the person did not ask for.
        //
        // AN ACCOUNT WITH NO SET ANSWERS 200 AND {"remaining": 0}, NEVER A 404, and this is exactly the
        // kind of thing a later reader "corrects". There really is no credentials row to read, so a
        // handler written as "find the set, then count its codes" reaches for a 404 naturally — which is
        // why the read counts rows and never looks for the set. The client has no use for the
        // distinction: "you have no codes" and "you have zero left" are the same actionable fact —
        // generate a set — the control that offers it is the same control, and a 404 forces a settings
        // screen whose whole job is to say what to do next to carry a branch whose two arms render the
        // same thing.
        group.MapGet("/recovery-codes", async (
            CountRecoveryCodesHandler handler,
            CancellationToken cancellationToken) =>
        {
            RecoveryCodeCount count = await handler.HandleAsync(
                new CountRecoveryCodesQuery(),
                cancellationToken);

            return TypedResults.Ok(count);
        });

        // ITS OWN GROUP OVER ITS OWN PREFIX, AND IT IS ANONYMOUS. The shape PasskeyEndpoints uses for
        // its sign-in legs, and for the same reason: a redemption by definition runs before anyone is
        // signed in, because somebody redeeming a code has lost the authenticator that would have
        // proved who they are. What makes the permission reviewable is not this line —
        // AnonymousSurfaceTests reads every AllowAnonymous route off the route table and compares the
        // set whole, and the argument for this pattern is written out beside it there.
        //
        // NOT UNDER "/api/me", which is the current principal's namespace and this request has no
        // principal: it names nobody, and the account it lands on is discovered from the code. And not
        // under "/api/passkeys", which is where the WebAuthn ceremonies are run and this is not one.
        //
        // NO ProvisionsUser, AND IT MAY NEVER GAIN ONE — a likelier accident here than on "/api/me",
        // because this is the route people reach for when they cannot get in, which reads a great deal
        // like a route that should be able to create something. A provider id token stays valid for up
        // to an hour after the account it names is erased, so a marker here would turn one retried
        // redemption into a resurrected, passkey-less account that the re-authentication gate in front
        // of erasure can never remove again. The marker would also do nothing it appears to do:
        // UserProvisioningMiddleware reads the anonymous arm first and returns, so the two markers are
        // mutually exclusive and UserProvisioningRouteTests holds them disjoint.
        RouteGroupBuilder anonymous = endpoints.MapGroup("/api/recovery-codes").AllowAnonymous();

        // POST to a sub-resource rather than a verb: the thing being created is a redemption of the
        // account's set. 200 rather than 201, and with no Location header, for the reason the assertion
        // leg answers 200 — what this creates is a session, and a session is deliberately not a
        // resource this API exposes at an address.
        anonymous.MapPost("/redemption", async (
            RedemptionRequest request,
            RedeemRecoveryCodeHandler handler,
            CancellationToken cancellationToken) =>
        {
            RedeemedRecoveryCode redemption = await handler.HandleAsync(
                new RedeemRecoveryCodeCommand(request.Verifier),
                cancellationToken);

            // The kind, the expiry and what is left — and deliberately no session id, for the reason
            // AssertionResponse states: returning the row's id would hand the client a stable handle to
            // a session, and the likeliest way this design is broken later is somebody deciding that
            // handle is close enough to a token to start accepting it. Do not add it.
            //
            // The kind is converted here rather than left to the serializer because
            // ConfigureHttpJsonOptions registers JsonStringEnumConverter with no naming policy, so a
            // SessionKind would go over the wire as "Full" while the column, and every other spelling
            // of it in this product, reads "full". AssertionResponse makes the same conversion at the
            // same boundary.
            return TypedResults.Ok(new RedemptionResponse(
                JsonNamingPolicy.CamelCase.ConvertName(redemption.Kind.ToString()),
                redemption.ExpiresAtUtc,
                redemption.Remaining));
        });

        return endpoints;
    }

    /// <summary>
    /// The verifier being presented, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One member and no second one.</b> There is nothing else a redemption may carry: an account
    /// id, an email or a credential id would each be a value the server would have to either ignore or
    /// trust, and trusting one would let an anonymous caller name the account a code is matched
    /// against.
    /// </para>
    /// <para>
    /// <b>Not <c>required</c></b>, deliberately and for the reason
    /// <see cref="RecoveryCodeGenerationRequest" /> gives about its own members: a body of <c>{}</c>
    /// binds this to <see langword="null" /> and reaches the handler's own decode, which answers the
    /// same 401 every other refusal answers. Marking it required would buy a framework 400 that tells
    /// an anonymous caller the server has an opinion about the member's shape before it has refused
    /// them, and it would be a second answer this route can give.
    /// </para>
    /// <para>
    /// That envelope covers what binds, not what fails to. No body at all, a literal <c>null</c>, or a
    /// member of the wrong JSON type is a framework 400 raised before the handler is entered, and the
    /// gap is accepted for the reason the erasure states — a deserialization failure is a fact about
    /// the caller's own request and says nothing about what is stored.
    /// </para>
    /// </remarks>
    private sealed record RedemptionRequest(string? Verifier);

    /// <summary>
    /// What a redeemed code bought: how much of the account the sign-in reaches, until when, and how
    /// many codes the card has left.
    /// </summary>
    /// <remarks>
    /// The kind is a string rather than a <c>SessionKind</c> so the spelling on the wire is the
    /// column's, decided at this boundary — see the conversion at the call site.
    /// </remarks>
    private sealed record RedemptionResponse(string Kind, DateTime ExpiresAtUtc, int Remaining);

    /// <summary>
    /// What an issue has to say for itself: the session a replacement re-established, or
    /// <see langword="null" />, and how many sessions replacing the set ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two members and no third one.</b> No code, no verifier, no stored hash, no credential id and
    /// no account id: the server never held a code, echoing the verifiers back would put a value a
    /// redemption could be attempted with into every client log on the way, and an id in a response body
    /// is an id in a client log.
    /// </para>
    /// <para>
    /// The session is nested rather than flattened into a kind and an expiry beside the count, so
    /// "kind present, expiry absent" is unrepresentable — see <see cref="RecoveryCodesGeneration" />.
    /// </para>
    /// </remarks>
    private sealed record RecoveryCodeGenerationResponse(
        ReestablishedSessionResponse? Session,
        int SessionsEnded);

    /// <summary>
    /// The session a replacement opened over the new set.
    /// </summary>
    /// <remarks>
    /// The kind is a string rather than a <c>SessionKind</c> so the spelling on the wire is the
    /// column's, decided at this boundary — see the conversion at the call site. No session id, for the
    /// reason <see cref="RedemptionResponse" /> gives.
    /// </remarks>
    private sealed record ReestablishedSessionResponse(string Kind, DateTime ExpiresAtUtc);

    /// <summary>
    /// The set being presented, and the assertion the issue is authorized by — the latter in the shape
    /// the erasure and revocation legs' own request records already use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The five assertion members are byte-identical to <c>ErasureRequest</c>'s and
    /// <c>RevocationRequest</c>'s on purpose, so a caller comparing the erasure, revocation and
    /// generation gates learns nothing from the difference between them.
    /// </para>
    /// <para>
    /// <b>No member is <c>required</c></b>, deliberately and identically to those two. Every member bound
    /// to <see langword="null" /> — a body of <c>{}</c>, or one naming only some of them — reaches the
    /// gate's own decode, which answers the same 401 every other refusal on this endpoint answers.
    /// Marking them required would buy a framework 400 that tells a caller holding a stolen bearer token
    /// that its proof was the thing found wanting. It bites harder on <see cref="Verifiers" /> than on
    /// the assertion members: a framework 400 there would tell an unproven caller that the server has an
    /// opinion about the set, which is the disclosure the handler's gate-before-validation ordering
    /// exists to prevent. So an absent set arrives here as <see langword="null" /> despite the
    /// non-nullable declaration and is refused past the gate as a set of the wrong size, which is what
    /// it is.
    /// </para>
    /// <para>
    /// That envelope covers what binds, not what fails to. No body at all, a literal <c>null</c>, or a
    /// member of the wrong JSON type is a framework 400 raised before this handler is entered, and the
    /// gap is accepted for the reason the erasure states — a deserialization failure is a fact about the
    /// caller's own request and says nothing about what this account holds.
    /// </para>
    /// <para>
    /// The verifiers are base64url <b>text</b>, which is how every binary member of this exchange crosses
    /// JSON, and they travel beside the assertion rather than nested because they are members of the same
    /// command. <see cref="CredentialId" /> is the WebAuthn <em>handle</em> of the authenticator that
    /// signed the assertion — this route addresses no credential of its own, so unlike the revocation
    /// there is no second id space for it to be confused with.
    /// </para>
    /// </remarks>
    private sealed record RecoveryCodeGenerationRequest(
        IReadOnlyList<string> Verifiers,
        string CredentialId,
        string ClientDataJson,
        string AuthenticatorData,
        string Signature,
        string? UserHandle);
}
