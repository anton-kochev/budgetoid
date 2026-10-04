using Application.Abstractions;
using Application.Passkeys.Reauthentication;
using Domain.Erasure;

namespace Application.Erasure.CancelScheduledErasure;

/// <summary>
/// Withdraws the signed-in account's scheduled erasure on a verified passkey assertion, leaving no
/// schedule standing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate runs first, every time, and outside any transaction.</b> Not to hide whether a schedule
/// stands — any full session reads that from <c>GET /api/me/session</c> — but for two narrower reasons.
/// A 204 only ever goes to a caller who proved a passkey, so a bad proof's answer never depends on what
/// the table holds. And the nonce is spent even when nothing is scheduled: a handler that returned early
/// on "nothing to cancel" would leave it live in the shared re-authentication pool, where it would also
/// authorize <c>POST /api/me/erasure</c> and every other assertion-gated act for the rest of its five
/// minutes. That second reason is hygiene, not a boundary — the nonce was minted for this session and
/// would have been spendable there anyway. Outside a transaction for the reason
/// <see cref="Users.EraseAccount.EraseAccountHandler"/> gives: the consume must commit on its own.
/// </para>
/// <para>
/// <b>The row is deleted, never stamped</b>, and an absent row is not an error. The post-condition — no
/// schedule stands — is what the caller is told, whether this request removed the row, something else
/// removed it first, or none was ever filed.
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
