using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// The recovery-code tables in memory: the <c>credentials</c> row standing for a set, and the
/// <c>recovery_code_hashes</c> rows hanging off it.
/// </summary>
/// <remarks>
/// <para>
/// Two lists rather than one, and the split is the behaviour of the real stack that a retried unit of
/// work turns on. Rows a handler <em>seeded</em> are rows the database holds; rows it
/// <see cref="AddSetAsync" />ed inside the current unit of work are queued inserts living in the
/// change tracker, which a <c>ROLLBACK</c> never sees and which
/// <see cref="DiscardTrackedEntities" /> is what removes. A fake with one list would let a handler
/// that never discards look correct while production wrote two sets, and
/// <see cref="InMemorySessionRepository" /> models the same distinction for the same reason.
/// </para>
/// <para>
/// <see cref="FindRecoveryCodeCredentialAsync" /> reads the committed rows only. That is not an
/// omission: a query does not return an entity that is merely tracked as Added, so a fake answering
/// with pending rows would let a handler read back a set it had queued in this very attempt and call
/// it the account's existing one.
/// </para>
/// <para>
/// <paramref name="cascadeFromCredential" /> is the database's own <c>ON DELETE CASCADE</c> from
/// <c>credentials</c>, mirrored rather than stubbed —
/// <see cref="InMemoryPasskeyRepository.DeletePasskeyAsync" /> makes the same choice by holding its
/// three rows in one entry. It is supplied by the caller rather than wired to a session repository
/// here so that this fake keeps no opinion about sessions: it knows a credential row went, and the
/// test says what else the database would have taken with it.
/// </para>
/// </remarks>
public sealed class InMemoryRecoveryCodeRepository(Action<Credential>? cascadeFromCredential = null)
    : IRecoveryCodeRepository
{
    private readonly List<Set> _committed = [];
    private readonly List<Set> _pending = [];

    /// <summary>
    /// Every set the database would hold if this unit of work committed now — the rows already there
    /// plus the ones queued for insert.
    /// </summary>
    public IReadOnlyList<Credential> Credentials =>
        [.. _committed.Concat(_pending).Select(set => set.Credential)];

    /// <summary>Every unredeemed code the database would hold if this unit of work committed now.</summary>
    public IReadOnlyList<RecoveryCodeHash> Hashes =>
        [.. _committed.Concat(_pending).SelectMany(set => set.Hashes)];

    /// <summary>How many times a set was asked to be deleted.</summary>
    /// <remarks>
    /// Recorded because "the account had no previous set and none was deleted" is a claim about a call
    /// that did not happen, and the row counts cannot express it: an unconditional delete of a set
    /// that is not there leaves the same empty table an absent delete does.
    /// </remarks>
    public int DeleteSetCallCount { get; private set; }

    /// <summary>
    /// A question this fake asks once, at the moment <see cref="DeleteSetAsync" /> is entered and
    /// before anything is removed, so a test can observe the world exactly as the delete finds it.
    /// </summary>
    /// <remarks>
    /// Ordering is what this exists for, and a pair of call counters compared before and after cannot
    /// express it: two counters that both moved say both things happened, never that one preceded the
    /// other. The fake knows nothing about what is being asked — the caller supplies the predicate —
    /// which is the shape <see cref="InMemoryPasskeyRepository.ObserveAtDelete" /> already uses.
    /// </remarks>
    public Func<Credential, bool>? ObserveAtDelete { get; set; }

    /// <summary>
    /// What <see cref="ObserveAtDelete" /> answered, or <see langword="null" /> when the delete never
    /// ran at all — a distinction a plain <see langword="bool" /> could not make, and the two mean
    /// very different things to a test about ordering.
    /// </summary>
    public bool? ObservationAtDelete { get; private set; }

    /// <summary>Files a set the way a committed generation would have left it.</summary>
    public void Seed(Credential credential, IReadOnlyList<RecoveryCodeHash> hashes)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(hashes);

        _committed.Add(new Set(credential, [.. hashes]));
    }

    /// <summary>
    /// Forgets every set queued for insert, which is what clearing the change tracker does to rows a
    /// save has not written yet. Committed rows are untouched, because a discard is not a rollback of
    /// the database.
    /// </summary>
    public void DiscardTrackedEntities() => _pending.Clear();

    /// <summary>
    /// The account's set, if it holds one — with the type predicate the real query carries.
    /// </summary>
    /// <remarks>
    /// The type filter selects everything this fake is ever given, and is written out anyway for the
    /// reason <see cref="InMemoryPasskeyRepository.CountPasskeysForUserAsync" /> writes out its own:
    /// the day a passkey or a federated credential is seeded here is the day an unfiltered lookup
    /// starts answering a different question, and it would answer it by handing a caller the
    /// credential their Google sign-in hangs off.
    /// </remarks>
    public Task<Credential?> FindRecoveryCodeCredentialAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_committed
            .Find(set =>
                set.Credential.UserId == userId
                && set.Credential.Type == CredentialType.RecoveryCodes)
            ?.Credential);

    public Task AddSetAsync(
        Credential credential,
        IReadOnlyList<RecoveryCodeHash> hashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(hashes);

        _pending.Add(new Set(credential, [.. hashes]));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes the set's credential row and, with it, every unredeemed code hanging off it.
    /// </summary>
    /// <remarks>
    /// It takes the loaded entity and never an id, per ADR 0014, and the guarantee that buys is the
    /// narrow one <see cref="IPasskeyRepository.DeletePasskeyAsync" /> spells out:
    /// <see cref="Credential.CreateRecoveryCodes" /> is public, so a fabricated credential can reach
    /// this call — but it carries a freshly minted id naming no stored row, so the removal below
    /// matches nothing rather than taking a stranger's set. The one lookup that produces a credential
    /// this fake actually holds is <see cref="FindRecoveryCodeCredentialAsync" />, and it carries the
    /// owner in its predicate.
    /// </remarks>
    public Task DeleteSetAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // Asked first, before a single row moves: see ObserveAtDelete.
        ObservationAtDelete = ObserveAtDelete?.Invoke(credential);
        DeleteSetCallCount++;

        _committed.RemoveAll(set => set.Credential.Id == credential.Id);
        _pending.RemoveAll(set => set.Credential.Id == credential.Id);

        cascadeFromCredential?.Invoke(credential);

        return Task.CompletedTask;
    }

    private sealed record Set(Credential Credential, IReadOnlyList<RecoveryCodeHash> Hashes);
}
