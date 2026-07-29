using Domain.Accounts;
using Domain.Common;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class AccountRepository(BudgetoidDbContext dbContext) : IAccountRepository
{
    public async Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        dbContext.Accounts.Add(account);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Named, because SaveChanges flushes every tracked row and not just this account: only the
        // account name index says the name the caller just typed is the one already taken.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AccountConfiguration.NameIndexName,
        })
        {
            dbContext.Entry(account).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    public Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Use the filtered DbSet, not Find/FindAsync: Find can return a tracked entity while
        // bypassing global query filters, which would let one user reference another user's account.
        return dbContext.Accounts.FirstOrDefaultAsync(account => account.Id == id, cancellationToken);
    }

    public async Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Same index as AddAsync: a rename collides with exactly the rule an insert would.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AccountConfiguration.NameIndexName,
        })
        {
            // Detach the rejected entity so the failed (Modified) state can't leak into a later
            // SaveChanges if the context were reused, mirroring AddAsync's detach-on-conflict.
            dbContext.Entry(account).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    public async Task DeleteAsync(Account account, CancellationToken cancellationToken = default)
    {
        dbContext.Accounts.Remove(account);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The transactions reference is the only foreign key pointing at accounts, so this name is the
        // whole of "it has transactions". Naming it also means a second referencing table, or a
        // stranger's 23503 riding along on the same SaveChanges, cannot block a legitimate delete
        // behind a message about transactions the account does not have.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
            ConstraintName: TransactionConfiguration.AccountForeignKeyName,
        })
        {
            // Detach the rejected entity so the failed (Deleted) state can't leak into a later
            // SaveChanges if the context were reused, mirroring AddAsync/UpdateAsync's detach-on-conflict.
            dbContext.Entry(account).State = EntityState.Detached;
            throw ReferencedAccountValidationException();
        }
    }

    public Task<bool> HasTransactionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        return dbContext.Transactions.AnyAsync(transaction => transaction.AccountId == accountId, cancellationToken);
    }

    private static ValidationException DuplicateNameValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Account.Name)] = ["Account name must be unique."],
    });

    private static ValidationException ReferencedAccountValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Account.Id)] = ["Account cannot be deleted because it has transactions."],
    });
}
