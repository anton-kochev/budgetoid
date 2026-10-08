namespace Domain.Erasure;

/// <summary>
/// What removing an account's erasure schedule found: the row it deleted, or a row already gone.
/// </summary>
/// <remarks>
/// <b>Both members are a success</b>, because the post-condition a cancellation states — no schedule
/// stands — holds after either. <see cref="AlreadyGone"/> means the row was read and was already gone
/// when the delete ran: another withdrawal got there first, the account's erasure carried it away by
/// the cascade from <c>users</c>, or the save's own retry replayed a delete whose first attempt had
/// committed and lost its reply. It is reported rather than collapsed so the adapter's narrowing is
/// observable, not so a caller can refuse it.
/// </remarks>
public enum ScheduleRemoval
{
    /// <summary>The row was there and this call deleted it.</summary>
    Removed,

    /// <summary>The row was read, and it was already gone when the delete ran.</summary>
    AlreadyGone,
}
