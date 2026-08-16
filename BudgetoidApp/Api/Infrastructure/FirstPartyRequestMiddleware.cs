using Microsoft.Extensions.Primitives;

namespace Api.Infrastructure;

/// <summary>
/// Refuses any request that does not name itself as coming from a first-party client. One route is
/// exempt — the container platform's liveness probe — and nothing else is.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the CSRF control, and the session cookie is what makes it necessary.</b> A browser
/// attaches a <c>SameSite=Lax</c> cookie to a top-level cross-site navigation, and it attaches nothing
/// to a cross-origin <c>fetch</c> carrying a header the preflight has to allow. So a header no
/// cross-site form can add is what stands between a session cookie and a request the person never made.
/// </para>
/// <para>
/// <b>The value is read for presence and is otherwise unchecked, deliberately.</b> An attacker able to
/// set the header at all could set any value in it, so a checked value buys nothing — and it would be a
/// shared secret shipped to every client, which is a secret only in the sense that nobody has looked.
/// Do not "harden" this by comparing it to a configured string.
/// </para>
/// <para>
/// <b>It covers the anonymous routes too, and that is the half a reader will want to drop.</b> The
/// routes that <em>set</em> a cookie are anonymous by definition — nobody is signed in yet — and
/// login-CSRF is exactly the attack of signing somebody into an account they do not own so that what
/// they do next is recorded under it. A control that started at authentication would leave open the
/// only routes where a cookie is handed out. There is deliberately no list of anonymous routes here:
/// which routes serve an unauthenticated caller is a property of the route table that
/// <c>AnonymousSurfaceTests</c> reads off the <c>IAllowAnonymous</c> metadata, and a second definition
/// of that set would be one able to disagree with it.
/// </para>
/// <para>
/// <b>The one exemption is the liveness probe.</b> The container platform's probe carries no header of
/// ours and cannot be taught to, and the route discloses nothing but that the process is up — it runs
/// no dependency checks. It is matched by path against the constant <c>ServiceDefaults</c> maps it
/// under, so the exemption and the route cannot drift into two spellings.
/// </para>
/// <para>
/// <b>403 rather than 401, and it is not a near-miss.</b> 401 says "authenticate and try again", which
/// is wrong twice over: on an anonymous route there is nothing to authenticate, and on an authenticated
/// one the caller's credentials were never the problem. Nothing the caller can supply in a second
/// attempt makes a cross-site request first-party.
/// </para>
/// </remarks>
public sealed class FirstPartyRequestMiddleware(RequestDelegate next)
{
    /// <summary>
    /// The header a first-party client names itself with.
    /// </summary>
    /// <remarks>
    /// <b>Wire contract.</b> Every client already deployed sends these bytes, so a rename refuses every
    /// one of them at once. The tests that drive it write the name out as a literal of their own rather
    /// than reading this constant, so a rename goes red instead of agreeing with itself.
    /// </remarks>
    public const string ClientHeader = "X-Budgetoid-Client";

    /// <summary>
    /// The one sentence a refused request receives. Public so a test can pin it, and distinct from
    /// every titled refusal <see cref="UserProvisioningMiddleware" /> produces: the corrective action
    /// here is "send the header", which no other refusal on this path asks for.
    /// </summary>
    public const string Title = "Request did not come from a first-party client.";

    public async Task InvokeAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (IsLivenessProbe(httpContext) || NamesAFirstPartyClient(httpContext))
        {
            await next(httpContext);
            return;
        }

        await Results.Problem(title: Title, statusCode: StatusCodes.Status403Forbidden)
            .ExecuteAsync(httpContext);
    }

    /// <summary>
    /// Whether the header is present with a value — any value.
    /// </summary>
    /// <remarks>
    /// Whitespace does not count as a value: a proxy or a client library that emits the header with an
    /// empty value would otherwise satisfy a control that exists to be satisfied only on purpose.
    /// </remarks>
    private static bool NamesAFirstPartyClient(HttpContext httpContext) =>
        httpContext.Request.Headers.TryGetValue(ClientHeader, out StringValues values)
        && !string.IsNullOrWhiteSpace(values.ToString());

    /// <summary>
    /// Whether this request is the liveness probe.
    /// </summary>
    /// <remarks>
    /// By path rather than by endpoint metadata, because <c>MapHealthChecks</c> leaves no metadata that
    /// identifies it: it maps a raw request delegate and marks it <c>AllowAnonymous</c>, which the
    /// sign-in routes carry too, so the only thing peculiar to it is the display name string
    /// <c>"Health checks"</c> — a weaker contract than the path, and one no test would notice changing.
    /// Case-insensitively, because ASP.NET routing matches a path that way; an ordinal comparison would
    /// leave <c>/Health</c> serving the probe while this refused it.
    /// </remarks>
    private static bool IsLivenessProbe(HttpContext httpContext) =>
        httpContext.Request.Path.Equals(
            ServiceDefaults.Extensions.HealthPath,
            StringComparison.OrdinalIgnoreCase);
}
