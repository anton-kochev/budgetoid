namespace Application.Users.GetSignedInUser;

/// <summary>
/// Asks who the current request is signed in as.
/// </summary>
/// <remarks>
/// <b>No member, and none may be added.</b> The account described is whichever one the request is
/// authenticated as, read from <see cref="Application.Abstractions.IUserContext.UserId" />. A user id
/// declared here would be a tenancy parameter with no ownership check to pair with it — the same rule
/// <c>ExportDataQuery</c> and <c>EraseAccountCommand</c> carry, and for the same reason.
/// </remarks>
public sealed record GetSignedInUserQuery;
