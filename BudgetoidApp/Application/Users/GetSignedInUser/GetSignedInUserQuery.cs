namespace Application.Users.GetSignedInUser;

/// <summary>
/// Asks who the current request is signed in as.
/// </summary>
/// <remarks>
/// <b>No member, and none may be added.</b> The account described is whichever one the request is
/// authenticated as, read from <see cref="Application.Abstractions.IUserContext.UserId" />. A user id
/// declared here would be a tenancy parameter with no ownership check to pair with it — the same rule
/// <c>ExportDataQuery</c> and <c>EraseAccountCommand</c> carry, and for the same reason.
/// <b>A budget id is refused here for that reason too, and the response carrying one changes
/// nothing.</b> The budget on the answer is
/// <see cref="Application.Abstractions.IBudgetContext.BudgetId" />, resolved while the request
/// authenticates off the session cookie; a member here would let a caller name the tenant it wanted
/// to be told about, which is the direction this API refuses whatever it publishes on the way out.
/// </remarks>
public sealed record GetSignedInUserQuery;
