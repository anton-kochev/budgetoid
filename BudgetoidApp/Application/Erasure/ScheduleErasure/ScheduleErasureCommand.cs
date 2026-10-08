namespace Application.Erasure.ScheduleErasure;

/// <summary>
/// Asks for the signed-in account's erasure to be filed for later rather than performed — the one act
/// a locked session may perform.
/// </summary>
/// <remarks>
/// <b>No members, and no account may ever be named here</b>, for the rule
/// <see cref="Users.EraseAccount.EraseAccountCommand"/> states for the immediate erasure: the account
/// scheduled is whichever one the request is authenticated as, read off
/// <see cref="Abstractions.IUserContext.UserId"/>. A user id declared on this command would be an
/// account a caller could choose.
/// </remarks>
public sealed record ScheduleErasureCommand;
