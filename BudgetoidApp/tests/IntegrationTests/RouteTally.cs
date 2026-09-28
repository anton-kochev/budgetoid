using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// Records which route every request a host served was matched to, and what it answered, so a census
/// can ask which routes of the table its traffic reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>A startup filter, so it wraps the whole pipeline</b>, and reads the endpoint once the request
/// has come back out: routing has chosen it by then, and the status is the one the client received.
/// </para>
/// <para>
/// A route counts as <b>reached</b> only on a 2xx. A 401 from the authorization middleware selects
/// the endpoint too, and a floor that counted it would call a route driven whose handler never ran.
/// </para>
/// </remarks>
public sealed class RouteTally : IStartupFilter
{
    private readonly ConcurrentQueue<(string Route, int Status)> _hits = new();

    /// <summary>Registers the tally on a host's services.</summary>
    public void AttachTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IStartupFilter>(this);
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            try
            {
                await pipeline(context);
            }
            finally
            {
                if (context.GetEndpoint() is RouteEndpoint endpoint)
                {
                    // Keyed as Declared keys it, so a route naming no method is reached under '*'.
                    string method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is null
                        ? AnyMethod
                        : context.Request.Method;
                    _hits.Enqueue((KeyOf(method, endpoint), context.Response.StatusCode));
                }
            }
        });

        next(app);
    };

    /// <summary>Every route answered with a 2xx at least once, as <c>METHOD pattern</c>.</summary>
    public IReadOnlySet<string> Reached() =>
        new HashSet<string>(
            _hits.Where(hit => hit.Status is >= 200 and < 300).Select(hit => hit.Route),
            StringComparer.Ordinal);

    /// <summary>
    /// Every route the host's table declares, as <c>METHOD pattern</c>, one entry per method — and one
    /// <c>* pattern</c> entry for a route that names no method, which answers every one.
    /// </summary>
    /// <remarks>
    /// A route with no <see cref="HttpMethodMetadata" /> is still a route: <c>MapHealthChecks</c> maps
    /// one. Skipping it would leave it out of the floor with nothing saying so.
    /// </remarks>
    public static IReadOnlySet<string> Declared(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return new HashSet<string>(
            services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is { } methods
                    ? methods.HttpMethods.Select(method => KeyOf(method, endpoint))
                    : [KeyOf(AnyMethod, endpoint)]),
            StringComparer.Ordinal);
    }

    /// <summary>The method a route that names none is keyed under.</summary>
    private const string AnyMethod = "*";

    private static string KeyOf(string method, RouteEndpoint endpoint) =>
        $"{method.ToUpperInvariant()} {endpoint.RoutePattern.RawText}";
}
