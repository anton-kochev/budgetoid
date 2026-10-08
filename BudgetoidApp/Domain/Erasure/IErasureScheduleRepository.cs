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
    /// <see cref="ScheduleRemoval.AlreadyGone"/> answers a delete that matched nothing because the row
    /// was already gone when it ran — see that member for the three ways it gets there. The narrowing
    /// reads the entries the concurrency failure names: it answers <see cref="ScheduleRemoval.AlreadyGone"/>
    /// only when there is at least one and every one is a deleted <see cref="ErasureSchedule"/>, and lets
    /// anything else propagate. Those entries are only the first failing command's, so this tells a
    /// schedule's empty delete apart from a conflict reported on another entity; it does not prove that
    /// no other row in the same save conflicted.
    /// </para>
    /// </remarks>
    Task<ScheduleRemoval> RemoveAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default);
}
