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

    /// <summary>
    /// How many credentials of type <see cref="CredentialType.Passkey"/> the account named by
    /// <paramref name="userId"/> holds — the number the "an account's last passkey cannot be revoked"
    /// floor is measured against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the repository rather than on <c>Application.Users.IUserAccountReadService</c>, which is
    /// where the credential list is projected from.</b> That interface says what it is for: reads that
    /// are for showing, not for deciding. This number decides, and a rule keyed on a value chosen for
    /// display is precisely what the split exists to prevent — the list is free to grow a page, an
    /// ordering, or a projection that folds away rows a person need not see, and none of that may be
    /// allowed to move the floor.
    /// </para>
    /// <para>
    /// The type predicate is the rule itself rather than tidiness. Every account also holds the
    /// federated Google credential provisioning minted for it, so a count with no type filter reads
    /// <b>two</b> for an account standing on the floor and lets its last passkey go — leaving somebody
    /// who can still sign in, still cannot reach any budget content, and cannot even prove presence
    /// for an erasure.
    /// </para>
    /// </remarks>
    Task<int> CountPasskeysForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a passkey credential, and with it everything the database hangs off that row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the loaded entity and never an id, and that signature is the design's main
    /// defence.</b> <see cref="Credential"/> has a private constructor and no setters, so the only way
    /// to hold one is <see cref="FindPasskeyCredentialAsync"/> — id, owner and type in one predicate.
    /// An unscoped delete is therefore <em>unavailable</em> rather than discouraged, which is what has
    /// to stand in for a policy: <c>credentials</c> is exempt from row-level security, so nothing
    /// beneath this call narrows the statement to the person asking. The same idiom
    /// <see cref="FindByWebAuthnCredentialIdForUserAsync"/> uses, and the argument for it is
    /// <c>docs/decisions/0014-scope-the-credential-delete-in-the-application.md</c>.
    /// </para>
    /// <para>
    /// What makes a delete by primary key sound once the read has scoped it is that
    /// <c>credentials.user_id</c> is immutable — the role holds no <c>UPDATE</c> of any shape — so the
    /// binding between an id and its owner cannot move between the read and the write.
    /// </para>
    /// <para>
    /// Only the <c>credentials</c> row is deleted. The public key, the signature counter and the
    /// sessions the credential opened leave by the database's own <c>ON DELETE CASCADE</c>, which runs
    /// as the table owner and is not a statement this role has to be granted — the role holds no
    /// <c>DELETE</c> on any of them, and <c>AppRoleGrantsTests</c> pins that absence deliberately.
    /// </para>
    /// <para>
    /// A credential that is already gone raises <see cref="Domain.Common.NotFoundException"/>, with the
    /// message <see cref="FindPasskeyCredentialAsync"/>'s caller uses for a miss. That is the whole of
    /// what this port surfaces about a lost delete race: the persistence exception behind it is an
    /// implementation's to translate, which keeps the storage assembly out of every caller and every
    /// fake standing in for this interface.
    /// </para>
    /// </remarks>
    Task DeletePasskeyAsync(Credential credential, CancellationToken cancellationToken = default);

    Task<PasskeySignatureCounter?> FindCounterAsync(
        Guid credentialId,
        CancellationToken cancellationToken = default);

    Task SaveCounterAsync(
        PasskeySignatureCounter counter,
        CancellationToken cancellationToken = default);
}
