using System.Text.Json;
using Api.Infrastructure;
using Application.Passkeys.BeginAssertion;
using Application.Passkeys.BeginRegistration;
using Application.Passkeys.CompleteAssertion;
using Application.Passkeys.CompleteRegistration;
using Application.Passkeys.Reauthentication;
using Application.Sessions;

namespace Api.Endpoints;

public static class PasskeyEndpoints
{
    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Two groups over one path prefix rather than one group with per-endpoint attributes, so the
        // permission is declared once for a set of routes instead of once per route. Anything added to
        // the first group inherits the application's fallback policy and stays authenticated; anything
        // added to the second is public, and has to be argued for where it is written.
        //
        // WHAT THIS SHAPE DOES NOT BUY IS A COMPLETE ANONYMOUS SURFACE A REVIEWER CAN READ HERE. It
        // never did: the health check in ServiceDefaults is anonymous and is nowhere near this file, and
        // a redemption of a recovery code is anonymous in RecoveryCodeEndpoints. The surface is pinned
        // whole by AnonymousSurfaceTests, which reads every AllowAnonymous route off the route table and
        // compares it against a written-out set — so a route that gains the marker is a red test
        // somebody has to answer for in the same commit, by adding the pattern AND the argument for it
        // beside the others. That, and not a line in this file, is what makes the permission reviewable.
        RouteGroupBuilder authenticated = endpoints.MapGroup("/api/passkeys");

        // POST, not GET, for both options legs. They mint a nonce and persist it, so they are neither
        // safe nor idempotent, and a GET would be cacheable and prefetchable — both of which spend
        // challenges nobody asked for.
        authenticated.MapPost("/registration/options", async (
            BeginRegistrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            PasskeyCreationOptions options = await handler.HandleAsync(
                new BeginRegistrationCommand(),
                cancellationToken);
            return TypedResults.Ok(options);
        });

