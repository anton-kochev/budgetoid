using Domain.Sessions;

namespace UnitTests.Fakes;

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly List<Session> _sessions = [];

    /// <summary>Every session this repository holds, in the order it was added.</summary>
    /// <remarks>
    /// Exposed because the sessions a revocation deliberately left alone are the interesting ones,
    /// and no method on the interface hands them back.
    /// </remarks>
    public IReadOnlyList<Session> Sessions => _sessions;

    /// <summary>How many times <see cref="RevokeForCredentialAsync"/> was asked to run.</summary>
    public int RevokeForCredentialCallCount { get; private set; }

    /// <summary>The instant the last call was told to revoke at.</summary>
    /// <remarks>
    /// Recorded so a test can pin where the clock is read. The real repository writes whatever
    /// instant it is handed; a caller that let the repository stamp its own <c>now</c> instead would
    /// spread one revocation sweep across as many instants as it touched rows.
    /// </remarks>
    public DateTime? LastRevokedAtUtc { get; private set; }

    public Task AddAsync(Session session, CancellationToken cancellationToken = default)
    {
        _sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task<int> RevokeForCredentialAsync(
        Guid credentialId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        RevokeForCredentialCallCount++;
        LastRevokedAtUtc = revokedAtUtc;

        int revoked = 0;

        foreach (Session session in _sessions.Where(session => session.CredentialId == credentialId))
        {
            // Counting the sessions that were still live rather than the rows that matched: a
            // re-run — a retry, or a second report of the same compromise — matches the same rows
            // and ends nothing, and a matched-row count would report it as having cut off access a
            // second time. The real repository's UPDATE narrows on revoked_at IS NULL for the same
            // reason, so the number it reports means the same thing this one does.
            bool wasActive = session.RevokedAtUtc is null;
            session.Revoke(revokedAtUtc);

            if (wasActive)
            {
                revoked++;
            }
        }

        return Task.FromResult(revoked);
    }
}
