namespace Domain.Transactions;

public interface ITransactionRepository
{
    Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default);
    Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateAsync(Transaction transaction, CancellationToken cancellationToken = default);
    Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every transaction in the ambient budget. Written for erasure: <c>transactions</c> is
    /// the child of four of the owned graph's five <c>ON DELETE RESTRICT</c> edges — to
    /// <c>budgets</c>, <c>accounts</c>, <c>categories</c> and <c>payees</c> — so these rows are the
    /// ones a delete of the owning user would otherwise be refused over.
    /// </summary>
    /// <remarks>
    /// Deliberately takes no budget id, for the reason
    /// <see cref="Domain.Budgets.IBudgetRepository.HasTransactionsAsync"/> already states: tenancy
    /// comes from the <c>BudgetIsolation</c> query filter via <c>IBudgetContext</c>, which is the
    /// only authorization mechanism this system has, so a caller-supplied budget id would be a
    /// tenancy parameter with no ownership check to pair with it — and on a delete that would be an
    /// id naming whose rows get destroyed.
    /// <para>
    /// Returns no count. The observable outcome is that the budget holds no transactions, and a
    /// number invites a caller — or a test — to assert on how many rows a delete happened to find
    /// rather than on the state it left behind. That outcome is also what a concurrent erasure of
    /// the same account leaves, so losing that race returns rather than throws.
    /// </para>
    /// </remarks>
    Task DeleteAllForAmbientBudgetAsync(CancellationToken cancellationToken = default);
}
