namespace Domain.Users;

/// <summary>
/// What one attempt to save an email change came to: it landed, or it lost to a unique rule or to a
/// racing change of the same account.
/// </summary>
/// <remarks>
/// <b><see cref="SubjectTaken"/> is zero so that <see langword="default"/> is a refusal</b>, the
/// fail-closed direction <see cref="RegistrationOutcome"/> takes for the same reason. A member added
/// later goes on the end.
/// </remarks>
public enum EmailChangeOutcome
{
    /// <summary>
    /// A credential already holds the replacement's provider subject. <b>Ambiguous, and the caller
    /// resolves it</b> — another account's, or this account's own when a racing change filed it first.
    /// </summary>
    SubjectTaken = 0,

    /// <summary>Every write of the change landed.</summary>
    Applied,

    /// <summary>
    /// The address is spoken for. <b>Ambiguous, and the caller resolves it</b> — one save can breach
    /// the credential's <c>(provider, subject)</c> and the email at once, and PostgreSQL names only one,
    /// picked by write order, exactly as <see cref="RegistrationOutcome.EmailTaken"/> states.
    /// </summary>
    EmailTaken,

    /// <summary>
    /// The credential the change would retire is no longer the account's, because a racing change
    /// committed between this request's read and its save.
    /// </summary>
    FederatedCredentialMoved,
}

/// <summary>
/// Reads the signed-in account and its federated credential, and saves a
/// <see cref="FederatedIdentityChange"/> against them in one statement batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member takes the account id explicitly and the only caller reads it from the session.</b>
/// <c>credentials</c> is exempt from row-level security, so the owner in the predicate is the only thing
/// scoping the credential read; <c>users</c> is policed on the same id, so naming it costs nothing there.
/// </para>
/// <para>
/// <b>A refusal is reported rather than thrown, and the sentences live above</b> — the shape
/// <see cref="IRegistrationRepository"/> takes, for its reason: which collision a caller is told about
/// depends on what that caller was doing, and the repository knows only which rule refused the save.
/// </para>
/// </remarks>
public interface IEmailChangeRepository
{
    /// <summary>The account, or <see langword="null"/> when no row carries <paramref name="userId"/>.</summary>
    Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The account's federated credential, or <see langword="null"/> when it holds none.
    /// </summary>
    /// <remarks>
    /// Scoped on the owner <b>and</b> the type. The owner is the only thing narrowing a read of an exempt
    /// table; the type is what keeps a passkey or a recovery-code credential from ever being handed to
    /// <see cref="FederatedIdentityChange.Decide"/> as the one to retire.
    /// </remarks>
    Task<Credential?> FindFederatedCredentialAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the retired credential, inserts the filed one and moves the address, in one save, and
    /// reports which rule refused it when it was refused.
    /// </summary>
    /// <remarks>
    /// A refusal writes nothing: the whole save is refused, so no row of the change moved. Writes the
    /// caller made on earlier saves in the same unit of work are the caller's to roll back.
    /// </remarks>
    Task<EmailChangeOutcome> ApplyAsync(
        FederatedIdentityChange change,
        Guid userId,
        CancellationToken cancellationToken = default);
}
