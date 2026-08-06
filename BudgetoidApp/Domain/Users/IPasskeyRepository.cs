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
    /// Resolves the public key registered under <paramref name="webAuthnCredentialId"/> <b>and</b>
    /// owned by <paramref name="userId"/>, or <see langword="null"/> when no passkey of that account
    /// answers to that handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pair with the lookup directly above, and the contrast between them is the point. That one
    /// runs before the request has an identity and is the single query in the codebase permitted to
    /// omit an owner filter; this one runs when an identity is already established, so it names the
    /// owner like every other read of an exempt table. An exempt table scopes nothing — no policy and
    /// no query filter narrows <c>passkey_public_keys</c> — so the predicate here is the only thing
    /// standing between a caller and somebody else's credential.
    /// </para>
    /// <para>
    /// It exists so that a handle belonging to another account is indistinguishable from a handle
    /// nothing answers to <b>by construction</b>, rather than by a comparison a later refactor can
    /// delete with one test noticing. Re-authentication before erasure is what needs that: the
    /// account being erased comes from the request, and the credential proving the person is present
    /// has to be one of that account's — reusing the discovery lookup here would verify a stranger's
    /// signature perfectly and then erase the caller's own account on the strength of it.
    /// </para>
    /// </remarks>
    Task<PasskeyPublicKey?> FindByWebAuthnCredentialIdForUserAsync(
        Guid userId,
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
