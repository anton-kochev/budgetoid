namespace Api.Infrastructure;

/// <summary>
/// Declares that this route may be reached with a handle whose session has already ended — revoked, or
/// expired. Every other route refuses one, and a request presenting no valid handle at all is refused
/// here too.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists for exactly one route, and the reason is that signing out has to be idempotent.</b> A
/// sign-out response can be lost on the way back and the client retries; a person can have two tabs
/// open and close both. Under the ordinary rule the second attempt presents a handle whose session is
/// already revoked, is refused 401, and leaves the client holding a dead cookie forever with a
/// signed-in shell on screen — the exact state <c>SessionCookie.Clear</c> exists to prevent. There is
/// also nothing left to protect on that request: the session it names has already ended.
/// </para>
/// <para>
/// <b>Opt-in, for <see cref="ProvisionsUserAttribute" />'s reason.</b> Under opt-out, "which routes
/// accept a dead handle" would be "everything nobody thought about", and the route added next is
/// exactly the one nobody reviews for this. A group that forgets the marker refuses a dead handle,
/// which is the safe direction and is loud.
/// </para>
/// <para>
/// <b>What it does not grant.</b> The handle still has to name a real session belonging to the account
/// it publishes, so this is not an anonymous route and does not appear in
/// <c>AnonymousSurfaceTests</c>' set — a request with no cookie is challenged like any other. And
/// <see cref="SessionCookieAuthenticationHandler" /> publishes <b>no ambient budget</b> for an ended
/// session, so a route carrying this marker structurally cannot reach budget content: the first
/// budget-scoped statement under it meets an unresolved budget and throws. It says nothing either about
/// what a live session may reach — <see cref="AllowsLockedSessionAttribute" /> owns that, and the
/// sign-out route carries both markers because it needs both answers, not because one implies the other.
/// </para>
/// <para>
/// Read purely as endpoint metadata, like <see cref="ProvisionsUserAttribute" />, and read by the
/// authentication handler rather than by an authorization policy. The distinction is forced rather than
/// chosen: an ended session that authenticated and then failed a policy would be answered <c>403</c>,
/// and a presented handle that is no longer good is a <c>401</c> — it is the credential that is
/// wanting, not the permission.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class AcceptsEndedSessionAttribute : Attribute;
