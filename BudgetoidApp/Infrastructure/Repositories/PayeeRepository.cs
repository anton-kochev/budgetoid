using Application.Abstractions;
using Application.Payees;
using Domain.Payees;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class PayeeRepository(
    BudgetoidDbContext dbContext,
    IUserContext userContext,
    TimeProvider timeProvider) : IPayeeRepository
{
    public async Task<IReadOnlyList<Payee>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.Payees
            .AsNoTracking()
            .OrderBy(payee => payee.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Payee> GetOrCreateAsync(string name, CancellationToken cancellationToken = default)
    {
        string normalizedName = name.Trim();

        Payee? existing = await FindByNameAsync(normalizedName, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        Payee payee = Payee.Create(
            userContext.UserId,
            normalizedName,
            timeProvider.GetUtcNow().UtcDateTime);
        dbContext.Payees.Add(payee);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return payee;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
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
