using Application.Abstractions;
using Application.Sessions.AuthenticateSession;
using Domain.Sessions;

namespace Application.Sessions.DisplaceSession;

/// <summary>
/// Deletes the session a presented handle names, live or ended, on whichever account owns it. Answers
/// whether this call removed one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Run only after a new session has committed, and only in a dependency scope of its own.</b> The
/// caller is the API's <c>SessionCookieWriter</c>, the one caller of <c>SessionCookie.Issue</c>, which
/// runs this between the establishing handler's return and the new cookie. Both conditions are the
/// caller's to keep, and neither can be checked from here, so they are stated rather than assumed:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>After the commit</b>, because every refusal on an establishing path leaves before it, by
/// exception or by a returned outcome. Run earlier, a refused sign-in — a signature that does not
/// verify, a registration whose address is taken — would sign the browser out of the session it
/// already held, and a stranger holding nothing could do that to it. Measured for those two: displacing
/// before the handler reddens both tests that a refused sign-in leaves the presented session live.
/// </description></item>
/// <item><description>
/// <b>In its own scope</b>, because the identity it publishes is the presented session's owner, and
/// that can be another account than the one that just signed in. In the request's scope the
/// publication below would replace the new account's, and anything that request did afterwards would
/// run as somebody else — measured: every endpoint test passes that way, and only
/// <c>SessionCookieWriterTests</c> catches it. And the request's own publication cannot do the delete
/// instead: <c>sessions</c> is policed by <c>user_isolation</c>, so under the new account a row of
/// another account is not found — measured: deleting that way reddens the cross-account cases.
/// </description></item>
/// </list>
/// <para>
/// <b>It authenticates the handle through <see cref="AuthenticateSessionHandler"/> rather than looking
/// it up itself</b>, so the exempt read, the publication and the policed read keep their one owner and
/// their one order — ADR 0019's. That handler publishes the owner as soon as the handle is found and
/// before it judges liveness, and returns an ended session rather than nothing, which is what lets an
/// ended session be removed here too. The live arm also publishes the owner's budget; nothing below is
/// budget-scoped, so that is a read this call pays for and does not use.
/// </para>
/// <para>
/// <b>No transaction</b>, for the reason the authenticating handler gives: one opened before its
/// publication fails every policed statement inside it with <c>22P02</c>.
/// </para>
/// </remarks>
public sealed class DisplaceSessionHandler(
    AuthenticateSessionHandler authenticateSession,
    ISessionRepository sessionRepository) : ICommandHandler<DisplaceSessionCommand, bool>
{
    public async Task<bool> HandleAsync(
        DisplaceSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        AuthenticatedSession? presented = await authenticateSession.HandleAsync(
            new AuthenticateSessionCommand(command.TokenHash),
            cancellationToken);

        // A handle that names no session — never issued, or its row already gone — leaves nothing to
        // take. Measured: without this guard, a sign-in from a browser holding such a cookie answered
        // 500 after the new session committed. Liveness is deliberately not consulted: the browser is
        // about to stop presenting this handle either way, and an ended row is residue once it does —
        // one the ended-session sweep cannot reach when it belongs to another account.
        if (presented is null)
        {
            return false;
        }

        return await sessionRepository.RemoveAsync(presented.SessionId, cancellationToken);
    }
}
