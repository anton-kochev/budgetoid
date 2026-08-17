using System.Collections.Frozen;

namespace Api.Infrastructure;

/// <summary>
/// Puts the four browser-facing security headers on every response this application produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>What pins the registration is <c>FirstPartyRequestMiddleware</c>, and only that.</b> "Every response"
/// is the whole requirement, and a response a browser renders is often written by something other than a
/// route delegate. <c>FirstPartyRequestMiddleware</c> is the case that decides the position: it answers 403
/// without invoking <c>next</c>, so anything registered below it never runs on a refused request and that
/// 403 ships bare. Above it is the one position that runs on every request there is, whoever ends up
/// writing the response. Ordering against <c>UseExceptionHandler()</c> is <em>not</em> part of this — it was
/// measured to make no difference, for the reason the <c>OnStarting</c> paragraph gives.
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
/// write with all four names present and every value empty, while the other five paths passed. What
/// repaired it is the move to <c>OnStarting</c>, which fires after any such clear and just before the
/// response flushes — the only point at which "on the wire" and "written" mean the same thing. Moving the
/// registration is what does not repair it: with the write in <c>OnStarting</c>, registering this
/// middleware <em>below</em> <c>UseExceptionHandler()</c> still delivers all four headers on a 500 —
/// measured. <c>HttpResponse.Clear()</c> resets the status and the headers and does not touch the
/// <c>OnStarting</c> callback list, which lives on the response feature and has no public API to reset: the
/// middleware still runs, still registers the callback, and the callback still fires at flush, after the
/// clear. Writing the four headers allocates nothing per response: the callback is <c>static</c> and takes
/// the response as state, so registering it captures no closure, and <see cref="Headers" /> is declared as
/// the concrete <see cref="FrozenDictionary{TKey,TValue}" />, so the <c>foreach</c> binds its struct
/// enumerator instead of boxing one behind an interface.
/// </para>
/// <para>
/// <b><c>/health</c> gets no exemption, unlike the first-party header control.</b> That exemption exists
/// because the container platform's probe cannot be taught to <em>send</em> a request header of ours.
/// Response headers run the other way: the probe receives them and ignores them, so exempting the route
/// would make one response differ from every other for no reason anybody could state.
/// </para>
/// <para>
/// <b>The tripwire.</b> The day somebody adds a Swagger or Scalar UI, <c>default-src 'none'</c> blanks it —
/// no script, no stylesheet, no font will load. The answer is an opt-out read <em>inside this middleware's
/// own callback</em>, off endpoint metadata: <c>((HttpResponse)state).HttpContext.GetEndpoint()</c> is
/// available at flush time, so the callback can find a marker on the documentation endpoint and write that
/// endpoint's policy instead — verified in a probe. Neither shape of "override it on that one endpoint"
/// works, and both were measured. An endpoint assigning
/// <c>Response.Headers.ContentSecurityPolicy</c> while it handles the request is overwritten, because
/// <em>any</em> <c>OnStarting</c> callback runs later, at flush. An endpoint registering its own
/// <c>OnStarting</c> loses too: Kestrel keeps these callbacks in a <c>Stack</c> and runs them LIFO —
/// confirmed in a stack trace through
/// <c>HttpProtocol.&lt;FireOnStarting&gt;g__ProcessEvents|240_0(HttpProtocol, Stack&lt;T&gt;)</c> — so this
/// middleware's, registered first and outermost, runs <em>last</em> and overwrites the endpoint's. Metadata
/// rather than <c>HttpContext.Items</c>, because metadata fails closed: <c>ExceptionHandlerMiddleware</c>
/// nulls the endpoint, so a 500 can never inherit a documentation page's relaxed policy. Never loosen the
/// global policy so a documentation page can render. Today <c>MapOpenApi()</c> is Development-only and
/// serves JSON, and the only OpenAPI package is <c>Microsoft.AspNetCore.OpenApi</c>, so no HTML UI exists
/// here to break.
/// </para>
/// <para>
/// <b>The price the callback pays for firing last: a throw in any other <c>OnStarting</c> callback drops all
/// four headers.</b> Kestrel's <c>ProcessEvents</c> holds its <c>try</c>/<c>catch</c> outside the pop loop,
/// so the first throw abandons the rest of the stack, and LIFO puts this callback at the bottom of it —
/// the most exposed position there is. Measured: a probe endpoint registering a throwing callback produced
/// a bodyless 500 carrying none of the four headers. This application registers exactly one
/// <c>OnStarting</c> callback today and it is this one, so nothing is broken; the day a second one appears,
/// this one becomes conditional on it.
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
/// serves JSON to a client served from elsewhere. A global <c>Cache-Control: no-store</c>: no endpoint in
/// this application states its cacheability — <c>Cache-Control</c>, <c>[ResponseCache]</c> and output
/// caching appear nowhere under <c>Api</c>, this sentence aside — so the question is open, not delegated,
/// and a blanket value here would settle it in the one place that knows least about what was returned.
/// Adding the header is a decision of its own and this class does not make it.
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
    /// The declared type is the concrete <see cref="FrozenDictionary{TKey,TValue}" /> rather than
    /// <see cref="IReadOnlyDictionary{TKey,TValue}" />, which is what lets the callback's <c>foreach</c>
    /// bind the struct enumerator; widening it back to the interface costs a boxed enumerator on every
    /// response and nothing catches it. The comparer is <see cref="StringComparer.Ordinal" /> because this
    /// is a fixed set pinned by exact bytes — <see cref="IHeaderDictionary" /> is already case-insensitive
    /// on the write side, so nothing here needs to be.
    /// </remarks>
    public static readonly FrozenDictionary<string, string> Headers =
        new KeyValuePair<string, string>[]
        {
            new("Strict-Transport-Security", "max-age=63072000; includeSubDomains"),
            new(
                "Content-Security-Policy",
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"),
            new("Referrer-Policy", "no-referrer"),
            new("X-Content-Type-Options", "nosniff"),
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
