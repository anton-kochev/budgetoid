namespace Application.Users.ListCredentials;

/// <summary>
/// Asks for every way the current request's account can be signed in to.
/// </summary>
/// <remarks>
/// <b>No member, and none may be added.</b> The account listed is whichever one the request is
/// authenticated as, read from <see cref="Application.Abstractions.IUserContext.UserId" />. A user id
/// declared here would be a tenancy parameter with no ownership check to pair with it — the same rule
/// <c>GetSignedInUserQuery</c>, <c>ExportDataQuery</c> and <c>EraseAccountCommand</c> carry, and for the
/// same reason. It bites harder here than on any of them: <c>credentials</c> is exempt from row-level
/// security, so a caller-supplied id would reach a read that nothing beneath the application narrows.
/// </remarks>
public sealed record ListCredentialsQuery;
