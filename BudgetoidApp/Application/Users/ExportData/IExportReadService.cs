namespace Application.Users.ExportData;

/// <summary>
/// The read an export is assembled from.
/// </summary>
/// <remarks>
/// <para>
/// <b>One method, because the document has to be one state.</b> The account, its owned budgets and the
/// five collections inside the ambient budget are read inside one <c>REPEATABLE READ</c> transaction,
/// so a write committing while the export runs is either wholly in the answer or wholly out of it. As
/// separate methods each ran at <c>READ COMMITTED</c>, and PostgreSQL takes a fresh snapshot per
/// statement there, so a transaction could arrive naming an account the account array did not carry.
/// </para>
/// <para>
/// The decision about <em>which</em> budgets an export may contain still lives in the handler — where
/// a hand-written fake can put it under test — and it is made over the owned set this snapshot
/// carries, so the refusal and the document decide on the same state.
/// </para>
/// <para>
/// It takes the user id and no budget id, and the asymmetry is the tenancy model rather than an
/// oversight. <c>users</c> and <c>budgets</c> carry no EF query filter and are policed on
/// <c>user_id</c> in the database, so an owner predicate is what makes those two reads selective;
/// everything inside a budget is scoped by the <c>BudgetIsolation</c> filter and the
/// <c>budget_isolation</c> policy, both keyed on the ambient budget. A budget id parameter here would
/// be a tenancy argument with no ownership check to pair with it — the reason
/// <c>IBudgetRepository.HasTransactionsAsync</c> and
/// <c>ITransactionRepository.DeleteAllForAmbientBudgetAsync</c> take none either.
/// </para>
/// </remarks>
public interface IExportReadService
{
    /// <summary>
    /// The account <paramref name="userId" /> names, every budget it owns and everything filed under
    /// the ambient budget, all read from one database snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every collection — the owned budgets and the five inside the ambient one — ascends by
    /// <c>CreatedAtUtc</c>, and that ascent is the contract. <c>Id</c> breaks ties deterministically but
    /// is not part of it: <c>uuid</c> collation is provider-defined, so two rows sharing an instant may
    /// order one way here and another way under a second implementation, with neither being wrong.
    /// Never <c>Id</c> alone, though: UUID v7 sorts by creation time under PostgreSQL's <c>uuid</c> byte
    /// order but not under .NET's <see cref="Guid.CompareTo(Guid)" />. It is stated here rather than
    /// left to the implementation because the order rows come back in is what a caller restoring from
    /// a saved file has to reconstruct creation sequence from — nothing else in the document records
    /// it. <c>IBudgetRepository.FindFirstForUserAsync</c> states the same rule as contract.
    /// </para>
    /// <para>
    /// The contents are read before the handler decides whether the owned set lets it answer, which
    /// is the price of deciding on the same snapshot: a refused export has already read the ambient
    /// budget's rows into memory, and discards them.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Called while the context is already inside a transaction, whose isolation level this read would
    /// otherwise silently inherit.
    /// </exception>
    Task<ExportSnapshot> ReadSnapshotAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything an export is assembled from, as read from one database snapshot.
/// </summary>
/// <remarks>
/// Not a wire record: nothing serializes this. It exists so the three parts travel together and so the
/// handler's refusal is made over the owned set read in the same snapshot as the contents it guards.
/// </remarks>
/// <param name="User">
/// The account's row, or <see langword="null" /> when no row answers to the id — a broken invariant
/// the handler throws on, not a state the product can produce.
/// </param>
/// <param name="OwnedBudgets">Every budget the account owns, ascending by creation.</param>
/// <param name="Contents">Everything filed under the ambient budget.</param>
public sealed record ExportSnapshot(
    ExportedUser? User,
    IReadOnlyList<ExportedBudget> OwnedBudgets,
    ExportedBudgetContents Contents);

/// <summary>
/// The five collections one budget holds, as one value.
/// </summary>
/// <remarks>
/// Not a wire record: nothing serializes this. It exists so the five reads travel together and cannot
/// be returned half-filled by an implementation that forgot one.
/// </remarks>
public sealed record ExportedBudgetContents(
    IReadOnlyList<ExportedAccount> Accounts,
    IReadOnlyList<ExportedCategoryGroup> CategoryGroups,
    IReadOnlyList<ExportedCategory> Categories,
    IReadOnlyList<ExportedPayee> Payees,
    IReadOnlyList<ExportedTransaction> Transactions);
