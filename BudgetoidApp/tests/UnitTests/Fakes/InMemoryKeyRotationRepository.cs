using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// The staging side of a content-key rotation, held in memory: which factors an account's live
/// passkeys are filed under, and the one row — if any — that account has staged.
/// </summary>
/// <remarks>
/// <para>
/// <b>The staged rotations are keyed on the account, because <c>key_rotations.user_id</c> is the
/// primary key.</b> "At most one rotation in flight per account" is therefore a fact about the
/// dictionary here exactly as it is a fact about the table there, and a handler that staged twice for
/// one account cannot leave two rows behind in either place.
/// </para>
/// <para>
/// <b><see cref="StageAsync" /> replaces rather than refuses, which is the port's promise and not a
/// convenience of this fake.</b> Begin is the repair path — when a completion refuses because the live
/// factor set moved, the client re-posts a begin carrying the corrected set — so a second begin has to
/// go through. What this fake cannot show is <em>how</em> an implementation keeps that promise against
/// a primary key: a delete-then-insert and an upsert are indistinguishable from here, and a naive
/// insert would pass every case in <c>BeginKeyRotationHandlerTests</c> and meet <c>23505</c> in
/// PostgreSQL. That one belongs to the integration tier.
/// </para>
/// <para>
/// <b><see cref="ListPasskeyFactorsAsync" /> answers passkey factors and nothing else, and the
/// recovery-code factors seeded beside them are deliberately unreachable through it.</b> A set of
/// recovery codes is ten factors under one credential, so "the factor this rotation began under" would
/// have ten answers; and a begin is gated on a passkey assertion a set of codes cannot produce. Seeding
/// them at all is what lets a test say "the account genuinely holds this factor and the begin is still
/// refused" rather than the much weaker "the account holds no such factor".
/// </para>
/// </remarks>
public sealed class InMemoryKeyRotationRepository : IKeyRotationRepository
{
    private readonly Dictionary<Guid, KeyRotation> _staged = [];
    private readonly Dictionary<Guid, Dictionary<Guid, Credential>> _passkeyFactors = [];
    private readonly Dictionary<Guid, Dictionary<Guid, Credential>> _recoveryCodeFactors = [];

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
    /// Files <paramref name="factorId" /> as a live passkey factor of the account
    /// <paramref name="passkey" /> belongs to.
    /// </summary>
    public void SeedPasskeyFactor(Credential passkey, Guid factorId)
    {
        ArgumentNullException.ThrowIfNull(passkey);

        if (!_passkeyFactors.TryGetValue(passkey.UserId, out Dictionary<Guid, Credential>? factors))
        {
            factors = [];
            _passkeyFactors[passkey.UserId] = factors;
        }

        factors[factorId] = passkey;
    }

    /// <summary>
    /// Files <paramref name="factorId" /> as one of the ten factors of a set of recovery codes the
    /// account holds — a row of <c>wrapped_account_keys</c> that really is there, and that this port
    /// deliberately never answers with.
    /// </summary>
    public void SeedRecoveryCodeFactor(Credential recoveryCodes, Guid factorId)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodes);

        if (!_recoveryCodeFactors.TryGetValue(recoveryCodes.UserId, out Dictionary<Guid, Credential>? factors))
        {
            factors = [];
            _recoveryCodeFactors[recoveryCodes.UserId] = factors;
        }

        factors[factorId] = recoveryCodes;
    }

    /// <summary>The recovery-code factors seeded on <paramref name="userId" />, oldest first.</summary>
    public IReadOnlyList<Guid> RecoveryCodeFactorsOf(Guid userId) =>
        _recoveryCodeFactors.TryGetValue(userId, out Dictionary<Guid, Credential>? factors)
            ? [.. factors.Keys]
            : [];

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

    public Task<IReadOnlyDictionary<Guid, Credential>> ListPasskeyFactorsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, Credential>>(
            _passkeyFactors.TryGetValue(userId, out Dictionary<Guid, Credential>? factors)
                ? new Dictionary<Guid, Credential>(factors)
                : []);

    public Task<KeyRotation?> FindStagedRotationAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_staged.GetValueOrDefault(userId));

    public Task StageAsync(KeyRotation rotation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rotation);

        StageCallCount++;

        // Asked first, before a single row moves: see ObserveAtStage.
        ObservationAtStage = ObserveAtStage?.Invoke();

        // Keyed on the account, as the table is. Replacement rather than refusal — see the remarks.
        _staged[rotation.UserId] = rotation;

        return Task.CompletedTask;
    }
}
