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

    /// <summary>
    /// Revokes the single session named by <paramref name="sessionId"/> at
    /// <paramref name="revokedAtUtc"/>, reporting whether this call is the one that ended it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent, and it inherits that rather than restating it.</b>
    /// <see cref="Session.Revoke"/> keeps the instant access actually ended, so a second call over
    /// an already-revoked session changes nothing and answers <see langword="false"/> — which is
    /// the same reading <see cref="RevokeForCredentialAsync"/>'s count carries, one row wide: what
    /// <i>this</i> call ended, never what is true of the session afterwards. A caller reporting a
    /// revocation needs that distinction, and a caller retrying needs it not to lie.
    /// </para>
    /// <para>
    /// <b>Takes an id where the credential deletes take a loaded entity, and the difference is the
    /// policy.</b> <c>docs/decisions/0014-scope-the-credential-delete-in-the-application.md</c>
    /// demands the entity because <c>credentials</c> is exempt from row-level security, so nothing
    /// beneath the application scopes a statement issued by primary key. <c>sessions</c> is
    /// <b>policed</b> by <c>user_isolation</c>: both the read and the write carry
    /// <c>user_id = current_setting('app.current_user_id')</c> underneath, so a session belonging to
    /// somebody else is not found and cannot be written. Do not add an owner predicate above that —
    /// it would be a second source of tenancy able to disagree with the policy, and the first
    /// disagreement is a request that reads its own session and cannot end it.
    /// </para>
    /// <para>
    /// <b>A session this does not find is not a distinguishable answer.</b> Never established,
    /// already revoked, and belonging to another account all report <see langword="false"/>, so a
    /// caller cannot learn that a session id they named is real but not theirs.
    /// </para>
    /// </remarks>
    Task<bool> RevokeAsync(
        Guid sessionId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default);
}
