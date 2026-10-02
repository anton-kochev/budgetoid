using Domain.Erasure;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class ErasureScheduleRepository(BudgetoidDbContext dbContext) : IErasureScheduleRepository
{
    /// <inheritdoc />
    public Task<ErasureSchedule?> FindAsync(Guid userId, CancellationToken cancellationToken = default) =>
        // Untracked: the handler answers the instant and never writes back to a found row, and the
        // re-read after a lost race below must not resolve to a tracked instance either.
        dbContext.ErasureSchedules
            .AsNoTracking()
            .Where(schedule => schedule.UserId == userId)
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<ErasureSchedule> AddAsync(
        ErasureSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        dbContext.ErasureSchedules.Add(schedule);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return schedule;
        }
        // Named, because the re-read below is only an answer when this key refused the insert: then a
        // racing request's row is there to be read, and it is the instant the account holds. A 23505
        // from any other rule — another table's unique index, flushed by the same save — would send the
        // re-read after a schedule nobody stored.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ErasureScheduleConfiguration.PrimaryKeyName,
        })
        {
            // The refused insert stays Added otherwise, and the next save on this request-scoped
            // context — whatever it was for — would flush it again and meet the same key.
            dbContext.Entry(schedule).State = EntityState.Detached;

            return await FindAsync(schedule.UserId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The account's erasure schedule collided on its primary key but could not be read back.");
        }
    }
}
