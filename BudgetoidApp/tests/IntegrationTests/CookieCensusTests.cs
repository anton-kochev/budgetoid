using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// No cookie is set for any purpose other than serving the request: across the log census's traffic,
/// which reaches every declared route, the only <c>Set-Cookie</c> name any response carries is the
/// session's.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cookie name is a literal here, not <c>SessionCookie.Name</c></b>, for the reason
/// <see cref="SessionCookieIssuanceTests" /> gives: a test reading the constant agrees with whatever the
/// constant says, so a rename would pass the census rather than redden it.
/// </para>
/// <para>
/// <b>Its own run of the traffic rather than a second assertion on
/// <see cref="LogRedactionTests" />'s.</b> A shared run would tie the two failure modes together: a log
/// offence would hide a cookie verdict and the other way round. The route floor is not restated: both
/// classes call <see cref="LogRedactionTests.AssertRouteFloorAsync" />.
/// </para>
/// <para>
/// <b>What a green run does not cover.</b> A branch of a route the traffic reaches without taking; the
/// provider-token path on the real bearer handler; a cookie appended from an <c>OnStarting</c> callback
/// registered before the tally's, which runs after the tally has read the headers; a host outside
/// Development, since <see cref="ApiFactory" /> boots this one in Development and a cookie set only on
/// another environment's branch is not seen; a cookie written outside ASP.NET Core's response headers
/// (nothing in this API can); cookies set outside the API — the web client's scripts and the static
/// host; and the attributes of the session cookie, which <see cref="SessionCookieIssuanceTests" /> owns.
/// </para>
/// </remarks>
public sealed class CookieCensusTests
{
    /// <summary>The one cookie the API may set. A literal on purpose; see the remarks on the class.</summary>
    private const string SessionCookieName = "__Host-budgetoid-session";

    [Test]
    public async Task Traffic_SetsNoCookieButTheSession()
    {
        // Arrange
        await using PostgresTestHost host = new(
            usesApplicationAuthentication: true, repointsProviderSchemeToTestHandler: true);
        await host.StartAsync();

        LogRecorder recorder = new();
        RouteTally tally = new();
        ApiFactory app = host.CreateFactory(configureServices: services =>
        {
            recorder.AttachTo(services);
            tally.AttachTo(services);

            // As LogRedactionTests has it, so the traffic takes the same paths.
            services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        });

        // Act
        IReadOnlySet<string> declared;
        await using (app)
        {
            await LogCensusTraffic.DriveAsync(host, app, recorder, _ => Task.CompletedTask);
            declared = RouteTally.Declared(app.Services);
        }

        IReadOnlySet<string> reached = tally.Reached();
        IReadOnlySet<string> cookies = tally.CookiesSet();
        Console.WriteLine(
            $"Routes declared: {declared.Count}. "
            + $"Cookie names seen: {string.Join(", ", cookies.Order(StringComparer.Ordinal))}");

        // Assert — the route floor first: a census that reached nothing has nothing to say.
        await LogRedactionTests.AssertRouteFloorAsync(declared, reached);

        // Non-vacuity: the capture saw the one cookie the traffic is known to set.
        await Assert.That(cookies).Contains(SessionCookieName);

        // The rule.
        await Assert.That(cookies.Except([SessionCookieName], StringComparer.Ordinal).ToArray()).IsEmpty();
    }

    /// <summary>
    /// The control: a cookie appended from <c>OnStarting</c> by a middleware inside the tally, on a
    /// request no endpoint matches and whose response has no body, is still recorded.
    /// </summary>
    /// <remarks>
    /// Body-less is the point. Such a response starts only after the whole pipeline has returned, so a
    /// capture that read the headers once the pipeline came back would miss this cookie.
    /// </remarks>
    [Test]
    public async Task Census_ReportsACookieAddedOutsideAnEndpoint()
    {
        // Arrange
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();

        RouteTally tally = new();
        await using ApiFactory factory = host.CreateFactory(configureServices: services =>
        {
            tally.AttachTo(services);

            // Registered after the tally, so it runs inside it.
            services.AddSingleton<IStartupFilter>(new ProbeCookieFilter());
        });

        // Signed in, because the fallback policy answers an anonymous unmatched request with a 401 body.
        ApiFactory.SignedInClient signedIn = await factory.CreateSignedInClientAsync();

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(ProbePath);
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        // Assert — the request matched nothing and the response carried no body. The bytes are read
        // rather than Content-Length, which a chunked body leaves unset.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(body.Length).IsEqualTo(0);
        await Assert.That(tally.CookiesSet()).Contains(ProbeCookieName);
    }

    private const string ProbePath = "/cookie-census-probe-matches-no-route";
    private const string ProbeCookieName = "probe";

    /// <summary>Appends <c>probe</c> from <c>OnStarting</c> on <see cref="ProbePath" /> only.</summary>
    private sealed class ProbeCookieFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pipeline) =>
            {
                if (context.Request.Path == ProbePath)
                {
                    context.Response.OnStarting(() =>
                    {
                        context.Response.Cookies.Append(ProbeCookieName, "1");
                        return Task.CompletedTask;
                    });
                }

                await pipeline(context);
            });

            next(app);
        };
    }
}
