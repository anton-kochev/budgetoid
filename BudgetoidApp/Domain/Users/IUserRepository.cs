namespace Domain.Users;

public interface IUserRepository
{
    /// <summary>
    /// Resolves the user a federated credential points at, or <see langword="null"/> when no
    /// credential holds that <paramref name="provider"/> and <paramref name="subject"/> pair.
    /// </summary>
    Task<User?> FindByFederatedCredentialAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the user and the credential that resolves to it. Both rows are written in one save,
    /// so a refusal leaves neither behind. Returns <see langword="false"/> when the insert lost to an
    /// existing row on either unique rule — the credential's <c>(provider, subject)</c> or the user's
    /// email. Which of the two it was is deliberately not reported, because a losing insert can
    /// breach both at once; the caller decides by re-reading the credential, adopting the winning row
    /// when there is one.
    /// </summary>
    /// <remarks>
    /// The single save is load-bearing, not a convenience. A users row persisted without its
    /// credential would hold the unique email forever while no credential resolves to it, so every
    /// later sign-in with that address would be refused with a 409 and no way to heal.
    /// </remarks>
    Task<bool> TryAddAsync(User user, Credential credential, CancellationToken cancellationToken = default);
}
