using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// The staging side of a content-key rotation, held in memory: every factor an account holds, the one
/// staged generation — a row and its per-factor seals — that account has in flight, and the one
/// <c>factor_manifests</c> row a completion promotes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The staged rotations are keyed on the account, because <c>key_rotations.user_id</c> is the
/// primary key.</b> "At most one rotation in flight per account" is therefore a fact about the
/// dictionary here exactly as it is a fact about the table there, and a handler that staged twice for
/// one account cannot leave two rows behind in either place. The seals hang off the same key, because
/// <c>key_rotation_seals</c> is keyed on <c>(user_id, factor_id)</c> and the run it belongs to is the
/// account's only one.
/// </para>
/// <para>
/// <b><see cref="StageAsync" /> replaces rather than refuses, which is the port's promise and not a
/// convenience of this fake.</b> Begin is the repair path — when a completion refuses because the live
/// factor set moved, the client re-posts a begin carrying the corrected set — so a second begin has to
/// go through. What this fake cannot show is <em>how</em> an implementation keeps that promise against
/// a primary key: a delete-then-insert and an upsert are indistinguishable from here, and a naive
/// insert would pass every case in <c>BeginKeyRotationHandlerTests</c> and meet <c>23505</c> in
/// PostgreSQL. The same blindness now reaches the children, and one level further: replacing the whole
/// seal list per account is <em>observationally identical</em> to the per-key converge the adapter
/// really performs, so nothing here can show that each factor takes exactly one statement, that the
/// statement is an <c>UPDATE</c> where a seal already stood and an <c>INSERT</c> where none did, or
/// that <b>no key ever takes a <c>DELETE</c> and an <c>INSERT</c> together</b> — which is the pair EF
/// batches in no guaranteed order and which is therefore a coin flip on <c>23505</c> against
/// <c>PK_key_rotation_seals</c>. Nor can it show that the role holds no <c>DELETE</c> on either table,
/// so the tidier spelling would fail with <c>42501</c> besides. All of that belongs to the integration
/// tier.
/// </para>
/// <para>
/// <b><see cref="ListFactorsAsync" /> answers <em>every</em> factor the account holds — the passkey
/// factors and the recovery-code factors together — and this paragraph reverses the one it
/// replaces.</b> The old text argued that recovery-code factors were seeded precisely so that they
/// would be <em>unreachable</em> through this port, because "the factor this rotation began under" has
/// ten answers for a set of codes and a set of codes produces no assertion for the gate to verify.
/// That was right while a begin named one factor. It is exactly backwards now: a run stages one
/// <see cref="KeyRotationSeal" /> per factor, needing nothing but each factor's public half, so a
/// begin that skipped the ten code factors is the <em>orphaning</em> the whole slice exists to
/// prevent — the promotion overwrites
/// <see cref="WrappedAccountKeys.EncapsulatedAccountKeys" /> for the passkey and leaves the card in
/// somebody's wallet holding a copy of a content key that opens nothing, still enrolled, with nothing
/// anywhere reporting it. So the code factors are reachable here, they are part of the set a begin is
/// judged against in both directions, and <b>a begin that omits them must be REFUSED</b>. The
/// passkey/recovery split survives only as a seeding convenience, so a test can say which kind of
/// factor it is arranging; it is no longer a visibility rule and must not become one again.
/// </para>
/// <para>
/// <b>The values are <see cref="WrappedAccountKeys" /> rows rather than <see cref="Credential" />s,
/// because <see cref="KeyRotationSeal.For" /> takes the loaded entity.</b> That factory reads the owner
/// off the rotation and the factor off the loaded row so that it can refuse a disagreement between the
/// two, and a credential could not stand in anyway: a set of recovery codes is ten factors under one
/// credential, so ten keys of this dictionary would answer with the same object. The rows are built
/// through <see cref="WrappedAccountKeys.For" /> — never by reflection — so a seeded factor is one the
/// application could really have written, and the two envelopes it carries are at the exact widths and
/// versions that factory refuses to bend.
/// </para>
/// <para>
/// <b>THE TWO FACTOR READS ARE NOT INTERCHANGEABLE, AND THIS FAKE IS BUILT SO THAT SWAPPING THEM
/// FAILS.</b> <see cref="ListFactorsAsync" /> is <c>AsNoTracking</c> in production, so it materialises
/// a <b>fresh, detached</b> instance on every call here too: a caller that mutated one would be
/// mutating an object no save will ever look at, which in production is a <c>200</c> that moved
/// nothing. <see cref="TrackFactorsAsync" /> is the identity map — the same instance every time, for
/// the reason <see cref="InMemoryFactorManifests.Find" /> gives about the manifest — so a completion
/// that promotes through it is promoting what the save will flush. Modelled this way, the whole
/// promotion is invisible through <see cref="FactorOf" /> when a handler reads the wrong member, which
/// is exactly what a reader of the production code cannot see.
/// </para>
/// <para>
/// <b>Nothing here commits, and that is the shared model rather than an omission.</b>
/// <see cref="PromoteAsync" /> counts its call and files nothing, exactly as
/// <see cref="InMemoryFactorManifests" /> holds no save and as
/// <c>InMemoryPasskeyRepository.DeletePasskeyAsync</c> declines to file the manifest it is handed: the
/// promoted instances are already visible through <see cref="FactorOf" /> and
/// <see cref="FactorManifestOf" />, and what a save does in production is commit them.
/// <see cref="DiscardTrackedEntities" /> is the rollback, for
/// <see cref="InMemoryFactorManifests.Discard" />'s reason — an abandoned attempt's UPDATE went back
/// with its transaction, so the stored row still holds <c>N</c> and the surviving attempt promotes it
/// exactly once. <b>The cost, stated so nobody reads a green bar as covering it:</b> a handler that
/// promoted every entity and never called <see cref="PromoteAsync" /> at all would satisfy every
/// byte-level read below, which is why <see cref="PromoteCallCount" /> and
/// <see cref="ObserveAtPromote" /> exist and why the cases that matter assert both.
/// </para>
/// </remarks>
public sealed class InMemoryKeyRotationRepository : IKeyRotationRepository
{
    private readonly Dictionary<Guid, KeyRotation> _staged = [];
    private readonly Dictionary<Guid, List<KeyRotationSeal>> _stagedSeals = [];
    private readonly Dictionary<Guid, List<Guid>> _passkeyFactors = [];
    private readonly Dictionary<Guid, List<Guid>> _recoveryCodeFactors = [];

