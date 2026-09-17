using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// The staging side of a content-key rotation, held in memory: every factor an account holds, and the
/// one staged generation — a row and its per-factor seals — that account has in flight.
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
/// </remarks>
public sealed class InMemoryKeyRotationRepository : IKeyRotationRepository
{
    private readonly Dictionary<Guid, KeyRotation> _staged = [];
    private readonly Dictionary<Guid, List<KeyRotationSeal>> _stagedSeals = [];
    private readonly Dictionary<Guid, Dictionary<Guid, WrappedAccountKeys>> _passkeyFactors = [];
    private readonly Dictionary<Guid, Dictionary<Guid, WrappedAccountKeys>> _recoveryCodeFactors = [];

    /// <summary>
    /// How many times a rotation was staged, whatever it replaced. Zero is what a refusal that
    /// happened before any write has to leave behind.
    /// </summary>
    public int StageCallCount { get; private set; }

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
    /// </remarks>
    public void SeedPasskeyFactor(Credential passkey, Guid factorId)
    {
        ArgumentNullException.ThrowIfNull(passkey);

        Seed(_passkeyFactors, passkey, factorId);
    }

    /// <summary>
    /// Files <paramref name="factorId" /> as one of the ten factors of a set of recovery codes the
    /// account holds — a row of <c>wrapped_account_keys</c> that really is there, and that this port
    /// <b>does</b> answer with, because a begin that skipped it would orphan the card.
    /// </summary>
    public void SeedRecoveryCodeFactor(Credential recoveryCodes, Guid factorId)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodes);

        Seed(_recoveryCodeFactors, recoveryCodes, factorId);
    }

    /// <summary>The recovery-code factors seeded on <paramref name="userId" />, oldest first.</summary>
    public IReadOnlyList<Guid> RecoveryCodeFactorsOf(Guid userId) =>
        _recoveryCodeFactors.TryGetValue(userId, out Dictionary<Guid, WrappedAccountKeys>? factors)
            ? [.. factors.Keys]
            : [];

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

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, WrappedAccountKeys>> ListFactorsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Asked first, before an answer is assembled: see ObserveAtListFactors.
        ObservationAtListFactors = ObserveAtListFactors?.Invoke();

        // The UNION of the two seedings, which is the whole of the port's contract. Written as an
        // explicit Add rather than a merge that overwrites, so a test that seeded one factor id under
        // both kinds fails loudly here — factor_id is the primary key of wrapped_account_keys, and two
        // rows claiming one factor is a database that has lost that key rather than a case to pick a
        // winner in.
        Dictionary<Guid, WrappedAccountKeys> answer = [];

        foreach (KeyValuePair<Guid, WrappedAccountKeys> factor in FactorsOf(_passkeyFactors, userId))
        {
            answer.Add(factor.Key, factor.Value);
        }

        foreach (KeyValuePair<Guid, WrappedAccountKeys> factor in FactorsOf(_recoveryCodeFactors, userId))
        {
            answer.Add(factor.Key, factor.Value);
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, WrappedAccountKeys>>(answer);
    }

    /// <inheritdoc />
    public Task<KeyRotation?> FindStagedRotationAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_staged.GetValueOrDefault(userId));

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

    private static Dictionary<Guid, WrappedAccountKeys> FactorsOf(
        Dictionary<Guid, Dictionary<Guid, WrappedAccountKeys>> seeded,
        Guid userId) =>
        seeded.TryGetValue(userId, out Dictionary<Guid, WrappedAccountKeys>? factors) ? factors : [];

    private static void Seed(
        Dictionary<Guid, Dictionary<Guid, WrappedAccountKeys>> seeded,
        Credential credential,
        Guid factorId)
    {
        if (!seeded.TryGetValue(credential.UserId, out Dictionary<Guid, WrappedAccountKeys>? factors))
        {
            factors = [];
            seeded[credential.UserId] = factors;
        }

        factors[factorId] = WrappedAccountKeys.For(
            credential,
            factorId,
            WrappedPrivateKey(Filler(factorId)),
            EncapsulatedAccountKeys(Filler(factorId)),
            SeedInstant);
    }

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
    /// visible by eye rather than being two identical buffers.
    /// </remarks>
    private static byte Filler(Guid factorId) => factorId.ToByteArray()[0];

    private static byte[] Payload(int length, byte version, byte filler)
    {
        byte[] payload = new byte[length];
        Array.Fill(payload, filler);
        payload[0] = version;

        return payload;
    }
}
