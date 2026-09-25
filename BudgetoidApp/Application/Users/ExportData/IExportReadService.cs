namespace Application.Users.ExportData;

/// <summary>
/// The reads an export is assembled from.
/// </summary>
/// <remarks>
/// <para>
/// Three methods rather than one, so the decision about <em>which</em> budgets an export may contain
/// lives in the handler — where a hand-written fake can put it under test — instead of inside a query
/// that only a database can answer.
/// </para>
/// <para>
/// The first two take the user id and the third takes nothing, and the asymmetry is the tenancy
/// model rather than an oversight. <c>users</c> and <c>budgets</c> carry no EF query filter and are
/// policed on <c>user_id</c> in the database, so an owner predicate is what makes those two reads
/// selective; everything inside a budget is scoped by the <c>BudgetIsolation</c> filter and the
/// <c>budget_isolation</c> policy, both keyed on the ambient budget. A budget id parameter here would
/// be a tenancy argument with no ownership check to pair with it — the reason
/// <c>IBudgetRepository.HasTransactionsAsync</c> and
/// <c>ITransactionRepository.DeleteAllForAmbientBudgetAsync</c> take none either.
/// </para>
/// </remarks>
public interface IExportReadService
{
    /// <summary>
    /// The account <paramref name="userId" /> names, or <see langword="null" /> when no row answers to
    /// it.
    /// </summary>
    Task<ExportedUser?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every budget <paramref name="userId" /> owns, ascending by creation.
    /// </summary>
    /// <remarks>
    /// <c>CreatedAtUtc</c> ascending is the contract — that is what a caller may rely on. <c>Id</c> is a
    /// deterministic tiebreaker and is not part of it: <c>uuid</c> collation is provider-defined, so two
    /// budgets sharing an instant may order one way here and another way under a second implementation,
    /// with neither being wrong. Never <c>Id</c> alone, though: UUID v7 sorts by creation time under
    /// PostgreSQL's <c>uuid</c> byte order but not under .NET's <see cref="Guid.CompareTo(Guid)" />.
    /// <c>IBudgetRepository.FindFirstForUserAsync</c> states the same rule as contract.
    /// </remarks>
    Task<IReadOnlyList<ExportedBudget>> ListOwnedBudgetsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything filed under the ambient budget.
    /// </summary>
    /// <remarks>
    /// Every collection ascends by <c>CreatedAtUtc</c>, and that ascent is the contract. <c>Id</c> breaks
    /// ties deterministically but is not part of it: <c>uuid</c> collation is provider-defined, so two
    /// rows sharing an instant may order one way here and another way under a second implementation,
    /// with neither being wrong. Never <c>Id</c> alone, for the reason
    /// <see cref="ListOwnedBudgetsAsync" /> gives — UUID v7 sorts by creation time under PostgreSQL's
    /// <c>uuid</c> byte order and not under <see cref="Guid.CompareTo(Guid)" />. It is stated here rather
    /// than left to the implementation because the order rows come back in is what a caller restoring
    /// from a saved file has to reconstruct creation sequence from — nothing else in the document
    /// records it.
    /// </remarks>
    Task<ExportedBudgetContents> ReadAmbientBudgetContentsAsync(
        CancellationToken cancellationToken = default);
}

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
