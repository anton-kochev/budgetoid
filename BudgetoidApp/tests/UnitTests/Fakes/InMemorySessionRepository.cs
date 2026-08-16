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

    /// <summary>
    /// Forgets every session added so far, which is what clearing the change tracker does to a row
    /// that is still only queued for insert.
    /// </summary>
    /// <remarks>
    /// <see cref="Sessions"/> is the set of rows a save would write, so discarding is emptying it. A
    /// handler that adds a session inside a unit of work the provider replays adds a second one on
    /// the replay, and both are still queued when the surviving attempt commits — one sign-in, two
    /// rows. Only a fake that keeps them both can show that.
    /// </remarks>
    public void DiscardTrackedEntities() => _sessions.Clear();

    /// <summary>
    /// Removes every session the credential established, which is what the database's own
    /// <c>ON DELETE CASCADE</c> from <c>credentials</c> does when that row goes.
    /// </summary>
    /// <remarks>
    /// Here rather than assumed away, because a test about <em>ordering</em> around that delete is
    /// only honest if the cascade really happens. A handler that ended a credential's sessions
    /// <em>after</em> removing the credential would, against a fake that ignored the cascade, still
    /// find the rows and report a plausible number — the exact wrong implementation the ordering
    /// exists to refuse. With the cascade modelled it matches nothing and reports zero, which is also
    /// what deleting the revocation outright reports, so a test asserting the count reddens on both.
    /// <para>
    /// A caller wires this in; nothing here calls it. The credential row is not this repository's, and
    /// a fake that removed sessions on its own initiative would be inventing a rule.
    /// </para>
    /// </remarks>
    public void RemoveForCredential(Guid credentialId) =>
        _sessions.RemoveAll(session => session.CredentialId == credentialId);

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

    public Task<bool> RevokeAsync(
        Guid sessionId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        // Neither RevokeForCredentialCallCount nor LastRevokedAtUtc is touched. Both were added for
        // the credential sweep and every assertion on them today is about that sweep; a second writer
        // would make "the last call" mean two different things depending on which member ran.
        Session? session = _sessions.SingleOrDefault(session => session.Id == sessionId);

        // A session this does not hold is not a distinguishable answer from one already revoked, the
        // reading the real repository's own predicate produces: it narrows on id AND revoked_at_utc is
        // null, so never-established, already-revoked and belonging-to-somebody-else all fall out of
        // it together and all report false.
        if (session is null || session.RevokedAtUtc is not null)
        {
            return Task.FromResult(false);
        }

        // Session.Revoke keeps the first instant, so the answer has to be decided BEFORE the call
        // rather than read off the entity afterwards — the entity looks identically revoked either
        // way. That is the same reason RevokeForCredentialAsync above counts wasActive rather than
        // matched rows: what is being reported is what THIS call ended, never what is true of the
        // session afterwards.
        session.Revoke(revokedAtUtc);

        return Task.FromResult(true);
    }
}