    /// <summary>
    /// Every <c>wrapped_account_keys</c> row as the database holds it, keyed on the factor because
    /// <c>factor_id</c> is that table's primary key — so two rows claiming one factor is unreachable
    /// here for the same reason it is unreachable there.
    /// </summary>
    private readonly Dictionary<Guid, FactorRow> _factorRows = [];

    /// <summary>
    /// The instances the change tracker is holding, which is what a promotion mutates and what a save
    /// flushes. See the type's remarks for why this is separate from <see cref="_factorRows" />.
    /// </summary>
    private readonly Dictionary<Guid, WrappedAccountKeys> _trackedFactors = [];

    /// <summary>
    /// The account's one <c>factor_manifests</c> row, delegated rather than modelled again — see
    /// <see cref="InMemoryFactorManifests" />, which owns the identity map and the rollback that a
    /// second copy would be a second chance to get wrong.
    /// </summary>
    private readonly InMemoryFactorManifests _manifests = new();

    /// <summary>
    /// How many times a rotation was staged, whatever it replaced. Zero is what a refusal that
    /// happened before any write has to leave behind.
    /// </summary>
    public int StageCallCount { get; private set; }

    /// <summary>
    /// How many times a completion asked for its promotion to be saved. Zero is what every refusal has
    /// to leave behind, and it is <b>not</b> what says a promotion happened — see the type's remarks,
    /// where what this fake cannot see about a save is written out.
    /// </summary>
    public int PromoteCallCount { get; private set; }

    /// <summary>Every account's staged row, which is at most one each.</summary>
    public IReadOnlyCollection<KeyRotation> Staged => _staged.Values;

