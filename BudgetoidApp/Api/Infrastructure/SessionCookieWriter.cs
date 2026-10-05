using Application.Sessions;
using Application.Sessions.DisplaceSession;

namespace Api.Infrastructure;

/// <summary>
/// Hands a newly established session to the browser: deletes the session the browser's incoming cookie
/// named, then writes the cookie for the new one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overwriting a cookie does not, by itself, end the session it named.</b> The browser stops
/// presenting it, but its row stays, with no browser holding it — live until it expires or a revocation
/// ends it — a standing record that this browser was signed in to that account. The ended-session sweep
/// that runs when a session is established cannot reach it: the sweep takes ended rows only, and it runs
/// as the account signing in, while the cookie may name another account, whose rows
/// <c>user_isolation</c> hides. So the session the cookie named is displaced here: deleted, whichever
/// account owns it and whether or not it is still live.
/// </para>
/// <para>
/// <b>The only caller of <see cref="SessionCookie.Issue" />, so that every establishing path displaces.</b>
/// An endpoint writing the cookie itself would compile and still hand the browser a working cookie, and
/// would leave the overwritten session behind — reasoned, not run. What was run is the guard:
/// <c>SessionCookieIssueCensusTests</c> holds it to one file outside the endpoints — it counts files,
/// not call sites, so a second call in this one would pass it — and a direct <c>Issue</c> in an
/// endpoint reddens it.
/// </para>
/// </remarks>
public sealed class SessionCookieWriter(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Deletes the session <paramref name="context" />'s incoming cookie names, then writes
    /// <paramref name="handoff" /> onto its response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Call it only once the establishing handler has returned, and only on the arm that established
    /// a session.</b> Every refusal on those paths leaves by exception or by a returned outcome before
    /// this point, so a refused sign-in leaves the session the browser presented untouched. Run earlier,
    /// a signature that fails to verify, or a registration whose address is taken, would sign the
    /// browser out of the session it already had — and a stranger holding nothing could cause that.
    /// Measured for those two: displacing before the handler reddens both tests that a refused sign-in
    /// leaves the presented session live, in <c>PasskeyCeremonyTests</c> and
    /// <c>AccountRegistrationTests</c>.
    /// </para>
    /// <para>
    /// <b>The delete comes before the cookie.</b> If it throws, the request answers 500 after the new
    /// session committed, and no new cookie is written, because <see cref="SessionCookie.Issue" /> is
    /// never reached: the new session stands with no browser holding it, and the browser keeps its old
    /// cookie, whose session the failed delete left in place. Measured: swallowing the failure here
    /// turned that 500 into a 200 with the old session left live, so it must stay loud. Written first,
    /// the cookie would be on a response that is about to be replaced by an error.
    /// </para>
    /// <para>
    /// <b>The delete, not a revocation.</b> A revoked row stays until its own account next signs in and
    /// sweeps it, and when the cookie named somebody else, that may never happen.
    /// </para>
    /// </remarks>
    public async Task WriteEstablishedAsync(
        HttpContext context,
        SessionHandoff handoff,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handoff);

        // No cookie, or one that is not a handle this application could have issued: nothing to displace.
        // The reader is the session scheme's own, so a value the scheme's reader refuses is not one this
        // deletes by. Measured: a lenient decoder here, truncating to 32 bytes, deleted a live full
        // session presented as its handle with one byte appended. That is about decoding, not liveness:
        // a well-formed handle naming an ended session, which the scheme does not authenticate, is
        // still displaced. No check that the cookie names the new session: the handoff's handle was
        // minted on this request, and the incoming cookie was issued before it, so it never can.
        if (SessionCookie.TryReadTokenHash(context.Request, out byte[]? presentedHash))
        {
            // A CHILD SCOPE, because the delete has to run as the presented session's owner and the
            // request must not. The handler publishes that owner into the scope's CurrentUser, and the
            // scope's own DbContext and SessionContextInterceptor put it on the connection. In the
            // request's scope that publication would replace the new account's, so anything the request
            // did afterwards would act as somebody else; and the request's own publication cannot do the
            // delete either, since sessions is policed and a row of another account is not found under
            // it. Disposing the scope ends the publication with it. Measured both ways: deleting under
            // the request's publication reddens the cross-account cases, and resolving the handler from
            // the request's scope leaves the request published as the cookie's account, which only
            // SessionCookieWriterTests catches.
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            DisplaceSessionHandler displace = scope.ServiceProvider.GetRequiredService<DisplaceSessionHandler>();

            // Whether a row was removed does not change the response. A handle naming nothing, or a
            // session another request removed first, leaves the same browser holding the same new cookie.
            _ = await displace.HandleAsync(new DisplaceSessionCommand(presentedHash), cancellationToken);
        }

        SessionCookie.Issue(context.Response, handoff.Token, handoff.ExpiresAtUtc);
    }
}
