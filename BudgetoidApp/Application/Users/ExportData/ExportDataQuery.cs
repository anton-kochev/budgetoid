namespace Application.Users.ExportData;

/// <summary>
/// Asks for everything the signed-in account owns, as one document.
/// </summary>
/// <remarks>
/// <para>
/// <b>No member, and none may be added.</b> The account exported is whichever one the request is
/// authenticated as, read from <see cref="Application.Abstractions.IUserContext.UserId" />; the budget
/// read is the ambient one. A user id or a budget id declared here would be a tenancy parameter with
/// no ownership check to pair with it — the same rule <c>EraseAccountCommand</c> carries, and for the
/// same reason.
/// </para>
/// <para>
/// Nothing selects, filters, pages or ranges either: a document that answered a range would be the
/// truncation this feature exists to refuse.
/// </para>
/// </remarks>
public sealed record ExportDataQuery;
