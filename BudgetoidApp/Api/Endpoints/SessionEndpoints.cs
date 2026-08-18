using System.Security.Claims;
using Api.Infrastructure;
using Application.Sessions.RevokeSession;

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
        // No ProvisionsUserAttribute, and it must never gain one: signing out is the last request that
        // should be able to bring an account into existence. No AllowAnonymous either — the handle still
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
                // Absent on a request that authenticated some other way, which today means the bearer
                // bridge: there is no session row to end, so this ends nothing and still answers 204
                // and still clears the cookie. That branch leaves with the bridge.
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
            // And the one route a session that reads no budget content may reach. The two markers are
            // independent and this route happens to need both: the first is about a handle that is no
            // longer good, the second about a credential that never opened the account's keys.
            .WithMetadata(new AllowsLockedSessionAttribute());

        return endpoints;
    }
}
