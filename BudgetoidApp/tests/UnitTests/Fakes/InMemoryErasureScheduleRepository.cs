using Domain.Erasure;

namespace UnitTests.Fakes;

/// <summary>
/// The account's pending erasure, held in memory and keyed on the account — because
/// <c>erasure_schedules.user_id</c> is the primary key, and "one schedule per account" is therefore a
/// fact about this dictionary exactly as it is a fact about the table.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="FindAsync" /> answers only the account it is asked about.</b> A schedule filed for
/// somebody else stays invisible to a lookup naming this account, so a handler that read "a schedule"
/// instead of "this account's schedule" is handed nothing and files its own — which the handler tests
/// can see, and which the integration tier cannot: under the application role, <c>user_isolation</c>
/// hides a stranger's row from an unfiltered read anyway.
/// </para>
/// <para>
/// <b><see cref="AddAsync" /> keeps the port's promise for a lost race and nothing more.</b> When the
/// account already holds a schedule it returns the stored one and files nothing, which is what the
/// adapter does on a <c>23505</c> naming the primary key. What it cannot show is <em>how</em> the
/// adapter keeps that promise — the constraint-name filter, the detach, the re-read — and that belongs
/// to <c>ErasureScheduleRepositoryTests</c>.
/// </para>
/// <para>
/// <see cref="AddCalls" /> counts calls rather than rows, so "a repeat files nothing" is a claim about
/// what the handler asked for and not about what this fake happened to absorb.
/// </para>
/// </remarks>
public sealed class InMemoryErasureScheduleRepository : IErasureScheduleRepository
{
    private readonly Dictionary<Guid, ErasureSchedule> _schedules = [];

    /// <summary>How many times <see cref="AddAsync" /> was called, whatever it did with the call.</summary>
    public int AddCalls { get; private set; }

    /// <summary>
    /// How many times either read — <see cref="FindAsync" /> or <see cref="FindTrackedAsync" /> — was
    /// called, so "the repository was never touched" is a claim about what a handler asked for.
    /// </summary>
    public int ReadCalls { get; private set; }

    /// <summary>How many times <see cref="RemoveAsync" /> was called, whatever it found.</summary>
    public int RemoveCalls { get; private set; }

    /// <summary>Every stored schedule, keyed on the account it belongs to.</summary>
    public IReadOnlyDictionary<Guid, ErasureSchedule> Stored => _schedules;

    /// <summary>Files a schedule as though an earlier request had already stored it.</summary>
    public void Seed(ErasureSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        _schedules.Add(schedule.UserId, schedule);
    }

    public Task<ErasureSchedule?> FindAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        return Task.FromResult(_schedules.GetValueOrDefault(userId));
    }

    /// <remarks>
    /// The same owner-keyed lookup as <see cref="FindAsync" />; "tracked" is the adapter's concern — the
    /// instance handed back is the one <see cref="RemoveAsync" /> is later given.
    /// </remarks>
    public Task<ErasureSchedule?> FindTrackedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        return Task.FromResult(_schedules.GetValueOrDefault(userId));
    }

    /// <remarks>
    /// Removes the row keyed on the schedule's own account, and answers
    /// <see cref="ScheduleRemoval.AlreadyGone" /> when nothing was there to remove — the state the adapter
    /// reports when a concurrent cancel deleted the row between the read and the delete.
    /// </remarks>
    public Task<ScheduleRemoval> RemoveAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        RemoveCalls++;

        return Task.FromResult(_schedules.Remove(schedule.UserId)
            ? ScheduleRemoval.Removed
            : ScheduleRemoval.AlreadyGone);
    }

    public Task<ErasureSchedule> AddAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        AddCalls++;

        // The stored row wins, as the primary key makes it win in PostgreSQL.
        if (_schedules.TryGetValue(schedule.UserId, out ErasureSchedule? stored))
        {
            return Task.FromResult(stored);
        }

        _schedules.Add(schedule.UserId, schedule);
        return Task.FromResult(schedule);
    }
}
