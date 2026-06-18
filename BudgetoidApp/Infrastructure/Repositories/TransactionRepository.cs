using Application.Transactions;
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

    public async Task<IReadOnlyList<Transaction>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // No explicit user predicate: the BudgetoidDbContext global query filter scopes this
        // to the current user automatically (see TECH_DEBT.md "Data isolation invariant").
        return await dbContext.Transactions
            .AsNoTracking()
            .OrderByDescending(transaction => transaction.Date)
            .ThenByDescending(transaction => transaction.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }
}
