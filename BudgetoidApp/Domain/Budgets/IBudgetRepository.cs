namespace Domain.Budgets;

public interface IBudgetRepository
{
    /// <summary>
    /// Returns the user's earliest budget, ordered by <see cref="Budget.CreatedAtUtc"/> and then by
    /// <see cref="Budget.Id"/> as a deterministic tiebreaker, or <see langword="null"/> when the
    /// user owns no budget.
    /// </summary>
    /// <remarks>
    /// The ordering is part of the contract, not an implementation detail: UUID v7 sorts by creation
    /// time under PostgreSQL's <c>uuid</c> byte order but not under .NET's
    /// <see cref="Guid.CompareTo(Guid)"/>, so ordering by <see cref="Budget.CreatedAtUtc"/> first is
    /// what keeps an in-memory implementation and the database-backed one in agreement.
    /// </remarks>
    Task<Budget?> FindFirstForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<bool> TryAddAsync(Budget budget, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <see langword="true"/> when the ambient budget holds at least one transaction, which
    /// is what makes it undeletable — recorded money movement is never discarded with the budget
    /// that holds it.
    /// </summary>
    /// <remarks>
    /// Deliberately takes no budget id. Tenancy comes from the <c>BudgetIsolation</c> query filter
    /// via <c>IBudgetContext</c>, which is the only authorization mechanism this system has, so a
    /// caller-supplied budget id would be a tenancy parameter with no ownership check to pair with
    /// it: the answer would be about whichever budget the caller named rather than the one it is
    /// entitled to.
    /// </remarks>
    Task<bool> HasTransactionsAsync(CancellationToken cancellationToken = default);
}
