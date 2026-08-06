namespace Domain.Users;

public interface IPasskeyRepository
{
    /// <summary>
    /// Resolves the public key registered under <paramref name="webAuthnCredentialId"/>, or
    /// <see langword="null"/> when no passkey answers to that handle.
    /// </summary>
    /// <remarks>
    /// This call runs before the request has an identity — the handle the client sent is the only
    /// thing naming an account, and the answer is what establishes who is asking. That is why the
    /// table it reads is exempt from row-level security, and why this is the one query against that
    /// table permitted to omit an owner filter. Every other read of it names the user it belongs to.
    /// </remarks>
    Task<PasskeyPublicKey?> FindByWebAuthnCredentialIdAsync(
        ReadOnlyMemory<byte> webAuthnCredentialId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The handles of every passkey already registered to <paramref name="userId"/>, which a
    /// registration ceremony offers back so an authenticator does not enrol itself twice.
    /// </summary>
    Task<IReadOnlyList<ReadOnlyMemory<byte>>> ListWebAuthnCredentialIdsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the credential, its public key and its signature counter. All three rows are written
    /// in one save, so a refusal leaves none of them behind. Returns <see langword="false"/> when the
    /// insert lost to an existing row on the WebAuthn credential id, meaning that handle is already
    /// registered.
    /// </summary>
    Task<bool> TryAddAsync(
        Credential credential,
        PasskeyPublicKey publicKey,
        PasskeySignatureCounter counter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the passkey credential named by <paramref name="credentialId"/> and owned by
    /// <paramref name="userId"/>, or <see langword="null"/> when no such row exists.
    /// </summary>
    /// <remarks>
    /// The owner is a parameter rather than left to the database because <c>credentials</c> is exempt
    /// from row-level security (ADR 0011) — nothing beneath this call narrows the read, so the filter
    /// here is the only thing scoping it. Sign-in needs the credential itself, not its id, because
    /// <see cref="Domain.Sessions.Session.Establish"/> derives how much of the account a session
    /// reaches from the credential that opened it.
    /// </remarks>
    Task<Credential?> FindPasskeyCredentialAsync(
        Guid credentialId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<PasskeySignatureCounter?> FindCounterAsync(
        Guid credentialId,
        CancellationToken cancellationToken = default);

    Task SaveCounterAsync(
        PasskeySignatureCounter counter,
        CancellationToken cancellationToken = default);
}
