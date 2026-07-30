using Domain.Common;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    public async Task UpdateAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        // The entity came from GetByIdAsync and is already tracked, so saving is the whole update.
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The handler resolves the account and the category through their filtered repositories
        // before it mutates anything, so a violation here means the row disappeared between that
        // read and this save — a concurrent delete. These catches are that race backstop, not the
        // primary guard. Filtering by constraint name and not by 23503 alone keeps a violation from
        // some other referencing row, riding along on the same SaveChanges, from being reported as
        // an account or category the caller never named; an unmatched one propagates, because a 500
        // naming the real constraint beats a 400 that lies.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
            ConstraintName: TransactionConfiguration.AccountForeignKeyName,
        })
        {
            // Detach the rejected entity so its failed (Modified) state can't leak into a later
            // SaveChanges if the context were reused, mirroring AccountRepository's detach-on-conflict.
            dbContext.Entry(transaction).State = EntityState.Detached;
            throw MissingAccountValidationException();
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
            ConstraintName: TransactionConfiguration.CategoryForeignKeyName,
        })
        {
            dbContext.Entry(transaction).State = EntityState.Detached;
            throw MissingCategoryValidationException();
        }
    }

    public async Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        // No foreign key points at transactions, so a delete has nothing to violate and needs no
        // 23503 translation the way accounts and categories do.
        dbContext.Transactions.Remove(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Worded and keyed the same as the up-front checks in the create and update handlers, so a row
    // that vanished under a race is reported to the caller exactly as one that was never there.
    private static ValidationException MissingAccountValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Transaction.AccountId)] = ["Account was not found."],
    });

    private static ValidationException MissingCategoryValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Transaction.CategoryId)] = ["Category was not found."],
    });
}
