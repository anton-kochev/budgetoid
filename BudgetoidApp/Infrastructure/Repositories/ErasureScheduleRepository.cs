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

    /// <inheritdoc />
    public Task<ErasureSchedule?> FindTrackedAsync(Guid userId, CancellationToken cancellationToken = default) =>
        // Tracked — no AsNoTracking may be added — because RemoveAsync deletes this very instance, and the
        // owner predicate is FindAsync's, for FindAsync's reason.
        dbContext.ErasureSchedules
            .Where(schedule => schedule.UserId == userId)
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<ScheduleRemoval> RemoveAsync(
        ErasureSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        // Remove and save rather than ExecuteDelete, which BannedSymbols.txt makes a compile error. The
        // statement EF emits is keyed on the primary key alone; user_isolation scopes it to the published
        // account underneath, so a row the session does not own is a row this DELETE cannot match.
        dbContext.ErasureSchedules.Remove(schedule);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ScheduleRemoval.Removed;
        }
        // The row was read and was gone by the time the DELETE ran — another withdrawal, the account's
        // erasure cascading, or the retrying execution strategy replaying a delete that had committed —
        // so it matches nothing where EF expected one row. The post-condition — no schedule stands —
        // holds, so it is an answer, not a 500. Narrowed BY THE ENTRIES, the shape
        // TransactionRepository.DeleteAllForAmbientBudgetAsync uses: the save flushes everything this
        // request-scoped context tracks, and a conflict EF names on another entity propagates. EF names
        // only the first failing command's entries, so that is all this filter can see.
        catch (DbUpdateConcurrencyException exception) when (IsAlreadyRemoved(exception))
        {
            // Left Deleted, the entry would be flushed again by the next save on this context — whatever
            // that save was for — and raise the same conflict there.
            dbContext.Entry(schedule).State = EntityState.Detached;
            return ScheduleRemoval.AlreadyGone;
        }
    }

    /// <summary>
    /// True when every conflicting entry is a deleted <see cref="ErasureSchedule"/>. The count test is not
    /// redundant: an exception EF could not attribute to any entry would satisfy <c>All</c> vacuously.
    /// </summary>
    private static bool IsAlreadyRemoved(DbUpdateConcurrencyException exception) =>
        exception.Entries.Count > 0
        && exception.Entries.All(entry =>
            entry.Entity is ErasureSchedule && entry.State == EntityState.Deleted);
}
