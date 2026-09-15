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
/// <para>
/// <b>The manifest is seeded per account and is absent until a caller seeds one</b>, which is the state
/// every account in every database is actually in: nothing writes a <c>factor_manifests</c> row. The
/// absent answer is <see langword="null" /> at epoch <see cref="NoManifestRotationEpoch" />, because
/// epoch 0 is the <em>absence</em> of a row rather than a generation — the floor a stored row may claim
/// is <c>FactorManifest.MinimumRotationEpoch</c>, which is 1, so the two can never be confused.
/// <c>AccountKeyCustody</c> argues both halves.
/// </para>
/// <para>
/// <b>Seeding a manifest is separate from seeding a factor, and deliberately so.</b> They are two
/// tables keyed on two different things — <c>factor_manifests</c> on the account, <c>wrapped_account_keys</c>
/// on the factor — and one seeding call taking both would make "an account with factors and no manifest"
/// awkward to arrange, which is the state this fake most often has to be in.
/// </para>
/// </remarks>
public sealed class InMemoryAccountKeyReadService : IAccountKeyReadService
{
    /// <summary>
    /// The epoch an account with no manifest row is answered at.
    /// </summary>
    /// <remarks>
    /// Written out as a literal rather than read off <c>FactorManifest.MinimumRotationEpoch - 1</c>: a
    /// drift in the domain's floor must not silently drag the absent answer along with it, which is the
    /// same arrangement <c>FactorManifestTests</c> keeps for the bounds it checks.
    /// </remarks>
    public const int NoManifestRotationEpoch = 0;

    private readonly List<(Guid UserId, Guid CredentialId, FactorEnvelopes Envelopes)> _rows = [];
    private readonly Dictionary<Guid, (ReadOnlyMemory<byte> Manifest, int RotationEpoch)> _manifests = [];
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

    /// <summary>
    /// Files the one <c>factor_manifests</c> row <paramref name="userId" /> holds.
    /// </summary>
    /// <remarks>
    /// An indexer rather than an add, because <c>PK_factor_manifests</c> is <c>user_id</c> and nothing
    /// else: an account holds at most one manifest, so a fake that accumulated two would offer an
    /// arrangement the schema refuses.
    /// </remarks>
    public void SeedManifest(Guid userId, ReadOnlyMemory<byte> manifest, int rotationEpoch) =>
        _manifests[userId] = (manifest, rotationEpoch);

    public Task<AccountKeyCustody> ListForAccountAsync(
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

        return Task.FromResult(_manifests.TryGetValue(userId, out (ReadOnlyMemory<byte> Manifest, int RotationEpoch) held)
            ? new AccountKeyCustody(held.Manifest, held.RotationEpoch, rows)
            : new AccountKeyCustody(null, NoManifestRotationEpoch, rows));
    }
}
