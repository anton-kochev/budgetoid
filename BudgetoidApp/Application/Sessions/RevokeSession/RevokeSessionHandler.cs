using Application.Abstractions;
using Domain.Sessions;

namespace Application.Sessions.RevokeSession;

/// <summary>
/// Ends one session, reporting whether this call is the one that ended it.
/// </summary>
/// <remarks>
/// <para>
/// Thin, and not redundant with the repository call it wraps, for the reason
/// <c>RevokeSessionsForCredentialHandler</c> gives: the clock is read here, so one decision to end
/// access is stamped with one instant. It also keeps the route delegate off a persistence port, which
/// <c>CompositionBoundaryTests</c> holds the API to.
/// </para>
/// <para>
/// <b>The <see cref="bool"/> is what this call did, never what is true of the session afterwards.</b>
/// <see cref="Session.Revoke"/> keeps the instant access actually ended, so a retry changes nothing and
/// answers <see langword="false"/> — the same reading the credential sweep's count carries, one row
/// wide. A caller reporting a revocation needs that distinction; the one caller there is deliberately
/// does not put it on the wire, and says why at its own call site.
/// </para>
/// <para>
/// <b>No transaction, and no need of one.</b> A single <c>UPDATE</c> is atomic on its own, and
/// <c>revoked_at_utc</c> is a concurrency token, so two calls racing do not overwrite each other's
/// instant — the loser matches nothing and is reported as having ended nothing. Wrapping this would
/// also put the whole authentication path's ordering trap back in play on a request that has already
/// published its identity for no gain.
/// </para>
/// </remarks>
public sealed class RevokeSessionHandler(
    ISessionRepository repository,
    TimeProvider timeProvider) : ICommandHandler<RevokeSessionCommand, bool>
{
    public Task<bool> HandleAsync(
        RevokeSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        return repository.RevokeAsync(command.SessionId, now, cancellationToken);
    }
}
