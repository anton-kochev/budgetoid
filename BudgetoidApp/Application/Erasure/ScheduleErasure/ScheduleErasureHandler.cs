using Application.Abstractions;
using Domain.Erasure;

namespace Application.Erasure.ScheduleErasure;

/// <summary>
/// Files the signed-in account's erasure to take effect <see cref="ErasurePolicy.Delay"/> from now, or
/// answers the instant already filed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A repeat never moves the date.</b> When the account already holds a schedule, its instant is the
/// answer and nothing is written: a date that slid forward on every visit would be a window that never
/// closes, which is exactly what somebody holding a stolen provider account would want. The race the
/// find cannot see — two first requests both finding nothing — is closed by the primary key, and the
/// repository answers the loser with the winner's row; that is why the answer is what
/// <see cref="IErasureScheduleRepository.AddAsync"/> returns and never the schedule built here.
/// </para>
/// <para>
/// <b>It takes no <c>ILogger</c>, and must never take one.</b> It holds the id of an account that asked
/// to be forgotten, and a line naming it outlives the erasure it schedules. <c>ErasureLoggingTests</c>
/// holds the rule.
/// </para>
/// <para>
/// <b>No transaction.</b> The one write is a single save, and the find before it is not a check the
/// write depends on for correctness — the key is. Wrapping the two in <see cref="ITransactionalExecutor"/>
/// would buy a READ COMMITTED snapshot that closes nothing.
/// </para>
/// </remarks>
public sealed class ScheduleErasureHandler(
    IErasureScheduleRepository schedules,
    IUserContext userContext,
    TimeProvider timeProvider)
    : ICommandHandler<ScheduleErasureCommand, ScheduledErasure>
{
    public async Task<ScheduledErasure> HandleAsync(
        ScheduleErasureCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Guid userId = userContext.UserId;

        ErasureSchedule? existing = await schedules.FindAsync(userId, cancellationToken);
        if (existing is not null)
        {
            return new ScheduledErasure(existing.TakesEffectAtUtc);
        }

        ErasureSchedule requested = ErasureSchedule.Request(
            userId, timeProvider.GetUtcNowToTheMicrosecond(), ErasurePolicy.Delay);
        ErasureSchedule stored = await schedules.AddAsync(requested, cancellationToken);

        return new ScheduledErasure(stored.TakesEffectAtUtc);
    }
}
