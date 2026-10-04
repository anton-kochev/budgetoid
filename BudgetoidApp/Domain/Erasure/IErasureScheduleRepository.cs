namespace Domain.Erasure;

public interface IErasureScheduleRepository
{
    /// <summary>
    /// Returns the schedule filed for <paramref name="userId"/>'s account, or <see langword="null"/> when
    /// it holds none.
    /// </summary>
    /// <remarks>
    /// <b>The owner predicate is explicit, and row-level security is not what scopes this read.</b>
    /// <c>user_isolation</c> would hide a stranger's row from the application role anyway, but a read
    /// leaning on that answers "whichever schedule this connection can see" — the right row only for as
    /// long as the policy and the published identity agree — and the day the table gains an exempt
    /// discovery reader for the executor, it would answer somebody else's.
    /// </remarks>
    Task<ErasureSchedule?> FindAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Files <paramref name="schedule"/> and returns the schedule the account holds afterwards — the one
    /// given, or, when a racing request filed one first, that one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The return value is the answer, never the argument.</b> Two first requests from one account can
    /// both find nothing and both add; the loser's insert collides on the primary key, and what it must
    /// tell its caller is the instant the account actually holds, not the slightly later one it computed.
    /// </para>
    /// <para>
    /// Only that collision is absorbed. Any other refusal — another unique rule, a foreign key, a
    /// violation raised by an unrelated row the same unit of work was tracking — propagates, because a
    /// re-read after one of those finds no winner to report.
    /// </para>
    /// </remarks>
    Task<ErasureSchedule> AddAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the schedule filed for <paramref name="userId"/>'s account as an instance the unit of work
    /// tracks, ready to hand to <see cref="RemoveAsync"/>, or <see langword="null"/> when it holds none.
    /// </summary>
    /// <remarks>
    /// The owner predicate is explicit for the reason <see cref="FindAsync"/> gives: <c>user_isolation</c>
    /// is the second wall, not the first. A remove that leant on the policy alone would delete whichever
    /// row the connection can see.
    /// </remarks>
    Task<ErasureSchedule?> FindTrackedAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes <paramref name="schedule"/>'s row and reports whether this call removed it or found it
    /// already gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row is deleted, never stamped.</b> A cancelled-at column would be a remnant on an account
    /// that once asked to be forgotten, and it would need an <c>UPDATE</c> the table is deliberately
    /// never granted.
    /// </para>
    /// <para>
    /// <see cref="ScheduleRemoval.AlreadyGone"/> answers only a delete that matched nothing because a
    /// concurrent cancel removed the row first. A conflict over any other row the same unit of work was
    /// tracking propagates: it is a failure this method does not model.
    /// </para>
    /// </remarks>
    Task<ScheduleRemoval> RemoveAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default);
}
