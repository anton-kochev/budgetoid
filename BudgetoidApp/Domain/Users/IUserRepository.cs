namespace Domain.Users;

public interface IUserRepository
{
    /// <summary>
    /// Resolves the id of the user a federated credential points at, or <see langword="null"/> when
    /// no credential holds that <paramref name="provider"/> and <paramref name="subject"/> pair.
    /// </summary>
    /// <remarks>
    /// An id rather than a <see cref="User"/>, because this call is what establishes the request's
    /// identity: it runs on a session that names nobody yet, and <c>users</c> is policed on exactly
    /// the identity it has not established, so reading that row here would refuse the question that
    /// produces the answer. Nothing is lost by not reading it — <c>credentials.user_id</c> is a NOT
    /// NULL foreign key to <c>users.id</c>, so the key already carries everything the row would
    /// prove about which account this is.
    /// </remarks>
    Task<Guid?> FindUserIdByFederatedCredentialAsync(
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

    /// <summary>
    /// Removes the user row, which cascades away everything that hangs off it. A row that is already
    /// gone is not an error: this states a post-condition rather than acting on a row, and a caller
    /// asking for an account to be absent has its answer either way. That covers a row another
    /// request erased while this one was in flight, not only one that was missing before it started
    /// — losing that race is the post-condition holding, not a failure to report.
    /// </summary>
    /// <remarks>
    /// Takes the id explicitly, unlike the budget-scoped repositories, which take none. Those are
    /// scoped by the <c>BudgetIsolation</c> query filter and would be handed a tenancy argument with
    /// no ownership check to pair with it; <c>users</c> carries no query filter at all, so the id has
    /// to be named and the <c>user_isolation</c> policy is what decides whether the named row is one
    /// this session may touch. That is why the only caller reads the id from <c>IUserContext</c>.
    /// </remarks>
    Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default);
}