    /// <summary>
    /// A question this fake asks once, at the moment <see cref="StageAsync" /> is entered and before it
    /// writes anything, so a test can observe the world exactly as the write finds it.
    /// </summary>
    /// <remarks>
    /// <c>InMemoryPasskeyRepository.ObserveAtDelete</c> is the same device for a different question,
    /// and its remarks argue why a pair of counters compared before and after cannot express an
    /// ordering: two counters that both moved prove both things happened, never that one preceded the
    /// other. The fake knows nothing about what is being asked — the caller supplies the predicate —
    /// so it stays a fake of <c>key_rotations</c> rather than growing an opinion about transactions.
    /// </remarks>
    public Func<bool>? ObserveAtStage { get; set; }

    /// <summary>
    /// What <see cref="ObserveAtStage" /> answered, or <see langword="null" /> when nothing was ever
    /// staged — a distinction a plain <see langword="bool" /> could not make, and the two mean very
    /// different things to a test about where a write happened.
    /// </summary>
    public bool? ObservationAtStage { get; private set; }

    /// <summary>
    /// The same device at the far end of a run: asked once, at the moment <see cref="PromoteAsync" />
    /// is entered.
    /// </summary>
    /// <remarks>
    /// The question worth asking there is which side of the transactional delegate the promotion
    /// landed on. A promotion outside the unit of work is the one write in this product that cannot be
    /// undone by anything — it overwrites the only copies of the generation still in force — so a
    /// rollback that left it standing would leave the account sealed under a key no stored value
    /// encapsulates.
    /// </remarks>
    public Func<bool>? ObserveAtPromote { get; set; }

    /// <summary>
    /// What <see cref="ObserveAtPromote" /> answered, or <see langword="null" /> when nothing was ever
    /// promoted — a different failure from a promotion made on the wrong side of the delegate, so both
    /// have to be nameable.
    /// </summary>
    public bool? ObservationAtPromote { get; private set; }

    /// <summary>
    /// The same device at the other end of the handler: asked once, at the moment
    /// <see cref="ListFactorsAsync" /> is entered, which is the instruction before the factor-set gate
    /// and the only point in the run from which that gate's <em>position</em> is observable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate itself calls nothing, so there is no collaborator to hang an observation off. Its
    /// listing is called immediately before it and by nobody else, so "when this port was asked for the
    /// account's factors" is "when the gate ran" to within one statement — and the two facts a caller
    /// wants to pin about that instant are that the re-authentication has already happened and that the
    /// counting read has not.
    /// </para>
    /// <para>
    /// A predicate rather than two counters read after the fact, for <see cref="ObserveAtStage" />'s
    /// reason: counters compared before and after prove both things happened and never that one
    /// preceded the other.
    /// </para>
    /// </remarks>
    public Func<bool>? ObserveAtListFactors { get; set; }

    /// <summary>
    /// What <see cref="ObserveAtListFactors" /> answered, or <see langword="null" /> when the factors
    /// were never listed at all — which is a different failure from a gate in the wrong place, so both
    /// have to be nameable.
    /// </summary>
    public bool? ObservationAtListFactors { get; private set; }

    /// <summary>
    /// Files <paramref name="factorId" /> as a live passkey factor of the account
    /// <paramref name="passkey" /> belongs to.
    /// </summary>
    /// <remarks>
    /// The kind is a seeding convenience and no longer a visibility rule — see the type's remarks. What
    /// lands is a <see cref="WrappedAccountKeys" /> row built through its own factory, because that is
    /// what <see cref="KeyRotationSeal.For" /> is handed.
    /// <para>
    /// <paramref name="encapsulatedFiller" /> is what a completion test needs and a begin test does
    /// not: the promotion overwrites this row's encapsulated value, so the bytes it starts with have to
    /// be chosen to differ from the bytes the staged seal carries, or "promoted" and "left alone" read
    /// the same. Left unset it is derived from the factor id, which is what every existing caller
    /// wants.
    /// </para>
    /// </remarks>
    public void SeedPasskeyFactor(Credential passkey, Guid factorId, byte? encapsulatedFiller = null)
    {
        ArgumentNullException.ThrowIfNull(passkey);

        Seed(_passkeyFactors, passkey, factorId, encapsulatedFiller);
    }

