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
    /// The federated credential filed under <paramref name="provider"/> and <paramref name="subject"/>,
    /// or <see langword="null"/> when no account holds that provider identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The discovery read of the locked sign-in, and the credential rather than its owner.</b> It
    /// runs before the request has an identity, exactly as
    /// <see cref="FindUserIdByFederatedCredentialAsync"/> does, so it reads <c>credentials</c> alone —
    /// exempt from row-level security — and never joins <c>users</c>, which is policed on the very id
    /// being resolved. It publishes nothing; the caller decides what to do with the answer.
    /// </para>
    /// <para>
    /// <b>One read, never the id lookup followed by a credential read.</b> The session has to be opened
    /// over the credential the provider token named. Resolving the owner first and then fetching "the
    /// account's federated credential" in a second statement lets an email change that moves the Google
    /// identity between the two reads hand the session a credential the token never vouched for.
    /// </para>
    /// </remarks>
    Task<Credential?> FindFederatedCredentialBySubjectAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken = default);

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
