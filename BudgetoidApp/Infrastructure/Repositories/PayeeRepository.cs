using Application.Abstractions;
using Domain.Payees;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Aliased because Application.Abstractions, imported above for IBudgetContext, declares a
// ValidationException of its own. Only Domain.Common's is the one ValidationExceptionHandler
// renders as a 400, which is what every other repository here throws.
using ValidationException = Domain.Common.ValidationException;

namespace Infrastructure.Repositories;

public sealed class PayeeRepository(
    BudgetoidDbContext dbContext,
    IBudgetContext budgetContext,
    TimeProvider timeProvider) : IPayeeRepository
{
    public async Task<Payee> GetOrCreateAsync(string name, CancellationToken cancellationToken = default)
    {
        string normalizedName = name.Trim();

        Payee? existing = await FindByNameAsync(normalizedName, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        Payee payee = Payee.Create(
            budgetContext.BudgetId,
            normalizedName,
            timeProvider.GetUtcNow().UtcDateTime);
        dbContext.Payees.Add(payee);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return payee;
        }
        // Named, because the recovery below assumes the losing side of a race for this very name: a
        // 23505 from any other rule leaves nothing to re-read, and swallowing it would turn a stranger's
        // collision into "no matching payee was found" with the real constraint already discarded.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PayeeConfiguration.NameIndexName,
        })
        {
            dbContext.Entry(payee).State = EntityState.Detached;
            return await FindByNameAsync(normalizedName, cancellationToken)
                ?? throw new InvalidOperationException("Payee unique violation occurred but no matching payee was found.");
        }
    }

    public Task<Payee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Use the filtered DbSet, not Find/FindAsync: Find can return a tracked entity while
        // bypassing global query filters, which would let one budget reach another budget's payee.
        return dbContext.Payees.FirstOrDefaultAsync(payee => payee.Id == id, cancellationToken);
    }

    public async Task UpdateAsync(Payee payee, CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The same index GetOrCreateAsync catches, and deliberately the opposite recovery: there the
        // caller only wanted a payee by that name, so the winner of the race is an acceptable answer
        // and gets re-read. A rename was asked for one specific name, so there is nothing to fall
        // back to and the collision is reported to the caller.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PayeeConfiguration.NameIndexName,
        })
        {
            // Detach the rejected entity so the failed (Modified) state can't leak into a later
            // SaveChanges if the context were reused, mirroring GetOrCreateAsync's detach-on-conflict.
            dbContext.Entry(payee).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    // Plain equality: the name column's case_insensitive collation makes PostgreSQL fold case for
    // both this comparison and the unique index, so the lookup and the index can never disagree.
    private Task<Payee?> FindByNameAsync(string normalizedName, CancellationToken cancellationToken)
    {
        return dbContext.Payees
            .SingleOrDefaultAsync(payee => payee.Name == normalizedName, cancellationToken);
    }

    private static ValidationException DuplicateNameValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Payee.Name)] = ["Payee name must be unique."],
    });
}
