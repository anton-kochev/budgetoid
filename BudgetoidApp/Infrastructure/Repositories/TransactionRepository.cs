using Domain.Transactions;
using Infrastructure.Persistence;

namespace Infrastructure.Repositories;

public sealed class TransactionRepository(BudgetoidDbContext dbContext) : ITransactionRepository
{
    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        dbContext.Transactions.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
