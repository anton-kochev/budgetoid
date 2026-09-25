using Domain.Sessions;

namespace Application.Sessions.AuthenticateSession;

/// <summary>
/// Who a presented handle turned out to be, and which sign-in it belongs to.
/// </summary>
/// <param name="UserId">The account the request is now acting as.</param>
/// <param name="SessionId">
/// The sign-in the handle names. Carried so the request can end <em>its own</em> session without
/// naming one, which is the whole of what makes signing out on a laptop leave a phone signed in.
/// </param>
/// <param name="Kind">
/// How much of the account the credential that opened this session reaches. Derived from that
/// credential and never chosen — see <c>Domain.Sessions.Session.KindFor</c>.
/// </param>
/// <param name="IsLive">
/// Whether the session was unrevoked and unexpired at the instant it was read.
/// <see langword="false"/> is a handle that named a real sign-in which has since ended.
/// </param>
/// <remarks>
/// <para>
/// <b>No budget id, deliberately.</b> The ambient budget is published by the handler that produced
/// this, through <c>IUserContextWriter</c>, which is the only mechanism the row-level-security model
/// reads. Returning it as well would offer a caller a second copy of the tenant to publish, and the
/// first caller to publish it out of order would pair one account's id with another's budget — the
/// state <c>CurrentUserWriter.ResolveUser</c> clears the budget to prevent.
/// </para>
/// <para>
/// <b><paramref name="IsLive"/> is a fact about the row, not a verdict on the request.</b> Whether an
/// ended session may still be presented is a decision about the route being asked for, and it is taken
/// where the route is visible. Folding it in here — answering <see langword="null"/> for a dead session
/// — would make signing out twice indistinguishable from presenting a handle nobody ever issued.
/// </para>
/// </remarks>
public sealed record AuthenticatedSession(Guid UserId, Guid SessionId, SessionKind Kind, bool IsLive);
