namespace Api.Infrastructure;

/// <summary>
/// Declares that this route may be reached by a session that reads no budget content — one opened by a
/// federated credential. Every other route the application's fallback policy covers refuses one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-out, and it is the opposite of the two markers beside it on purpose.</b>
/// <see cref="ProvisionsUserAttribute" /> and <see cref="AcceptsEndedSessionAttribute" /> are opt-in, and
/// all three polarities are the fail-loud direction for their own rule. A forgotten opt-in marker
/// refuses something that should have been allowed; a forgotten opt-out here is a 403 on a route that
/// should have worked, which is just as loud and reaches somebody within a day. What would be silent is
/// the inverse of this one: an opt-<em>in</em> gate whose forgotten marker admits a provider sign-in to
/// budget content, with nothing anywhere going red. So the gate covers everything by default, and a
/// route argues its way out.
/// </para>
/// <para>
/// <b>What it does not relax.</b> Nothing about whether the presented handle matched a session — that is
/// authentication and has already happened. Nothing about whether the session is still live;
/// <see cref="AcceptsEndedSessionAttribute" /> owns that, and the two are independent, which is why the
/// sign-out route happens to carry both and why neither implies the other. And it publishes nothing: a
/// route carrying it reaches the same ambient budget it would have reached without it.
/// </para>
/// <para>
/// The opted-out set is read whole off the route table by <c>LockedSessionTests</c>, so adding one is a
/// decision somebody signs rather than a line in a diff. Read purely as endpoint metadata, by
/// <see cref="FullSessionRequirement" />'s handler, off the request's own endpoint.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class AllowsLockedSessionAttribute : Attribute;
