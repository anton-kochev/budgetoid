namespace Domain.Users;

/// <summary>
/// The <c>credentials</c> row standing for an account's set of recovery codes, and the
/// <c>recovery_code_hashes</c> rows hanging off it.
/// </summary>
public interface IRecoveryCodeRepository
{
    /// <summary>
    /// The credential standing for <paramref name="userId"/>'s set of recovery codes, or
    /// <see langword="null"/> when that account holds no set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a second producer of a <see cref="Credential"/> naming a real row</b> — the thing
    /// <c>docs/decisions/0014-scope-the-credential-delete-in-the-application.md</c> says review has to
    /// catch, because <c>credentials</c> is exempt from row-level security and a credential that
    /// reached <see cref="DeleteSetAsync"/> from an unscoped read would leave that delete scoped by
    /// nothing at all. The property survives here for the reason
    /// <see cref="IPasskeyRepository.FindPasskeyCredentialAsync"/>'s does: <b>the owner and the type
    /// are both in the predicate</b>, so what this can hand back is one of the caller's own sets or
    /// nothing. A lookup added below that returns a <see cref="Credential"/> without naming its owner
    /// is what would break the chain.
    /// </para>
    /// <para>
    /// The type predicate is a rule rather than tidiness. Every account also holds the federated
    /// Google credential provisioning minted for it, and — once the account registers one — passkeys;
    /// an untyped lookup would hand the caller whichever of those it found first and offer it to a
    /// delete.
    /// </para>
    /// <para>
    /// The entity rather than its id, because that is what <see cref="DeleteSetAsync"/> takes and what
    /// <see cref="RecoveryCodeHash.From"/> reads its three copied columns off.
    /// </para>
    /// </remarks>
    Task<Credential?> FindRecoveryCodeCredentialAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a set: the credential standing for it and one <see cref="RecoveryCodeHash"/> per code,
    /// in one save, so a refusal leaves none of them behind.
    /// </summary>
    /// <remarks>
    /// One save is the rule rather than an optimisation. A credential with no codes is a set that
    /// counts as issued and can never be redeemed, and codes with no credential are unstorable
    /// anyway — the composite foreign key to <c>credentials(id, user_id, type)</c> refuses them.
    /// </remarks>
    Task AddSetAsync(
        Credential credential,
        IReadOnlyList<RecoveryCodeHash> hashes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a set: the <c>credentials</c> row, and with it every unredeemed code hanging off it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the loaded entity and never an id</b>, per ADR 0014, and the guarantee that buys is
    /// the narrow one <see cref="IPasskeyRepository.DeletePasskeyAsync"/> spells out:
    /// <see cref="Credential.CreateRecoveryCodes"/> is public, so a fabricated credential can certainly
    /// reach this call — but it carries a freshly minted <c>Guid.CreateVersion7()</c> naming no stored
    /// row, so the delete matches nothing rather than taking a stranger's set. The one read producing a
    /// credential the table actually holds is <see cref="FindRecoveryCodeCredentialAsync"/>, and it
    /// carries the owner in its predicate.
    /// </para>
    /// <para>
    /// <b>The set's <c>recovery_code_hashes</c> rows are not named here, and must never be
    /// materialised on this path.</b> They leave by the database's own <c>ON DELETE CASCADE</c>, which
    /// runs with the referencing table owner's privileges. Load them and EF cascades into the copies it
    /// can see and emits its own <c>DELETE FROM recovery_code_hashes</c> — and unlike <c>sessions</c>,
    /// the app role <em>is</em> granted <c>DELETE</c> there, so that statement quietly succeeds and the
    /// rows leave by the application instead of by the cascade, with no SQLSTATE to say so. This
    /// signature — the set's credential and nothing else — is what keeps the temptation out of reach.
    /// </para>
    /// </remarks>
    Task DeleteSetAsync(Credential credential, CancellationToken cancellationToken = default);
}
