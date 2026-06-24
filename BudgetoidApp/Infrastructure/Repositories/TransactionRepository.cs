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
        return await OrderedTransactions()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TransactionDto>> GetAllWithPayeeAsync(CancellationToken cancellationToken = default)
    {
        // Both Transactions and Payees are scoped by BudgetoidDbContext global query filters.
        return await (
                from transaction in dbContext.Transactions.AsNoTracking()
                join payee in dbContext.Payees.AsNoTracking()
                    on transaction.PayeeId equals (Guid?)payee.Id into payees
                from payee in payees.DefaultIfEmpty()
                orderby transaction.Date descending, transaction.CreatedAtUtc descending
                select new TransactionDto(
                    transaction.Id,
                    transaction.Amount,
                    transaction.Date,
                    transaction.Description,
                    transaction.CreatedAtUtc,
                    transaction.PayeeId,
                    payee == null ? null : payee.Name))
            .ToListAsync(cancellationToken);
    }

    private IOrderedQueryable<Transaction> OrderedTransactions() => dbContext.Transactions
        .AsNoTracking()
        .OrderByDescending(transaction => transaction.Date)
        .ThenByDescending(transaction => transaction.CreatedAtUtc);
}
