using Domain.Budgets;

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
    /// Inserts the whole account — the user, the credential that resolves to it, and the budget it
    /// owns — in one save, so a refusal leaves none of the three behind. Returns
    /// <see langword="false"/> when the insert lost to an existing row on either unique rule the
    /// account can collide on: the credential's <c>(provider, subject)</c> or the user's email. Which
    /// of the two it was is deliberately not reported, because a losing insert can breach both at
    /// once; the caller decides by re-reading the credential, adopting the winning row when there is
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single save is load-bearing, not a convenience, and the argument is the same for all three
    /// rows. A <c>users</c> row persisted without its credential would hold the unique email forever
    /// while no credential resolves to it, so every later sign-in with that address would be refused
    /// with a 409 and no way to heal. A <c>users</c> row persisted without its budget is a signed-in
    /// person whose every budget-scoped query comes back empty — which used to be answered by a heal
    /// on the resolve path, a <c>SELECT</c> charged to every authenticated request to repair a state
    /// this save makes unreachable.
    /// </para>
    /// <para>
    /// It also buys the caller's conflict re-read its soundness: a reported unique violation means
    /// the winning transaction committed, and that transaction contained the winner's budget row. So
    /// a loser — which wrote nothing at all — can read the winner's budget rather than mint one.
    /// </para>
    /// <para>
    /// <paramref name="defaultBudget"/> cannot breach <c>IX_budgets_user_id_name</c> on this path: its
    /// <c>user_id</c> is a <see cref="Guid.CreateVersion7()"/> minted for this call, so no other row
    /// can share it. A <c>23505</c> naming that index is therefore unreachable here, is not covered by
    /// the two names the implementation filters on, and propagates unhandled by design — the
    /// documented treatment for a unique rule this method does not model.
    /// </para>
    /// </remarks>
    Task<bool> TryAddAsync(
        User user,
        Credential credential,
        Budget defaultBudget,
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
