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
}
