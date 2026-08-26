namespace Application.AccountKeys.GetAccountKeys;

/// <summary>
/// Asks for the wrapped account keys held by the credential that opened the session named by
/// <paramref name="SessionId" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>It carries a session id where every neighbouring query carries nothing, and that is not an
/// inconsistency.</b> <c>ListCredentialsQuery</c>, <c>GetSignedInUserQuery</c> and <c>ExportDataQuery</c>
/// declare no member because the thing they are scoped to is the account, which is read from
/// <see cref="Application.Abstractions.IUserContext.UserId" /> — a member there would be a tenancy
/// parameter with no ownership check to pair with it. This answer is narrowed by something the account
/// does <em>not</em> determine: <b>which credential opened this session</b>. An account holds a passkey
/// and a set of recovery codes at once, so "the account's wrapped keys" is eleven rows across two
/// credentials, of which the browser can open only the ones belonging to the factor it just
/// authenticated with. The account is still read from the context; the session id is the second half
/// that the context cannot supply.
/// </para>
/// <para>
/// <b>The id is not the caller's to choose</b>, the same rule <c>RevokeSessionCommand</c> carries: it is
/// read off the <c>session_id</c> claim this very request's own authentication produced, never off a
/// body, a route or a query string, so there is no id on the wire for anyone to substitute. What would
/// refuse a substituted one anyway is <c>user_isolation</c> on <c>sessions</c> one layer down — a
/// stranger's session is not found rather than found and rejected — and a session that is not found is
/// an empty list, never an error that would tell the caller their guess named a real row.
/// </para>
/// <para>
/// <b>Putting a credential id on <c>CurrentUser</c> or <c>AuthenticatedSession</c> instead was
/// considered and rejected.</b> <see cref="Application.Abstractions.IUserContext" /> is resolved on
/// <em>every</em> authenticated request in the product, so a credential id there would make every
/// request compute, publish and carry a value that exactly one route reads — the cost paid on the
/// authentication path, which <c>docs/decisions/0019-authenticate-a-request-from-a-first-party-session-cookie.md</c>
/// already keeps to two round trips on purpose. <c>AuthenticatedSession</c> is the same objection one
/// step further on: it is what the cookie handler turns into claims, so a member added there either
/// becomes a new claim on every principal or is dropped on the floor, and the claim set is a surface a
/// route-table test reads whole. A session id is already on both, for the sign-out route's sake, so this
/// query spends nothing that was not already being spent.
/// </para>
/// <para>
/// <b>No member may be added naming a user, a credential or a factor.</b> Each would be a caller-chosen
/// identifier on the one read that hands back an account's key custody, and the read service it reaches
/// takes its owner argument from the resolved context precisely so that no request can name one.
/// </para>
/// </remarks>
public sealed record GetAccountKeysQuery(Guid SessionId);
