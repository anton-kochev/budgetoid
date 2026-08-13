using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// The four passkey tables in memory, with the one behaviour of the real stack that a retried unit
/// of work turns on: a counter already materialised is handed back rather than read again.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FindCounterAsync"/> models the identity map. EF Core resolves a second read of a row it
/// is already tracking to the instance it holds, so an attempt that advanced the counter in memory
/// and then rolled back gets its own advanced instance back on the replay. A fake that materialised a
/// fresh counter every call would hide exactly the failure the discard exists to prevent.
/// </para>
/// <para>
/// <see cref="SaveCounterAsync"/> records the value it was asked to write and leaves the row where it
/// was, because every save a test drives through this fake happens inside a transaction whose commit
/// the test never reaches — a rolled-back attempt's UPDATE is not what the next read sees.
/// </para>
/// </remarks>
public sealed class InMemoryPasskeyRepository : IPasskeyRepository
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<Guid, PasskeySignatureCounter> _trackedCounters = [];
    private readonly List<uint> _savedCounterValues = [];

    /// <summary>Every counter value a save was asked to write, oldest first.</summary>
    public IReadOnlyList<uint> SavedCounterValues => _savedCounterValues;

    /// <summary>
    /// Every factor's share of the account keys this fake holds — one row per registered passkey.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read off the entries rather than kept in a list of its own, so it leaves when its credential
    /// does. The row cascades from <c>credentials</c> exactly as the public key and the counter do, and
    /// a fake that dropped three of the four would let a unit test of the revocation path pass while
    /// the real stack behaved differently.
    /// </para>
    /// <para>
    /// A passkey filed by <see cref="Register" /> carries none, which is
    /// <c>RepositoryTestHost.SeedPasskeyAsync</c>'s choice and not an oversight: the wrapped keys are a
    /// row of their own, seeded by a call of their own where a test needs one, and nothing this fake
    /// answers reads a seeded factor's envelopes. What the registration path writes is not a
    /// simplification, which is why <see cref="TryAddAsync" /> files it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<WrappedAccountKeys> WrappedKeys =>
        [.. _entries.Select(entry => entry.WrappedAccountKeys).OfType<WrappedAccountKeys>()];

    /// <summary>
    /// A question this fake asks once, at the moment <see cref="DeletePasskeyAsync"/> is entered and
    /// before it removes anything, so a test can observe the world exactly as the delete finds it.
    /// </summary>
    /// <remarks>
    /// Ordering is what this exists for, and a call counter compared before and after cannot express
    /// it: two counters that both moved prove both things happened, never that one preceded the
    /// other. A question answered <b>at</b> the delete does. The fake knows nothing about what is
    /// being asked — the caller supplies the predicate — so it stays a fake of the passkey tables
    /// rather than growing an opinion about sessions.
    /// </remarks>
    public Func<Credential, bool>? ObserveAtDelete { get; set; }

    /// <summary>
    /// What <see cref="ObserveAtDelete"/> answered, or <see langword="null"/> when the delete never
    /// ran at all — a distinction a plain <see langword="bool"/> could not make, and the two mean
    /// very different things to a test about ordering.
    /// </summary>
    public bool? ObservationAtDelete { get; private set; }

    /// <summary>Files a passkey the way a completed registration would have.</summary>
    public void Register(Credential credential, PasskeyPublicKey publicKey, uint signatureCounter)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(publicKey);

        _entries.Add(new Entry(credential, publicKey, signatureCounter, WrappedAccountKeys: null));
    }

    /// <summary>
    /// Forgets every counter materialised so far, which is what clearing the change tracker does to
    /// them. Wired to the fake <see cref="Application.Abstractions.IPersistenceState"/> by the test.
    /// </summary>
    public void DiscardTrackedEntities() => _trackedCounters.Clear();

    public Task<PasskeyPublicKey?> FindByWebAuthnCredentialIdAsync(
        ReadOnlyMemory<byte> webAuthnCredentialId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries
            .FirstOrDefault(entry => entry.PublicKey.WebAuthnCredentialId.Span.SequenceEqual(webAuthnCredentialId.Span))
            ?.PublicKey);

    /// <summary>
    /// The owner-scoped lookup, written with a real owner filter rather than delegating to the
    /// discovery finder above.
    /// </summary>
    /// <remarks>
    /// The filter is the behaviour under test, so it has to be here. A fake that forwarded to
    /// <see cref="FindByWebAuthnCredentialIdAsync"/> and ignored <paramref name="userId"/> would hand
    /// back another account's key and let every unit test of the account binding pass against a gate
    /// that had no binding at all — the exact defect the scoped finder exists to make unreachable.
    /// <para>
    /// Filtered on the <b>public key's</b> own owner, which is the column the real
    /// <c>PasskeyRepository</c> names. The credential beside it carries the same id today, so the two
    /// are interchangeable right up until they are not — and on that day a fake reading the credential
    /// would keep answering as though nothing had changed, which is the one thing a fake standing in
    /// for a policed table must never do.
    /// </para>
    /// </remarks>
    public Task<PasskeyPublicKey?> FindByWebAuthnCredentialIdForUserAsync(
        Guid userId,
        ReadOnlyMemory<byte> webAuthnCredentialId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries
            .FirstOrDefault(entry =>
                entry.PublicKey.UserId == userId
                && entry.PublicKey.WebAuthnCredentialId.Span.SequenceEqual(webAuthnCredentialId.Span))
            ?.PublicKey);

    /// <summary>
    /// The owner-scoped enumeration, filtered on the public key's own owner for the reason
    /// <see cref="FindByWebAuthnCredentialIdForUserAsync" /> gives: that is the column the real
    /// <c>PasskeyRepository</c> names, and the credential's copy of it is only identical until it
    /// is not.
    /// </summary>
    public Task<IReadOnlyList<ReadOnlyMemory<byte>>> ListWebAuthnCredentialIdsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReadOnlyMemory<byte>>>(
        [
            .. _entries
                .Where(entry => entry.PublicKey.UserId == userId)
                .Select(entry => entry.PublicKey.WebAuthnCredentialId),
        ]);

    /// <summary>
    /// Files the four rows a registration writes, or refuses the handle somebody else already holds.
    /// </summary>
    /// <remarks>
    /// <paramref name="wrappedAccountKeys" /> is stored beside the other three rather than accepted and
    /// dropped, for the reason the credential and its siblings are: the promise of the single save is
    /// that a factor cannot exist without its share of the account keys, and a fake that took the
    /// argument and forgot it would let a handler filing the envelopes against the wrong credential —
    /// or filing none at all — look correct from every assertion a unit test can make.
    /// </remarks>
    public Task<bool> TryAddAsync(
        Credential credential,
        PasskeyPublicKey publicKey,
        PasskeySignatureCounter counter,
        WrappedAccountKeys wrappedAccountKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(wrappedAccountKeys);

        bool taken = _entries.Exists(entry =>
            entry.PublicKey.WebAuthnCredentialId.Span.SequenceEqual(publicKey.WebAuthnCredentialId.Span));
        if (taken)
        {
            // Nothing is filed, which is the whole of the refusal: the real save writes the four rows
            // together or not at all, so a fake keeping the wrapped keys of a registration it turned
            // down would hold a row the database never saw.
            return Task.FromResult(false);
        }

        _entries.Add(new Entry(credential, publicKey, counter.Value, wrappedAccountKeys));

        return Task.FromResult(true);
    }

    /// <summary>
    /// All three predicates the real repository carries, the type one included.
    /// </summary>
    /// <remarks>
    /// <c>credentials</c> is exempt from row-level security, so the owner filter is the only thing
    /// scoping this read; and the type filter is what stops a federated credential from being
    /// resolved here and opening a session that claims a passkey established it. A fake that dropped
    /// either would let a unit test pass over a query that had.
    /// </remarks>
    public Task<Credential?> FindPasskeyCredentialAsync(
        Guid credentialId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries
            .FirstOrDefault(entry =>
                entry.Credential.Id == credentialId
                && entry.Credential.UserId == userId
                && entry.Credential.Type == CredentialType.Passkey)
            ?.Credential);

    /// <summary>
    /// How many <b>passkey</b> credentials the account holds — the number the "an account's last
    /// passkey cannot be revoked" floor is measured against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type predicate is the rule rather than tidiness. Every account also holds exactly one
    /// federated Google credential, so a count over <c>credentials</c> with no type filter reads two
    /// for an account standing on the floor and lets its last passkey go. A fake that counted its
    /// entries without the filter would let a unit test pass over a query that had dropped it.
    /// </para>
    /// <para>
    /// Only registered passkeys are filed here today, so the filter selects everything — written out
    /// anyway, because the day a federated credential is seeded into this fake is the day an unfiltered
    /// count starts answering a different question than the one the handler asks.
    /// </para>
    /// <para>
    /// Filtered on the <b>credential's</b> owner, not the public key's, unlike the two lookups above:
    /// the real query counts rows of <c>credentials</c>, and that is the column it names.
    /// </para>
    /// </remarks>
    public Task<int> CountPasskeysForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.Count(entry =>
            entry.Credential.UserId == userId
            && entry.Credential.Type == CredentialType.Passkey));

    /// <summary>
    /// Removes a passkey credential and, with it, the public key, the signature counter and the
    /// factor's wrapped account keys — the database's own <c>ON DELETE CASCADE</c>, mirrored rather
    /// than stubbed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="Entry" /> holds all four rows together, so dropping it is the cascade: a fake
    /// that removed the credential and left the counter behind would let a unit test of the revocation
    /// path pass while the real stack behaved differently — and the counter is the row that shape of
    /// error hides best, since nothing else reads it once the credential is gone.
    /// </para>
    /// <para>
    /// The materialised counter is forgotten with it. <see cref="FindCounterAsync" /> models the
    /// identity map, so leaving a tracked instance behind would hand a test a live counter for a
    /// passkey that no longer exists — an answer the real repository cannot give.
    /// </para>
    /// <para>
    /// It takes the loaded entity and never an id, exactly as
    /// <see cref="IPasskeyRepository.DeletePasskeyAsync" /> declares. Be precise about what that buys,
    /// because the appealing shorthand — that a <see cref="Credential" /> can only come from a lookup —
    /// is false: <c>Credential.CreateFederated</c> and <c>Credential.CreatePasskey</c> are both public,
    /// and the tests around this fake call them. What actually holds is narrower. Each factory mints its
    /// own <c>Guid.CreateVersion7()</c>, so a fabricated credential names no seeded entry at all: the
    /// <c>_entries.RemoveAll</c> below matches nothing rather than dropping another owner's row, and the
    /// real repository raises on that zero-row DELETE instead of removing a stranger's. The one lookup
    /// that hands back a credential this fake actually holds,
    /// <see cref="FindPasskeyCredentialAsync" />, carries the owner and the type in its filter.
    /// </para>
    /// <para>
    /// So the guarantee is only this: to reach this call with a credential naming a stored row of the
    /// caller's choosing, someone has to add a new lookup — here and on
    /// <c>Infrastructure.Repositories.PasskeyRepository</c>. That is a rule review enforces over two
    /// small classes, not a property of the type, and it is load-bearing because <c>credentials</c> is
    /// exempt from row-level security: the application's predicate is the only thing scoping a
    /// destructive statement against that table. A reader who believes the delete is unscoped-by-
    /// construction is the reader who waves through the query that removes the scope. See
    /// <c>docs/decisions/0014-scope-the-credential-delete-in-the-application.md</c>.
    /// </para>
    /// <para>
    /// It models only the case where the row is still there, and there is no hook to make it fail. The
    /// port's contract for a credential that is already gone is <c>NotFoundException</c>, and the
    /// translation from the persistence exception that produces it belongs to
    /// <c>Infrastructure.Repositories.PasskeyRepository</c> — so it is measured against a real database
    /// by <c>IntegrationTests.PasskeyRepositoryTests</c>, not invented here. A fake able to raise a
    /// persistence type this interface never surfaces would let a unit test prove a behaviour the real
    /// system does not have, and would drag the EF assembly back above Infrastructure to name it.
    /// </para>
    /// </remarks>
    public Task DeletePasskeyAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // Asked first, before a single row moves: see ObserveAtDelete.
        ObservationAtDelete = ObserveAtDelete?.Invoke(credential);

        _entries.RemoveAll(entry => entry.Credential.Id == credential.Id);
        _trackedCounters.Remove(credential.Id);

        return Task.CompletedTask;
    }

    public Task<PasskeySignatureCounter?> FindCounterAsync(
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        if (_trackedCounters.TryGetValue(credentialId, out PasskeySignatureCounter? tracked))
        {
            return Task.FromResult<PasskeySignatureCounter?>(tracked);
        }

        Entry? entry = _entries.Find(candidate => candidate.Credential.Id == credentialId);
        if (entry is null)
        {
            return Task.FromResult<PasskeySignatureCounter?>(null);
        }

        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(entry.Credential, entry.RowCounterValue);
        _trackedCounters[credentialId] = counter;

        return Task.FromResult<PasskeySignatureCounter?>(counter);
    }

    public Task SaveCounterAsync(PasskeySignatureCounter counter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counter);

        _savedCounterValues.Add(counter.Value);

        return Task.CompletedTask;
    }

    /// <summary>
    /// One registered passkey: the <c>credentials</c> row and everything hanging off it.
    /// </summary>
    /// <param name="WrappedAccountKeys">
    /// The factor's share of the account keys, or <see langword="null" /> for a passkey
    /// <see cref="Register" /> seeded — see <see cref="WrappedKeys" /> for why a seed files none.
    /// </param>
    private sealed record Entry(
        Credential Credential,
        PasskeyPublicKey PublicKey,
        uint RowCounterValue,
        WrappedAccountKeys? WrappedAccountKeys);
}
