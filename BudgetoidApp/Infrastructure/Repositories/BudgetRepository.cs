using Domain.Budgets;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class BudgetRepository(BudgetoidDbContext dbContext) : IBudgetRepository
{
    /// <inheritdoc />
    public Task<Budget?> FindFirstForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        // Budget carries no global query filter — session authentication has to look a budget up
        // before any budget id exists — so ownership is scoped explicitly here.
        dbContext.Budgets
            .Where(budget => budget.UserId == userId)
            .OrderBy(budget => budget.CreatedAtUtc)
            .ThenBy(budget => budget.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> HasTransactionsAsync(CancellationToken cancellationToken = default) =>
        // No budget predicate: the BudgetIsolation query filter on the DbSet already scopes this to
        // the ambient budget. Re-filtering here would add a second source of tenancy that could
        // disagree with the filter.
        dbContext.Transactions.AnyAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(Budget budget, CancellationToken cancellationToken = default)
    {
        dbContext.Budgets.Add(budget);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Named, because false is not a generic failure signal: the caller answers it by re-reading the
        // owner's budget, and only this index guarantees a winner's row is there to be read. A 23505
        // from any other rule would send the caller after a budget nobody inserted.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: BudgetConfiguration.UserNameIndexName,
        })
        {
            dbContext.Entry(budget).State = EntityState.Detached;
            return false;
        }
    }
}
