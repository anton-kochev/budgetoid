using System.Security.Claims;
using System.Text.Json;
using Api.Infrastructure;
using Application.Sessions.EstablishLockedSession;
using Application.Sessions.ReadSession;
using Application.Sessions.RevokeSession;
using Domain.Common;
using Domain.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // A resource of its own beneath "/api/me" rather than a fourth group over that prefix: what
        // hangs off here is the caller's own sign-in, and the routes that will join it — listing the
        // account's sessions, ending one of the others — are about sessions rather than about the
        // principal.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me/session");

        // POST to a named sub-resource rather than DELETE on the group, for the reason the erasure
        // route gives: the session being ended is named by the request's own authentication and not by
        // anything in the URL, and a DELETE on "/api/me/session" reads as "delete the session I am
        // naming" when there is nothing to name.
        //
        // Signing out is the last request that should be able to bring an account into existence, and
        // now nothing here could: RegisterAccountHandler is the only code that creates one — the only
        // caller of the only factory the domain offers — behind POST /api/registration/registration,
        // which needs the provider scheme, a verified passkey attestation and a challenge from the
        // AccountRegistration pool. No AllowAnonymous either — the handle still
        // has to name a real session — and no RequireAuthorization, because the application's fallback
        // policy already covers every route that declares nothing. That last sentence is now the reason
        // this route has to say something: the fallback policy refuses a session that reads no budget
        // content, so without the opt-out below a person signed in through the identity provider could
        // not sign out. Signing out is not a read — the route reaches no budget content and no account
        // data at all — and refusing it would leave a locked session with no way to shed its cookie but
        // waiting out the expiry, on a client already showing a signed-in shell.
        group.MapPost("/revocation", async (
                HttpContext httpContext,
                RevokeSessionHandler handler,
                CancellationToken cancellationToken) =>
            {
                // Off the claim this request's own authentication produced, never off the body or the
                // route. There is therefore no session id here for a caller to substitute, which is
                // what makes "ends only the caller's session" a property of the shape rather than of a
                // check — and it is why a second live session on the same account survives this.
                //
                // The claim is absent on a request that authenticated some other way. Nothing reaches
                // this route on another scheme today — the fallback policy names the cookie's — so the
                // guard is kept for what it does when one arrives: there is no session row to end, so
                // this ends nothing, still answers 204, and still clears the cookie. Refusing instead
                // would be a sign-out that fails, which is the one answer this route may never give.
                if (Guid.TryParse(
                        httpContext.User.FindFirstValue(
                            SessionCookieAuthenticationHandler.SessionIdClaimType),
                        out Guid sessionId))
                {
                    // The handler reports whether THIS call ended the session, and that answer
                    // deliberately does not reach the wire. A caller retrying learns nothing from it,
                    // and a caller told "already ended" is told that a handle they presented was once
                    // live — which is the one thing a sign-out has no reason to disclose.
                    _ = await handler.HandleAsync(new RevokeSessionCommand(sessionId), cancellationToken);
                }

                // After the revocation, and unconditionally. The row is what ends access; this is what
                // stops a client going on believing it is signed in and showing a signed-in shell to
                // whoever is at the keyboard.
                SessionCookie.Clear(httpContext.Response);

                // 204: the post-condition is stated and there is nothing left to describe. The same
                // answer on a retry, which is the whole point — see AcceptsEndedSessionAttribute.
                return TypedResults.NoContent();
            })
            // The one route in the application that may be reached with a handle whose session has
            // already ended. Read the attribute before adding a second.
            .WithMetadata(new AcceptsEndedSessionAttribute())
            // And one of the three routes a session that reads no budget content may reach — the others
            // are the session read below and the erasure schedule, which a full session may not. The two
            // markers here are independent and this route happens to need both: the first is about a
            // handle that is no longer good, the second about a credential that never opened the
            // account's keys.
            .WithMetadata(new AllowsLockedSessionAttribute());

        // The session telling its holder what it is. A locked tab that reloads has to learn that it is
        // signed in and locked: GET /api/me refuses it 403, and a client reading that refusal as "signed
        // out" sends a signed-in person back to the front door with no way to reach the one action a
        // locked session has. Mapped on the full path rather than on the group: a group joins even an
        // empty pattern with a separator, which would make the route "/api/me/session/". The group
        // carries no conventions, so nothing is lost by stepping outside it.
        //
        // Only the locked-session opt-out. Not RequiresLockedSession — a full session asks the same
        // question and gets the same shape of answer — and not AcceptsEndedSession: a read that
        // answered a revoked handle would tell its holder the sign-in it named was once real, and what
        // kind it was. The cookie is not touched; reading the session neither ends nor renews it.
        //
        // The delegate takes the handler and nothing beneath it, so no persistence port is reachable
        // from here; the schedule is read by the request's resolved identity inside the handler.
        endpoints.MapGet("/api/me/session", async (
                HttpContext httpContext,
                ReadSessionHandler handler,
                CancellationToken cancellationToken) =>
            {
                // Off the claim this request's own authentication produced, exactly as the sign-out
                // route reads it, so there is no session id here for a caller to substitute. Unlike
                // the sign-out, a missing claim has no honest answer — there is no session to describe
                // — so it throws to the 500 GlobalExceptionHandler writes rather than inventing one.
                // Nothing reaches this route on another scheme today; the fallback policy names the
                // cookie's.
                if (!Guid.TryParse(
                        httpContext.User.FindFirstValue(
                            SessionCookieAuthenticationHandler.SessionIdClaimType),
                        out Guid sessionId))
                {
                    throw new InvalidOperationException(
                        "The session read was reached by a request carrying no session id claim.");
                }

                SessionSummary summary = await handler.HandleAsync(
                    new ReadSessionQuery(sessionId), cancellationToken);

                // The kind is converted here rather than left to the serializer, at the same boundary
                // every establishing leg converts its own: ConfigureHttpJsonOptions registers
                // JsonStringEnumConverter with no naming policy, so a SessionKind would reach the wire
                // as "Full" while the sessions.kind column, and every other spelling of it in this
                // product, reads "full".
                //
                // The erasure member is written as JSON null when the account holds no schedule rather
                // than omitted — nothing configures DefaultIgnoreCondition — so "the server did not
                // tell me" and "the server told me none" never read the same to a client.
                return TypedResults.Ok(new SessionResponse(
                    JsonNamingPolicy.CamelCase.ConvertName(summary.Kind.ToString()),
                    summary.ExpiresAtUtc,
                    summary.ErasureTakesEffectAtUtc is { } takesEffectAtUtc
                        ? new ScheduledErasureResponse(takesEffectAtUtc)
                        : null));
            })
            .WithMetadata(new AllowsLockedSessionAttribute());

        // THE LOCKED SIGN-IN: a provider token, and nothing else, turned into a locked session over the
        // account's federated credential. Here rather than in a file of its own because what it produces
        // is a session — the same three facts GET /api/me/session answers — and not beneath "/api/me"
        // because the request is served under no session: it opens one, and any it arrives with is at
        // most replaced.
        //
        // Its own policy, naming the provider's scheme — the registration group's shape, for that group's
        // reason. Naming the scheme is what makes AuthorizationMiddleware authenticate the bearer rather
        // than the session cookie, so a browser already holding a session — full or locked — cannot
        // stand in for the provider here. And declaring a policy takes the route off the fallback, which
        // is right rather than worked around: the two requirements about session kinds judge the session
        // a request is served under, and this route serves none — it opens one. Whether the browser
        // already holds a session is a separate question, asked by the delegate itself before anything
        // runs, and its only effect is the 409 below. Not AllowAnonymous: the provider's signature is the
        // whole proof this route opens a session on.
        //
        // The SAME claim gate as registration, not a restatement of it: a token with no usable sub or
        // email, or whose address the provider does not vouch for, is refused here with registration's
        // own titles. One judgement in ProviderClaims, one filter applying it to a policy-authenticated
        // provider principal; a second filter would be the second copy ProviderClaims exists to prevent.
        //
        // This route creates no account. Registration stays the one path that does; an unknown subject
        // is a 404 naming no_account, and the client offers registration from there.
        endpoints.MapPost("/api/locked-session", async Task<Results<Ok<LockedSignInResponse>, ProblemHttpResult>> (
                HttpContext httpContext,
                ClaimsPrincipal principal,
                EstablishLockedSessionHandler handler,
                HttpResponse response,
                CancellationToken cancellationToken) =>
            {
                // FIRST, before the handler: a locked sign-in never replaces a live FULL session. The
                // cookie the browser holds opens everything and this sign-in would open less, so the
                // answer is a 409 that writes nothing, issues no cookie and leaves that session live. A
                // live LOCKED session is not refused — the new cookie overwrites it — and neither is a
                // request with no cookie or with an ended session: the cookie scheme answers NoResult for
                // both, so the result does not succeed.
                //
                // The cookie scheme authenticated here, by name, and that RESULT judged — never
                // HttpContext.User, which the policy replaced with the provider's principal, so it holds
                // no session at all. ProviderAuthorizationGate reads its scheme the same way, for the same
                // reason. The default scheme already ran this handler, and AuthenticationHandler caches
                // its result per request, so this costs no second lookup. A kind that does not read back
                // is judged full: refuse what cannot be read. The message names no subject and no address.
                AuthenticateResult cookie = await httpContext.AuthenticateAsync(
                    SessionCookieAuthenticationHandler.SchemeName);
                if (cookie is { Succeeded: true, Principal: { } cookiePrincipal }
                    && !(SessionCookieAuthenticationHandler.TryReadSessionKind(cookiePrincipal, out SessionKind kind)
                         && kind == SessionKind.Locked))
                {
                    throw new ConflictException(FullSessionMessage, ConflictKind.FullSession);
                }

                // The subject off the provider's principal — the principal the policy set, because it
                // names the provider scheme alone — and never off a body, nor off the cookie's principal
                // judged above, whose sub is an account id rather than a provider subject. The empty
                // fallback is unreachable past the claim gate and exists so the expression has a total
                // answer; an empty subject matches no credential.
                LockedSignInOutcome outcome = await handler.HandleAsync(
                    new EstablishLockedSessionCommand(
                        principal.FindFirstValue(ProviderClaims.SubjectClaimType) ?? string.Empty),
                    cancellationToken);

                switch (outcome)
                {
                    case LockedSignInOutcome.NoAccount:
                        // A refusal a client acts on — offer registration — so it names itself under the
                        // product's refusal member, and it repeats nothing the caller sent: neither the
                        // subject nor the address. No cookie: nothing was established.
                        return TypedResults.Problem(
                            title: NoAccountTitle,
                            statusCode: StatusCodes.Status404NotFound,
                            extensions: new Dictionary<string, object?> { [RefusalMember.Name] = NoAccountRefusal });

                    case LockedSignInOutcome.Established established:
                        // AFTER THE HANDLER RETURNED, from the handoff it returned, and only on this arm —
                        // so the 404 above can never carry a cookie naming a session nobody wrote.
                        SessionCookie.Issue(response, established.Handoff.Token, established.Handoff.ExpiresAtUtc);

                        // The kind converted here, at the boundary every establishing leg converts its
                        // own, so the wire reads "locked" as the column does. The erasure member is
                        // written as JSON null when nothing is filed, as GET /api/me/session writes it.
                        SessionSummary session = established.Session;
                        return TypedResults.Ok(new LockedSignInResponse(
                            JsonNamingPolicy.CamelCase.ConvertName(session.Kind.ToString()),
                            session.ExpiresAtUtc,
                            session.ErasureTakesEffectAtUtc is { } takesEffectAtUtc
                                ? new ScheduledErasureResponse(takesEffectAtUtc)
                                : null));

                    default:
                        throw new InvalidOperationException(
                            $"A locked sign-in outcome was added and nobody answered it: {outcome.GetType().Name}.");
                }
            })
            .RequireAuthorization(policy => policy
                .RequireAuthenticatedUser()
                .AddAuthenticationSchemes(ProviderAuthentication.SchemeName))
            // Reused under its registration name rather than renamed in this change; see the gate's
            // remarks for why the judgement is shared and the response shape is too.
            .AddEndpointFilter<RegistrationClaimGate>();

        return endpoints;
    }

    /// <summary>The <c>refusal</c> word a locked sign-in for a provider identity no account holds carries.</summary>
    internal const string NoAccountRefusal = "no_account";

    /// <summary>The title of that refusal; it names no subject and no address.</summary>
    internal const string NoAccountTitle = "No account is registered under this sign-in.";

    /// <summary>
    /// The detail of the 409 a locked sign-in over a live full session answers; it names no subject, no
    /// address and no account.
    /// </summary>
    internal const string FullSessionMessage =
        "This browser is already signed in with full access, so this sign-in would open less and was not used.";

    /// <summary>
    /// The session a locked sign-in opened, and the account's pending erasure — and deliberately no
    /// session, account or budget id, no address and no subject.
    /// </summary>
    /// <param name="Kind">The session's kind in the product's one spelling; always <c>"locked"</c> here,
    /// carried anyway so a client reads one shape off this route and off <c>GET /api/me/session</c>.</param>
    /// <param name="ExpiresAtUtc">The stored row's expiry, in UTC.</param>
    /// <param name="Erasure">The account's scheduled erasure, or <see langword="null" /> when it holds
    /// none.</param>
    /// <remarks>
    /// Flat rather than nested under a <c>session</c> member, the shape the passkey sign-in answers in;
    /// the erasure member is the one addition, because a person signing in on a provider alone is most
    /// often coming back to see — or file — exactly that.
    /// </remarks>
    private sealed record LockedSignInResponse(string Kind, DateTime ExpiresAtUtc, ScheduledErasureResponse? Erasure);

    /// <summary>
    /// The session the caller holds, and the account's pending erasure — and deliberately no session,
    /// account or budget id, no address and no credential.
    /// </summary>
    /// <param name="Kind">The session's kind in the product's one spelling: <c>"full"</c> or
    /// <c>"locked"</c>.</param>
    /// <param name="ExpiresAtUtc">The stored row's expiry, in UTC.</param>
    /// <param name="Erasure">The account's scheduled erasure, or <see langword="null" /> when it holds
    /// none.</param>
    private sealed record SessionResponse(string Kind, DateTime ExpiresAtUtc, ScheduledErasureResponse? Erasure);

    /// <summary>
    /// When the account's scheduled erasure takes effect, and nothing else about it.
    /// </summary>
    /// <param name="TakesEffectAtUtc">The stored instant the erasure takes effect, in UTC.</param>
    private sealed record ScheduledErasureResponse(DateTime TakesEffectAtUtc);
}
