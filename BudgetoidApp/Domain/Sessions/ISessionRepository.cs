namespace Domain.Sessions;

public interface ISessionRepository
{
    Task AddAsync(Session session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes, at <paramref name="revokedAtUtc"/>, only the sessions the credential named by
    /// <paramref name="credentialId"/> established, leaving every other credential's sessions on the
    /// same account alive. Returns how many sessions this call ended, which excludes any a
    /// concurrent revocation of the same credential ended first.
    /// </summary>
    /// <remarks>
    /// The count is returned rather than discarded because a caller that cannot say what a
    /// revocation did cannot report it — to the person who asked for it, or to anyone reading the
    /// record afterwards. It counts what this call did rather than what is true of the credential
    /// afterwards, so two concurrent revocations of one credential report a total of the sessions
    /// ended, not that number twice.
    /// </remarks>
    Task<int> RevokeForCredentialAsync(
        Guid credentialId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default);
}
