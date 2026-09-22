namespace Api.Infrastructure;

/// <summary>
/// The cacheability a route states for itself, in one place: the <c>Cache-Control</c> value the two
/// routes that hand back key material write onto their own response.
/// </summary>
/// <remarks>
/// <para>
/// <b>One owner, because there are two routes now and a security header kept in two private copies is
/// the shape that drifts.</b> <c>GET /api/me/account-keys</c> hands back every factor's wrapped private
/// key and the account keys encapsulated to it; <c>GET /api/me/key-rotation</c> hands back a staged
/// seal per factor — the <em>only</em> copies of the generation an interrupted run was rewriting the
/// account under — and the manifest staged beside them. Each route writes this value itself; what is
/// shared is the value, never the decision, so a third route does not inherit the header by being
/// written in the same file.
/// </para>
/// <para>
/// <b><see cref="SecurityHeadersMiddleware" /> still owns no global <c>Cache-Control</c>, and this
/// constant is not the beginning of one.</b> That class argues the refusal where it makes it: a blanket
/// value would settle cacheability in the one place that knows least about what was returned. A shared
/// literal is the opposite of a shared decision — it says what <c>no-store</c> is spelled as, and
/// nothing about which response deserves it.
/// </para>
/// <para>
/// <b><c>no-store</c> alone, not the longer incantation.</b> <c>no-cache</c> permits storage and
/// requires revalidation, which is the opposite of what is wanted; <c>private</c> permits a browser
/// cache; <c>max-age=0</c> without <c>no-store</c> permits a stale-serving cache to keep the bytes. The
/// four together are a superstition that reads as more careful and stores more — and the tests compare
/// the header to this exact string rather than parsing it, because a parsed read answers
/// <c>NoStore</c> true for all four as readily as for this one.
/// </para>
/// <para>
/// <b>What a route owes beside writing it.</b> The value belongs to the <em>route</em> and never to
/// what the route happened to find: a rotation read that wrote it only when it had a staged run to
/// describe would serve a cacheable 200 to every account that has none, which is one member away from
/// the day such a response carries something. Both routes write it unconditionally.
/// </para>
/// </remarks>
internal static class ResponseCaching
{
    /// <summary>
    /// The whole <c>Cache-Control</c> value — this body may not be written to a shared cache, a disk
    /// cache or a back-button restore.
    /// </summary>
    internal const string NoStore = "no-store";
}
