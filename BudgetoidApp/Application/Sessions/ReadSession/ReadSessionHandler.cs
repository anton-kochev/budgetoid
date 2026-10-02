using Application.Abstractions;
using Domain.Erasure;
using Domain.Sessions;

namespace Application.Sessions.ReadSession;

/// <summary>
/// Answers the kind and expiry of the named session, and the instant the account's scheduled erasure
/// takes effect, if one is filed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The schedule is read for every kind of session.</b> Only a locked session can file one, but the
/// erasure belongs to the account: somebody who files it and then finds a passkey signs in full while
/// the instant still stands, and that tab has to see it too.
/// </para>
/// <para>
/// <b>The schedule is read by <see cref="IUserContext.UserId" /></b>, through the repository's explicit
/// owner predicate, and never by the session row's own <c>UserId</c>: the identity is what the request
/// published, and the session read is scoped by the same policy underneath.
/// </para>
/// <para>
/// <b>A missing session row throws</b>, the shape <c>GetSignedInUserHandler</c> gives its missing user
/// row. The route cannot reach it — the cookie handler has just read that very row — so a null is read
/// skew against a revocation or an erasure, and inventing a summary for a session nobody holds would
/// hide it.
/// </para>
/// <para>
/// <b>It takes no <c>ILogger</c></b>, for the reason <c>ScheduleErasureHandler</c> gives: it holds the
/// id of an account that may have asked to be forgotten. <b>No transaction</b> either: the two reads
/// depend on nothing in each other, and a transaction would buy a READ COMMITTED snapshot that closes
/// nothing.
/// </para>
/// </remarks>
public sealed class ReadSessionHandler(
    ISessionRepository sessions,
    IErasureScheduleRepository schedules,
    IUserContext userContext)
    : IQueryHandler<ReadSessionQuery, SessionSummary>
{
    public async Task<SessionSummary> HandleAsync(
        ReadSessionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        Session session = await sessions.FindByIdAsync(query.SessionId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The session this request authenticated with answers to no session row.");

        ErasureSchedule? schedule = await schedules.FindAsync(userContext.UserId, cancellationToken);

        return new SessionSummary(session.Kind, session.ExpiresAtUtc, schedule?.TakesEffectAtUtc);
    }
}
