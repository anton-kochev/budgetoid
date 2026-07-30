using Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Persistence;

/// <summary>
/// Encloses a unit of work in one database transaction on the request-scoped
/// <see cref="BudgetoidDbContext"/>. Every repository the operation touches resolves that same
/// scoped context, which is what lets a wrap this thin cover all of their saves.
/// </summary>
public sealed class DbContextTransactionalExecutor(BudgetoidDbContext dbContext) : ITransactionalExecutor
{
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        // Join an outer transaction instead of nesting under it. BeginTransactionAsync throws when
        // the context already has one, and the alternatives to joining are both worse than the
        // problem: an inner begin/commit pair would commit half the outer unit while the outer scope
        // still believed it could roll everything back, and throwing would reject a call that is
        // legitimate. Nothing nests today; joining is what keeps the outermost scope owning the
        // commit on the day something does.
        if (dbContext.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        // Go through the execution strategy rather than calling BeginTransactionAsync directly. The
        // API's EnrichNpgsqlDbContext installs NpgsqlRetryingExecutionStrategy, and a retrying
        // strategy refuses a user-initiated transaction outright — it cannot replay a unit of work
        // whose boundary it does not own, so handing it the begin/commit pair is what gives it
        // something replayable. This is also the right shape when the strategy does not retry, as it
        // then simply invokes the delegate once, so the wrap survives retries being turned off again.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async token =>
            {
                // Disposal rolls back anything not yet committed, so an exception out of the
                // operation abandons the whole unit.
                //
                // The rollback does not undo the change tracker: EF marks entities Unchanged the
                // moment SaveChanges returns, and rolling the transaction back afterwards does not
                // walk that back, so a payee saved and then discarded stays in the tracker looking
                // persisted. Nothing observes it today because this context is request-scoped and
                // dies with the request — but that is a property of the hosting model, not of this
                // class, and a caller that reuses a context across units of work would be handed
                // stale entities.
                await using IDbContextTransaction transaction =
                    await dbContext.Database.BeginTransactionAsync(token);
                TResult result = await operation(token);
                await transaction.CommitAsync(token);
                return result;
            },
            cancellationToken);
    }
}