    /// <summary>
    /// Files <paramref name="factorId" /> as one of the ten factors of a set of recovery codes the
    /// account holds — a row of <c>wrapped_account_keys</c> that really is there, and that this port
    /// <b>does</b> answer with, because a begin that skipped it would orphan the card.
    /// </summary>
    /// <remarks>
    /// <inheritdoc cref="SeedPasskeyFactor" path="/remarks/para" />
    /// </remarks>
    public void SeedRecoveryCodeFactor(
        Credential recoveryCodes,
        Guid factorId,
        byte? encapsulatedFiller = null)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodes);

        Seed(_recoveryCodeFactors, recoveryCodes, factorId, encapsulatedFiller);
    }

    /// <summary>The recovery-code factors seeded on <paramref name="userId" />, oldest first.</summary>
    public IReadOnlyList<Guid> RecoveryCodeFactorsOf(Guid userId) =>
        _recoveryCodeFactors.TryGetValue(userId, out List<Guid>? factors) ? [.. factors] : [];

    /// <summary>
    /// Every factor <paramref name="userId" /> holds, passkeys first — the set a completion's
    /// both-directions comparison is measured against.
    /// </summary>
    public IReadOnlyList<Guid> FactorsOf(Guid userId) =>
    [
        .. OrderedFactorIds(_passkeyFactors, userId),
        .. OrderedFactorIds(_recoveryCodeFactors, userId),
    ];

    /// <summary>
    /// Takes a factor row away, the way a revocation takes one away mid-run.
    /// </summary>
    /// <remarks>
    /// It leaves the staged seal standing, which in production the composite
    /// <c>ON DELETE CASCADE</c> from <c>wrapped_account_keys</c> would not — so an arrangement built on
    /// this is modelling a cascade that did not fire, and a case using it says so. What the arrangement
    /// is <em>for</em> is the other direction of the set comparison: without it, "a seal naming a
    /// factor the account no longer holds" is unreachable and the comparison could be written as one
    /// direction with nothing to notice.
    /// </remarks>
    public void RemoveFactor(Guid factorId)
    {
        if (!_factorRows.Remove(factorId, out FactorRow? row))
        {
            throw new InvalidOperationException("No such factor was seeded.");
        }

        _trackedFactors.Remove(factorId);
        OrderedFactorIdsFor(_passkeyFactors, row.Credential.UserId).Remove(factorId);
        OrderedFactorIdsFor(_recoveryCodeFactors, row.Credential.UserId).Remove(factorId);
    }

    /// <summary>
    /// The encapsulated account keys <paramref name="factorId" /> would hold if this unit of work
    /// committed now — the tracked instance's value when one has been materialised, and the stored
    /// row's otherwise.
    /// </summary>
    /// <remarks>
    /// <see cref="InMemoryFactorManifests.Current" />'s rule on the other half of a promotion, and the
    /// one read a completion test is really about: a refusal has to leave <b>every</b> one of these
    /// byte-identical, because a status assertion alone is satisfied by a handler that promoted and
    /// then threw.
    /// </remarks>
    public byte[]? FactorOf(Guid factorId) =>
        _trackedFactors.TryGetValue(factorId, out WrappedAccountKeys? tracked)
            ? tracked.EncapsulatedAccountKeys.ToArray()
            : _factorRows.TryGetValue(factorId, out FactorRow? row) ? [.. row.EncapsulatedAccountKeys] : null;

    /// <summary>
    /// The seals <paramref name="userId" /> currently has staged, in the order the last
    /// <see cref="StageAsync" /> was handed them.
    /// </summary>
    /// <remarks>
    /// Replaced wholesale on every stage, which is what lets a test observe that a second begin left
    /// one set behind rather than two. It is <em>not</em> a model of how the rows get there — see the
    /// type's remarks, where the per-key converge this cannot show is written out.
    /// </remarks>
    public IReadOnlyList<KeyRotationSeal> SealsOf(Guid userId) =>
        _stagedSeals.TryGetValue(userId, out List<KeyRotationSeal>? seals) ? seals : [];

    /// <summary>
    /// Seeds a rotation as already staged, which is the state a repair begin arrives in.
    /// </summary>
    /// <remarks>
    /// Seeded rather than staged through <see cref="StageAsync" /> so that
    /// <see cref="StageCallCount" /> still counts only what the handler under test did.
    /// </remarks>
    public void SeedStagedRotation(KeyRotation rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);

        _staged[rotation.UserId] = rotation;
    }

    /// <summary>
    /// Seeds the seals of an already-staged rotation, so a replacement can be seen replacing something.
    /// </summary>
    /// <remarks>
    /// Seeded for <see cref="SeedStagedRotation" />'s reason: a set arranged through
    /// <see cref="StageAsync" /> would be counted as work the handler did.
    /// </remarks>
    public void SeedStagedSeals(Guid userId, IReadOnlyList<KeyRotationSeal> seals)
    {
        ArgumentNullException.ThrowIfNull(seals);

        _stagedSeals[userId] = [.. seals];
    }

    /// <summary>Files the account's one manifest row, the way registration left it.</summary>
    public void SeedFactorManifest(Guid userId, byte[] manifest, int rotationEpoch) =>
        _manifests.Seed(userId, manifest, rotationEpoch);

    /// <summary>
    /// The manifest the account would hold if this unit of work committed now, or
    /// <see langword="null" /> when it holds none.
    /// </summary>
    public (byte[] Manifest, int RotationEpoch)? FactorManifestOf(Guid userId) =>
        _manifests.Current(userId);

    /// <summary>
    /// Forgets every materialised factor row — and every manifest instance with them — which is what
    /// clearing the change tracker does to them.
    /// </summary>
    /// <remarks>
    /// The rollback a replay needs, and the reason a completion converges instead of meeting
    /// <c>FactorManifest.Promote</c>'s own refusal on its second attempt.
    /// <see cref="InMemoryFactorManifests" /> carries the argument in full.
    /// </remarks>
    public void DiscardTrackedEntities()
    {
        _trackedFactors.Clear();
        _manifests.Discard();
    }

    /// <summary>
    /// Files every materialised row's current value as the stored row and forgets the instance, which
    /// is what committing a unit of work and ending the request scope do between two requests.
    /// </summary>
    /// <remarks>
    /// <b>Nothing a handler does calls this, and nothing ever should.</b> This fake models no save —
    /// see the type's remarks — so a test needing a <em>second</em> request has to say where the first
    /// one's commit happened, and this is that sentence. Folding it into
    /// <see cref="PromoteAsync" /> instead would make a replayed unit of work inexpressible: the
    /// abandoned attempt's UPDATE went back with its transaction, so the stored row still holds the old
    /// generation and the surviving attempt has to be able to promote it.
    /// </remarks>
    public void Commit()
    {
        foreach ((Guid factorId, WrappedAccountKeys tracked) in _trackedFactors)
        {
            _factorRows[factorId] = _factorRows[factorId] with
            {
                EncapsulatedAccountKeys = tracked.EncapsulatedAccountKeys.ToArray(),
            };
        }

        _trackedFactors.Clear();
        _manifests.Commit();
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, WrappedAccountKeys>> ListFactorsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Asked first, before an answer is assembled: see ObserveAtListFactors.
        ObservationAtListFactors = ObserveAtListFactors?.Invoke();

        // FRESH INSTANCES, BECAUSE THE PRODUCTION READ IS AsNoTracking. A caller that mutated one of
        // these would be mutating an object no save will look at, and modelling that here is what makes
        // a completion promoting through this member fail rather than pass. The UNION of the two
        // seedings, which is the whole of the port's contract, and written as an explicit Add so a
        // factor id seeded under both kinds fails loudly — factor_id is the primary key of
        // wrapped_account_keys, and two rows claiming one factor is a database that has lost that key
        // rather than a case to pick a winner in.
        Dictionary<Guid, WrappedAccountKeys> answer = [];

        foreach (Guid factorId in FactorsOf(userId))
        {
            answer.Add(factorId, Materialise(_factorRows[factorId]));
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, WrappedAccountKeys>>(answer);
    }

    /// <inheritdoc cref="ListFactorsAsync" />
    /// <remarks>
    /// THE IDENTITY MAP, which is the whole difference from the member above: the same instance every
    /// time, so a promotion is visible to the save that follows it.
    /// <see cref="InMemoryFactorManifests.Find" /> makes the same argument about the manifest, and its
    /// last sentence is the one that matters here — a member handing back a fresh entity per call would
    /// leave every test of the promotion passing over a handler that promoted nothing.
    /// </remarks>
    public Task<IReadOnlyDictionary<Guid, WrappedAccountKeys>> TrackFactorsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        Dictionary<Guid, WrappedAccountKeys> answer = [];

        foreach (Guid factorId in FactorsOf(userId))
        {
            if (!_trackedFactors.TryGetValue(factorId, out WrappedAccountKeys? tracked))
            {
                tracked = Materialise(_factorRows[factorId]);
                _trackedFactors[factorId] = tracked;
            }

            answer.Add(factorId, tracked);
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, WrappedAccountKeys>>(answer);
    }

    /// <summary>
    /// The seals <paramref name="userId" /> has staged, keyed on the factor each was encapsulated to.
    /// </summary>
    /// <remarks>
    /// Keyed rather than listed because a completion's question is a lookup — which seal does this
    /// factor adopt — and because <c>key_rotation_seals</c> is keyed on <c>(user_id, factor_id)</c>, so
    /// the key of this dictionary cannot collide for the same reason two rows cannot. The
    /// <c>Add</c> is deliberate for that reason.
    /// </remarks>
    public Task<IReadOnlyDictionary<Guid, KeyRotationSeal>> ListStagedSealsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        Dictionary<Guid, KeyRotationSeal> answer = [];

        foreach (KeyRotationSeal seal in SealsOf(userId))
        {
            answer.Add(seal.FactorId, seal);
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, KeyRotationSeal>>(answer);
    }

    /// <summary>
    /// The account's manifest row as the tracker holds it, or <see langword="null" /> when the account
    /// holds none.
    /// </summary>
    /// <remarks>
    /// The third verbatim copy of a member two other ports already declare, which is the shape
    /// production keeps on purpose — see <c>IRecoveryCodeRepository.FindFactorManifestAsync</c>, where
    /// the duplication is argued rather than apologised for.
    /// </remarks>
    public Task<FactorManifest?> FindFactorManifestAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_manifests.Find(userId));

    /// <inheritdoc />
    public Task<KeyRotation?> FindStagedRotationAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_staged.GetValueOrDefault(userId));

    /// <summary>
    /// The one save a completion makes: the promoted manifest and every promoted factor row, together.
    /// </summary>
    /// <remarks>
    /// Both arguments are required and neither is filed anywhere here, which is not the omission it
    /// looks like — it is <c>InMemoryPasskeyRepository.DeletePasskeyAsync</c>'s stance on the manifest
    /// it is handed. Every instance named has already been mutated in place and is already visible
    /// through <see cref="FactorOf" /> and <see cref="FactorManifestOf" />; what a save does in
    /// production is commit it. They are still taken and still null-checked, because a handler reaching
    /// this line without them is one that promoted nothing, and that is the failure worth naming.
    /// </remarks>
    public Task PromoteAsync(
        FactorManifest factorManifest,
        IReadOnlyList<WrappedAccountKeys> factors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factorManifest);
        ArgumentNullException.ThrowIfNull(factors);

        PromoteCallCount++;

        // Asked first, before anything is counted as done: see ObserveAtPromote.
        ObservationAtPromote = ObserveAtPromote?.Invoke();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StageAsync(
        KeyRotation rotation,
        IReadOnlyList<KeyRotationSeal> seals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        ArgumentNullException.ThrowIfNull(seals);

        StageCallCount++;

        // Asked first, before a single row moves: see ObserveAtStage.
        ObservationAtStage = ObserveAtStage?.Invoke();

        // Keyed on the account, as both tables are. Replacement rather than refusal — see the remarks.
        // The seals are copied rather than aliased, so a caller that reused its list for a second begin
        // cannot rewrite a set this fake has already accepted.
        _staged[rotation.UserId] = rotation;
        _stagedSeals[rotation.UserId] = [.. seals];

        return Task.CompletedTask;
    }

    /// <summary>
    /// The instant every seeded factor carries. Fixed, so nothing about a seeded row depends on the
    /// wall clock; the value is never read by anything the handler does.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 9, 10, 11, 12, 13, DateTimeKind.Utc);

    private static IReadOnlyList<Guid> OrderedFactorIds(
        Dictionary<Guid, List<Guid>> seeded,
        Guid userId) =>
        seeded.TryGetValue(userId, out List<Guid>? factors) ? factors : [];

    private static List<Guid> OrderedFactorIdsFor(Dictionary<Guid, List<Guid>> seeded, Guid userId)
    {
        if (!seeded.TryGetValue(userId, out List<Guid>? factors))
        {
            factors = [];
            seeded[userId] = factors;
        }

        return factors;
    }

    private void Seed(
        Dictionary<Guid, List<Guid>> seeded,
        Credential credential,
        Guid factorId,
        byte? encapsulatedFiller)
    {
        byte filler = encapsulatedFiller ?? Filler(factorId);

        if (!_factorRows.TryAdd(
                factorId,
                new FactorRow(credential, factorId, filler, EncapsulatedAccountKeys(filler))))
        {
            throw new InvalidOperationException("That factor is already seeded.");
        }

        OrderedFactorIdsFor(seeded, credential.UserId).Add(factorId);
    }

    /// <summary>Builds the entity a stored row stands for, through its own factory and never by reflection.</summary>
    private static WrappedAccountKeys Materialise(FactorRow row) =>
        WrappedAccountKeys.For(
            row.Credential,
            row.FactorId,
            WrappedPrivateKey(row.Filler),
            row.EncapsulatedAccountKeys,
            SeedInstant);

    /// <summary>
    /// A well-formed <c>wrapped_private_key</c>: the AEAD framing's version byte, then
    /// <paramref name="filler" /> to that column's exact width.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both numbers are read off <see cref="WrappedAccountKeys" /> rather than written out, which is the
    /// opposite of what a test asserting a width would do and is right here: this builder is not making
    /// a claim about the format, it is producing a value the factory accepts, and a literal would turn
    /// every seeding in the suite red the day a framing moved for a reason nobody here is measuring.
    /// </para>
    /// <para>
    /// <b>Two builders rather than one taking a width.</b> The two columns hold values of two different
    /// cryptographic suites at two different widths, and each builder reads the constants belonging to
    /// its own — one builder parameterised on a width would let a caller pair this suite's length with
    /// the other's version.
    /// </para>
    /// </remarks>
    private static byte[] WrappedPrivateKey(byte filler) =>
        Payload(
            WrappedAccountKeys.WrappedPrivateKeyLength,
            WrappedAccountKeys.WrappedPrivateKeyVersion,
            filler);

    /// <inheritdoc cref="WrappedPrivateKey" />
    private static byte[] EncapsulatedAccountKeys(byte filler) =>
        Payload(
            WrappedAccountKeys.EncapsulatedAccountKeysLength,
            WrappedAccountKeys.EncapsulatedAccountKeysVersion,
            filler);

    /// <summary>
    /// What tells one seeded factor's envelopes from another's: a byte off the factor identifier, so
    /// two rows of one account do not carry identical payloads.
    /// </summary>
    /// <remarks>
    /// Nothing in this fake compares them — it exists so that a row read back under the wrong factor is
    /// visible by eye rather than being two identical buffers. A completion test wants a value it
    /// chose instead, which is what the seeding overloads' filler parameter is for.
    /// </remarks>
    private static byte Filler(Guid factorId) => factorId.ToByteArray()[0];

    private static byte[] Payload(int length, byte version, byte filler)
    {
        byte[] payload = new byte[length];
        Array.Fill(payload, filler);
        payload[0] = version;

        return payload;
    }

    /// <summary>One stored <c>wrapped_account_keys</c> row: what it was filed with, and what it holds now.</summary>
    private sealed record FactorRow(
        Credential Credential,
        Guid FactorId,
        byte Filler,
        byte[] EncapsulatedAccountKeys);
}
