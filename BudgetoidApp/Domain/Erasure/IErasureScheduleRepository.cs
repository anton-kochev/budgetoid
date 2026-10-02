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
}
