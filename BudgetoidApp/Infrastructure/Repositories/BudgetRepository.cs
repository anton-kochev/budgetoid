using Domain.Budgets;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class BudgetRepository(BudgetoidDbContext dbContext) : IBudgetRepository
{
    /// <inheritdoc />
    public Task<Budget?> FindFirstForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        // Budget carries no global query filter — provisioning has to look a budget up before any
        // budget id exists — so ownership is scoped explicitly here.
        dbContext.Budgets
            .Where(budget => budget.UserId == userId)
            .OrderBy(budget => budget.CreatedAtUtc)
            .ThenBy(budget => budget.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<bool> TryAddAsync(Budget budget, CancellationToken cancellationToken = default)
    {
        dbContext.Budgets.Add(budget);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(budget).State = EntityState.Detached;
            return false;
        }
    }
}
