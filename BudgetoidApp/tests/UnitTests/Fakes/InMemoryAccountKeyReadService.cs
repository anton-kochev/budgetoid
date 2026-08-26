using Application.AccountKeys;

namespace UnitTests.Fakes;

/// <summary>
/// The wrapped-key rows a handful of credentials hold, answered for the
/// <c>(user id, credential id)</c> pair it is asked about — and a record of every pair it was asked
/// about, because on this port the arguments are the thing that cannot be measured anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Asked" /> is the whole reason this is a recording fake rather than a stub.</b>
/// <c>wrapped_account_keys</c> is policed by <c>user_isolation</c>, so an implementation that dropped
/// the owner from its predicate would still never return another account's rows — the policy makes a
/// wrong query answer <em>empty</em>, not <em>incorrect</em>. There is therefore no database state
/// that tells the two apart and no integration test that can, which leaves the arguments themselves
/// as the only observable, and this list as the only place to observe them.
/// </para>
/// <para>
/// <b>Both halves of the predicate are honoured rather than accepted and dropped</b>, the shape
/// <see cref="InMemoryRecoveryCodeReadService" /> keeps for its own owner argument: a fake that
/// answered every seeded row to whoever asked would hand a handler reading the wrong credential —
/// the first session in the store, an empty id — a plausible non-empty answer, and the tests about
/// <em>which</em> credential was read would pass on an implementation that never looked at one.
/// </para>
/// <para>
/// <b>It does not re-sort, and that is deliberate.</b>
/// <see cref="IAccountKeyReadService.ListForCredentialAsync" /> promises rows ordered by
/// <see cref="FactorEnvelopes.FactorId" />, and what that promise buys is determinism rather than a
/// particular sequence — <c>uuid</c> collation orders bytes in PostgreSQL and
/// <see cref="Guid.CompareTo(Guid)" /> does not, so an in-memory sort and the database's may
/// disagree with neither being wrong. A fake that sorted would be asserting the port's promise by
/// restating it, exactly the trade <see cref="StubUserAccountReadService" /> refuses to make about
/// row-level security, and it would invite a test whose expectation is a sequence — the one shape
/// that breaks the day the same expectation is written against a real database. Rows come back in
/// the order they were seeded, so a handler is measured on whether it hands back everything it was
/// given.
/// </para>
/// </remarks>
public sealed class InMemoryAccountKeyReadService : IAccountKeyReadService
{
    private readonly List<(Guid UserId, Guid CredentialId, FactorEnvelopes Envelopes)> _rows = [];
    private readonly List<(Guid UserId, Guid CredentialId)> _asked = [];

    /// <summary>
    /// Every <c>(user id, credential id)</c> pair this service was asked about, oldest first.
    /// </summary>
    public IReadOnlyList<(Guid UserId, Guid CredentialId)> Asked => _asked;

    /// <summary>
    /// Files <paramref name="envelopes" /> under the account and credential that own it.
    /// </summary>
    /// <remarks>
    /// One row at a time and no overload taking a whole set, so a fixture holding ten factors under
    /// one credential has to say so ten times. The count is the thing several of these tests are
    /// about, and a seeding shortcut that took a collection would be a place for it to be quietly
    /// collapsed to one.
    /// </remarks>
    public void Seed(Guid userId, Guid credentialId, FactorEnvelopes envelopes) =>
        _rows.Add((userId, credentialId, envelopes));

    public Task<IReadOnlyList<FactorEnvelopes>> ListForCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        _asked.Add((userId, credentialId));

        IReadOnlyList<FactorEnvelopes> rows =
        [
            .. _rows
                .Where(row => row.UserId == userId && row.CredentialId == credentialId)
                .Select(row => row.Envelopes),
        ];

        return Task.FromResult(rows);
    }
}
