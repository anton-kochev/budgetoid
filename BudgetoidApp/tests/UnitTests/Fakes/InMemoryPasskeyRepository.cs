using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// The three passkey tables in memory, with the one behaviour of the real stack that a retried unit
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

    /// <summary>Files a passkey the way a completed registration would have.</summary>
    public void Register(Credential credential, PasskeyPublicKey publicKey, uint signatureCounter)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(publicKey);

        _entries.Add(new Entry(credential, publicKey, signatureCounter));
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

    public Task<IReadOnlyList<ReadOnlyMemory<byte>>> ListWebAuthnCredentialIdsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReadOnlyMemory<byte>>>(
        [
            .. _entries
                .Where(entry => entry.Credential.UserId == userId)
                .Select(entry => entry.PublicKey.WebAuthnCredentialId),
        ]);

    public Task<bool> TryAddAsync(
        Credential credential,
        PasskeyPublicKey publicKey,
        PasskeySignatureCounter counter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(counter);

        bool taken = _entries.Exists(entry =>
            entry.PublicKey.WebAuthnCredentialId.Span.SequenceEqual(publicKey.WebAuthnCredentialId.Span));
        if (taken)
        {
            return Task.FromResult(false);
        }

        _entries.Add(new Entry(credential, publicKey, counter.Value));

        return Task.FromResult(true);
    }

    public Task<Credential?> FindPasskeyCredentialAsync(
        Guid credentialId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries
            .FirstOrDefault(entry => entry.Credential.Id == credentialId && entry.Credential.UserId == userId)
            ?.Credential);

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

    private sealed record Entry(Credential Credential, PasskeyPublicKey PublicKey, uint RowCounterValue);
}
