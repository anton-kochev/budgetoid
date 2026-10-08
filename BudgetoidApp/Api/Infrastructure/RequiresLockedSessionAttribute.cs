namespace Api.Infrastructure;

/// <summary>
/// Declares that this route may be reached by a locked session and by nothing else — a full session is
/// answered 403 here.
/// </summary>
/// <remarks>
/// <para>
/// <b>It narrows and never widens.</b> A locked session reaches a route only by the route escaping
/// <see cref="FullSessionRequirement" />, which is <see cref="AllowsLockedSessionAttribute" />'s job; this
/// marker on its own lets a locked session past nothing, and a route carrying it without the opt-out
/// refuses every session there is. A route that is for a locked session only therefore carries both, and
/// the two are read by different requirements on the same fallback policy.
/// </para>
/// <para>
/// <b>Opt-in, because the other polarity cannot ship.</b> A gate on by default that a route argued its
/// way out of would refuse every full session in the product until each route declared itself. The
/// cost is that a forgotten marker is quiet: a full session reaches the route and nothing goes red at
/// runtime. So the route that carries it is pinned twice — by a paired test, a full session refused 403
/// beside a locked session on the same account succeeding, and by the <c>LockedSessionTests</c> census
/// of the routes only a locked session may reach.
/// </para>
/// <para>
/// Read purely as endpoint metadata, by <see cref="LockedSessionOnlyRequirement" />'s handler, off the
/// request's own endpoint.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class RequiresLockedSessionAttribute : Attribute;
