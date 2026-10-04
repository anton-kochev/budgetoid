namespace Domain.Erasure;

/// <summary>
/// What removing an account's erasure schedule found: the row it deleted, or a row already gone.
/// </summary>
/// <remarks>
/// <b>Both members are a success</b>, because the post-condition a cancellation states — the account is
/// live and no schedule stands — holds after either. <see cref="AlreadyGone"/> is a concurrent cancel
/// from another tab landing between this request's read and its delete; it is reported rather than
/// collapsed so the adapter's narrowing is observable, not so a caller can refuse it.
/// </remarks>
public enum ScheduleRemoval
{
    /// <summary>The row was there and this call deleted it.</summary>
    Removed,

    /// <summary>The row was read, and by the time the delete ran another request had removed it.</summary>
    AlreadyGone,
}
