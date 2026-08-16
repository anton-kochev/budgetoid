namespace Domain.Sessions;

public interface ISessionRepository
{
    Task AddAsync(Session session, CancellationToken cancellationToken = default);

    /// <summary>
    /// The session named by <paramref name="sessionId"/>, or <see langword="null"/> when this request
    /// can see none under that id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No owner predicate, for the reason <see cref="RevokeAsync"/> gives.</b> <c>sessions</c> is
    /// <b>policed</b> by <c>user_isolation</c>, so PostgreSQL appends
    /// <c>user_id = current_setting('app.current_user_id')</c> underneath this read: somebody else's
    /// session is not found rather than found and then rejected. An owner filter above the policy would
    /// be a second source of tenancy able to disagree with it, and the first disagreement is a request
    /// that can read its own session through one and not the other.
    /// </para>
    /// <para>
    /// <b>The caller must have published the identity before calling this, and that ordering is the
    /// whole of ADR 0019.</b> The request arrives holding a cookie and nothing else, so the session id
    /// comes from <see cref="ISessionTokenRepository.FindByTokenHashAsync"/> on the exempt table — a
    /// read that runs before anyone is known — and only then is the owner it found published. Called
    /// first, this read meets <c>''::uuid</c> in the policy and raises <c>22P02</c>; called inside a
    /// transaction opened before the publication, so does every other policed statement in it.
    /// </para>
    /// <para>
    /// <b>It returns the entity rather than a verdict.</b> Whether a session is live is
    /// <see cref="Session.IsActiveAt"/>'s answer and it stays in the domain: a port member called
    /// <c>IsLiveAsync</c> would put "revoked or expired" in a second place, and the copy that drifts is
    /// the one deciding whether a request is authenticated. A <see langword="null"/> here is not a
    /// distinguishable answer either — never established, and belonging to another account, arrive the
    /// same way, which is what stops a caller learning that a session id is real but not theirs.
    /// </para>
    /// </remarks>
    Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

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
