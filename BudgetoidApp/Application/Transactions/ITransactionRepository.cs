using Domain.Transactions;

namespace Application.Transactions;

public interface ITransactionRepository
{
    Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default);

    // Returns the current user's transactions. Per-user scoping is enforced by the
    // BudgetoidDbContext global query filter, not by this method.
    Task<IReadOnlyList<Transaction>> GetAllAsync(CancellationToken cancellationToken = default);
}
