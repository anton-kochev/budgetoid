using Application.AccountKeys;

namespace UnitTests.Fakes;

/// <summary>
/// The wrapped-key rows a handful of accounts hold, answered for the account it is asked about — and a
/// record of every account it was asked about, because on this port the argument is the thing that
/// cannot be measured anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Asked" /> is the whole reason this is a recording fake rather than a stub.</b>
/// <c>wrapped_account_keys</c> is policed by <c>user_isolation</c>, so an implementation that dropped
/// the owner from its predicate would still never return another account's rows — the policy makes a
/// wrong query answer <em>empty</em>, not <em>incorrect</em>. There is therefore no database state
/// that tells the two apart and no integration test that can, which leaves the argument itself as the
/// only observable, and this list as the only place to observe it.
/// </para>
/// <para>
/// <b>The owner is honoured rather than accepted and dropped</b>, the shape
/// <see cref="InMemoryRecoveryCodeReadService" /> keeps for its own owner argument: a fake that
/// answered every seeded row to whoever asked would hand a handler reading the wrong account — an
/// empty id, a value taken off something other than the context — a plausible non-empty answer, and
/// the test about <em>which</em> account was read would pass on an implementation that never looked at
/// one.
/// </para>
/// <para>
/// <b>The credential is seeded and deliberately not filtered on, and both halves of that are
/// decisions.</b> <see cref="IAccountKeyReadService.ListForAccountAsync" /> reads by account and the
/// port's own remarks forbid a member that narrows it to a credential, so a fake that filtered by one
/// would make a credential-narrowed handler green — the exact implementation this port was changed to
/// refuse. It is still named at the seam because a fixture's arrangement has to be able to say
/// <em>one account, two credentials, eleven factors</em>: that is the shape every real account is in,
/// it is what the widening is about, and a seeding call that took no credential would render it
/// identically to eleven factors filed under one.
/// </para>
/// <para>
/// <b>It does not re-sort, and that is deliberate.</b>
/// <see cref="IAccountKeyReadService.ListForAccountAsync" /> promises rows ordered by
/// <see cref="FactorEnvelopes.FactorId" />, and what that promise buys is determinism rather than a
/// particular sequence — the corrected argument for that, including what
/// <see cref="Guid.ToByteArray()" /> does that <see cref="Guid.CompareTo(Guid)" /> does not, is stated
/// once on <c>GetAccountKeysHandler</c> and is not restated here. A fake that sorted would be
/// asserting the port's promise by restating it, exactly the trade
/// <see cref="StubUserAccountReadService" /> refuses to make about row-level security, and it would
/// invite a test whose expectation is a sequence. Rows come back in the order they were seeded, so a
/// handler is measured on whether it hands back everything it was given.
/// </para>
/// </remarks>
public sealed class InMemoryAccountKeyReadService : IAccountKeyReadService
{
    private readonly List<(Guid UserId, Guid CredentialId, FactorEnvelopes Envelopes)> _rows = [];
    private readonly List<Guid> _asked = [];

    /// <summary>
    /// Every account id this service was asked about, oldest first.
    /// </summary>
    public IReadOnlyList<Guid> Asked => _asked;

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

    public Task<IReadOnlyList<FactorEnvelopes>> ListForAccountAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        _asked.Add(userId);

        IReadOnlyList<FactorEnvelopes> rows =
        [
            .. _rows
                .Where(row => row.UserId == userId)
                .Select(row => row.Envelopes),
        ];

        return Task.FromResult(rows);
    }
}
