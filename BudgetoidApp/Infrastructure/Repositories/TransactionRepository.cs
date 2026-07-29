using Domain.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public sealed class TransactionRepository(BudgetoidDbContext dbContext) : ITransactionRepository
{
    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        dbContext.Transactions.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Query the filtered DbSet rather than Find/FindAsync: Find can answer from the change
        // tracker without ever reaching the budget query filter, which would hand one budget a
        // transaction belonging to another.
        return dbContext.Transactions.FirstOrDefaultAsync(
            transaction => transaction.Id == id,
            cancellationToken);
    }

    public async Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        // No foreign key points at transactions, so a delete has nothing to violate and needs no
        // 23503 translation the way accounts and categories do.
        dbContext.Transactions.Remove(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
