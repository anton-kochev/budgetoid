using Application.Abstractions;
using Domain.Payees;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    // Plain equality: the name column's case_insensitive collation makes PostgreSQL fold case for
    // both this comparison and the unique index, so the lookup and the index can never disagree.
    private Task<Payee?> FindByNameAsync(string normalizedName, CancellationToken cancellationToken)
    {
        return dbContext.Payees
            .SingleOrDefaultAsync(payee => payee.Name == normalizedName, cancellationToken);
    }
}
