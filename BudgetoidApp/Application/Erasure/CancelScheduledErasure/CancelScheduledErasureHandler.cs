using Application.Abstractions;
using Application.Passkeys.Reauthentication;
using Domain.Erasure;

namespace Application.Erasure.CancelScheduledErasure;

/// <summary>
/// Withdraws the signed-in account's scheduled erasure on a verified passkey assertion, leaving the
/// account live and no schedule standing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate runs first, every time, and outside any transaction.</b> Before the schedule is read, so
/// a refused assertion learns nothing about it; and even when nothing is scheduled, so the nonce is spent
/// either way. A handler that looked first and returned early on "nothing to cancel" would leave the
/// nonce live — a caller could tell by its survival whether a schedule stands, and a stolen full session
/// could keep a valid assertion in reserve for the moment one is filed. Outside a transaction for the
/// reason <see cref="Users.EraseAccount.EraseAccountHandler"/> gives: the consume must commit on its own.
/// </para>
/// <para>
/// <b>The row is deleted, never stamped</b>, and an absent row is not an error. The post-condition — the
/// account is live and no schedule stands — is what the caller is told, whether this request removed the
/// row, a concurrent cancel from another tab removed it first, or none was ever filed.
/// </para>
/// <para>
/// <b>It takes no <c>ILogger</c>, and must never take one.</b> A line naming an account whose schedule
/// was withdrawn records that the account once asked to be forgotten — the remnant the delete exists to
/// avoid. <c>ErasureLoggingTests</c> holds the rule.
/// </para>
/// <para>
/// <b>No <see cref="ITransactionalExecutor"/>.</b> The one write is a single save, and the read before it
/// is not a check the write depends on — the delete is keyed on the row and a lost race is answered by the
/// repository. A READ COMMITTED transaction around the two would close nothing.
/// </para>
/// </remarks>
public sealed class CancelScheduledErasureHandler(
    IErasureScheduleRepository schedules,
    IUserContext userContext,
    PasskeyReauthentication reauthentication)
    : ICommandHandler<CancelScheduledErasureCommand>
{
    public async Task HandleAsync(
        CancelScheduledErasureCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Before any read of the schedule, and unconditionally — see the remarks. Moving it below the
        // find reddens CancelScheduledErasureHandlerTests' ordering and nonce-spending cases.
        await reauthentication.VerifyAsync(command.Assertion, cancellationToken);

        ErasureSchedule? schedule = await schedules.FindTrackedAsync(userContext.UserId, cancellationToken);
        if (schedule is null)
        {
            return;
        }

        // Removed or AlreadyGone: both leave no schedule standing, which is the whole of the answer.
        _ = await schedules.RemoveAsync(schedule, cancellationToken);
    }
}