        authenticated.MapPost("/registration", async (
            RegistrationRequest request,
            CompleteRegistrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new CompleteRegistrationCommand(
                    request.ClientDataJson,
                    request.AttestationObject,
                    request.ClientExtensionResults,
                    request.FactorId,
                    request.WrappedContentKey,
                    request.WrappedIndexKey),
                cancellationToken);

            // 201 with no Location header and no body: the credential is a fact about the account,
            // not a resource this API exposes at an address. Nothing may read a stored passkey back,
            // so there is nowhere to point at — and nothing to report either, since the only thing
            // left to say is what the client told the server about its own device, and echoing that
            // back would read as the server having established it.
            return TypedResults.Created();
        });

        // The re-authentication options leg, and it belongs in THIS group. It mints the one nonce that
        // authorizes destroying an account, so an anonymous caller able to obtain one would make the
        // erasure endpoint's refusal of the other two pools worth nothing. There is no finish leg here:
        // the ceremony is completed by POST /api/me/erasure, which is the action it authorizes.
        authenticated.MapPost("/reauthentication/options", async (
            BeginReauthenticationHandler handler,
            CancellationToken cancellationToken) =>
        {
            PasskeyRequestOptions options = await handler.HandleAsync(
                new BeginReauthenticationCommand(),
                cancellationToken);
            return TypedResults.Ok(options);
        });

        // The sign-in legs. Anonymous on the group, because sign-in is the one exchange that by
        // definition runs before anyone is signed in. Nothing anywhere carries a list of routes to
        // skip: an exclusion list would be a second place the anonymous surface is defined, and
        // AnonymousSurfaceTests reads this very marker off the route table to hold the first one whole.
        RouteGroupBuilder anonymous = endpoints.MapGroup("/api/passkeys").AllowAnonymous();

        anonymous.MapPost("/assertion/options", async (
            BeginAssertionHandler handler,
            CancellationToken cancellationToken) =>
        {
            PasskeyRequestOptions options = await handler.HandleAsync(
                new BeginAssertionCommand(),
                cancellationToken);
            return TypedResults.Ok(options);
        });

        anonymous.MapPost("/assertion", async (
            AssertionRequest request,
            CompleteAssertionHandler handler,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            Issued<EstablishedSession> issued = await handler.HandleAsync(
                new CompleteAssertionCommand(
                    request.CredentialId,
                    request.ClientDataJson,
                    request.AuthenticatorData,
                    request.Signature,
                    request.UserHandle),
                cancellationToken);
            EstablishedSession session = issued.Value;

            // AFTER THE HANDLER RETURNED, AND THAT ORDERING IS THE WHOLE OF WHAT MAKES THIS COOKIE
            // EVIDENCE OF A SIGN-IN. Every refusal on this leg leaves by exception — an unknown
            // credential, a spent challenge, a signature that did not verify — so a cookie written
            // before this line is a cookie a refusal leaves behind on the client of whoever was
            // guessing. Nothing about it would look wrong: the handle names no session, so the next
            // request is refused and the caller learns only that a value they were handed does not
            // work.
            //
            // The handle comes from the handler and never from anything reachable here. This endpoint
            // cannot mint one, cannot read the stored digest, and cannot compute the expiry: it
            // destructures what the handler filed and writes it, which is what makes "the cookie
            // carries the bytes whose digest was stored, and dies with the row" a property rather than
            // a habit — see SessionHandle.
            //
            // The null arm is unreachable on this route: an assertion that returns has established a
            // session. It is written as a pattern anyway, uniformly with the two recovery-code legs,
            // because the alternative is a null-forgiving operator asserting a rule that lives in
            // another project — and a missing cookie is a red test rather than a 500 on a sign-in that
            // has already committed.
            if (issued.Handoff is { } handoff)
            {
                SessionCookie.Issue(response, handoff.Token, handoff.ExpiresAtUtc);
            }

            // The kind and the expiry, and deliberately no session id. The body has to say something
            // the behaviour can be observed through, and the kind is exactly the fact that matters —
            // how much of the account this sign-in reaches. Returning the row's id would hand the
            // client a stable handle to a session, and the most likely way this design is broken later
            // is somebody deciding that handle is close enough to a token to start accepting it. Do
            // not add it.
            return TypedResults.Ok(new AssertionResponse(
                JsonNamingPolicy.CamelCase.ConvertName(session.Kind.ToString()),
                session.ExpiresAtUtc));
        });

        return endpoints;
    }

    /// <summary>
    /// What the device produced, and the share of the account keys the factor it becomes is to hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three key-custody members are passed straight through, and none of them is <c>required</c> —
    /// see <see cref="CompleteRegistrationCommand"/>, which makes the whole argument: an absent member
    /// binds to <see langword="null"/> and is refused by the handler in a sentence about this ceremony,
    /// past the prf gate, rather than by a framework 400 raised before anything signed was judged.
    /// </para>
    /// <para>
    /// <b>This route registers a passkey on an account that already exists, and it may never create
    /// one.</b> It cannot: <c>RegisterAccountHandler</c> is the only code in the application that
    /// brings an account into existence — the only caller of <c>User.CreateWithId</c>, which is the
    /// only factory the domain offers — and it is reachable only from
    /// <c>POST /api/registration/registration</c>. The prohibition is stated because the consequence is
    /// severe rather than because anything here is close to breaking it: a provider id token outlives
    /// the account it names by up to an hour, so a passkey registration able to mint would let a stale
    /// token resurrect an erased account as a shell holding a passkey <em>and</em> a copy of the
    /// account keys, which is a working way back in rather than an empty row.
    /// </para>
    /// </remarks>
    private sealed record RegistrationRequest(
        string ClientDataJson,
        string AttestationObject,
        PasskeyClientExtensionResults? ClientExtensionResults,
        string FactorId,
        string WrappedContentKey,
        string WrappedIndexKey);

    private sealed record AssertionRequest(
        string CredentialId,
        string ClientDataJson,
        string AuthenticatorData,
        string Signature,
        string? UserHandle);

    private sealed record AssertionResponse(string Kind, DateTime ExpiresAtUtc);
}
