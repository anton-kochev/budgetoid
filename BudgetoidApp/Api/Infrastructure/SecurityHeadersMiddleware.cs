using System.Collections.Frozen;

namespace Api.Infrastructure;

/// <summary>
/// Puts the four browser-facing security headers on every response this application produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is registered outermost, above the exception handler, because "every response" is the whole
/// requirement.</b> Three kinds of response are written by something other than a route delegate — the
/// one an <c>IExceptionHandler</c> writes, the one the authentication challenge writes, and the one a
/// middleware that never calls <c>next</c> writes — and each of those is a response a browser renders.
/// The last kind is what pins the position: <c>FirstPartyRequestMiddleware</c> refuses without invoking
/// the rest of the pipeline, so anything registered below it never runs on a refused request and its 403
/// ships bare. Outermost is the one position that runs on every request there is, whoever ends up writing
/// the response.
/// </para>
/// <para>
/// <b>The write is unconditional and does not go through <c>UseHsts()</c>.</b> That helper keys on
/// <c>Request.IsHttps</c>, which is <see langword="false" /> behind Azure Container Apps ingress because
/// nothing in this repository configures forwarded-headers middleware — so it would emit nothing in the
/// one environment that needs it. RFC 6797 requires a user agent to ignore an HSTS header received over
/// plain HTTP, which makes writing it unconditionally inert in local development and deterministic under
/// test. There is deliberately no <c>IsDevelopment</c> branch: an environment-shaped header set is one
/// nobody tests in the shape that ships.
/// </para>
/// <para>
/// <b>Each header is assigned through the indexer, never appended.</b> Two writers appending would ship a
/// duplicate <c>Strict-Transport-Security</c>, and RFC 6797 has a user agent process the first and ignore
/// the rest — a duplicate is a silent downgrade to whichever copy happens to be first, not
/// belt-and-braces.
/// </para>
/// <para>
/// <b>The write happens from a <c>Response.OnStarting</c> callback, and it has to.</b> Writing the four
/// headers directly before <c>await next(...)</c> is the obvious version and it was tried first: it loses
/// them on every 500. ASP.NET Core's exception-handler middleware calls <c>HttpResponse.Clear()</c>
/// before it hands the exception to the registered handlers, which resets the status and <em>every</em>
/// header already on the response — measured, not assumed:
/// <c>SecurityHeaderTests.AResponseFromAnExceptionHandler_CarriesTheHeaders</c> failed against the direct
/// write with all four names present and every value empty, while the other five paths passed. Registering
/// outermost does not help, because the clear happens below this middleware and after it has run.
/// <c>OnStarting</c> fires after any such clear and just before the response flushes, which is the only
/// point at which "on the wire" and "written" mean the same thing. The callback is <c>static</c> and takes
/// the response as state so registering it allocates no closure.
/// </para>
/// <para>
/// <b><c>/health</c> gets no exemption, unlike the first-party header control.</b> That exemption exists
/// because the container platform's probe cannot be taught to <em>send</em> a request header of ours.
/// Response headers run the other way: the probe receives them and ignores them, so exempting the route
/// would make one response differ from every other for no reason anybody could state.
/// </para>
/// <para>
/// <b>The tripwire.</b> The day somebody adds a Swagger or Scalar UI, <c>default-src 'none'</c> blanks it —
/// no script, no stylesheet, no font will load. The right answer is to override the header <em>on that one
/// endpoint</em>, never to loosen the global policy so a documentation page can render. Today
/// <c>MapOpenApi()</c> is Development-only and serves JSON, and the only OpenAPI package is
/// <c>Microsoft.AspNetCore.OpenApi</c>, so no HTML UI exists here to break.
/// </para>
/// <para>
/// <b>There is no <c>X-Frame-Options</c>.</b> Every browser that can reach this API honours
/// <c>frame-ancestors</c>, and a browser honouring both ignores the older header — so it would be a second
/// spelling of one rule, able to disagree with the first and unable to change any outcome.
/// </para>
/// <para>
/// <b>Three headers are omitted on purpose</b>, each because it is what the next reader will suggest.
/// <c>Cross-Origin-Resource-Policy</c>: <c>same-origin</c> breaks the frontend, which is a different origin
/// from this API, and <c>same-site</c> is an amendment to the CORS story's argument rather than to this one.
/// <c>Permissions-Policy</c>: a document header governing a browsing context this host never creates — it
/// serves JSON to a client served from elsewhere. A global <c>Cache-Control: no-store</c>: cacheability is
/// owned by the endpoints that know what they returned, and a blanket value would defeat any future
/// conditional-GET work.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>
    /// The header set, exactly as it goes on the wire. Public so a test can pin it.
    /// </summary>
    /// <remarks>
    /// This dictionary is the wire contract: it is what the tests read, so a name or a value that drifts
    /// here drifts in one place rather than in two that can agree with each other while both being wrong.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains",
            ["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
            ["Referrer-Policy"] = "no-referrer",
            ["X-Content-Type-Options"] = "nosniff",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public Task InvokeAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        HttpResponse response = httpContext.Response;
        response.OnStarting(
            static state =>
            {
                IHeaderDictionary responseHeaders = ((HttpResponse)state).Headers;
                foreach ((string name, string value) in Headers)
                {
                    responseHeaders[name] = value;
                }

                return Task.CompletedTask;
            },
            response);

        return next(httpContext);
    }
}
