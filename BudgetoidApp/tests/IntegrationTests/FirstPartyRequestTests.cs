using System.Net;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// That every request this application serves — bar the liveness probe — has to say it came from a
/// first-party client, and that the exemption is exactly one route.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the CSRF control, and a cookie is what makes it necessary.</b> A browser attaches a
/// <c>SameSite=Lax</c> cookie to a top-level cross-site navigation, and it attaches nothing to a
/// cross-origin <c>fetch</c> carrying a header the preflight has to allow. So a header no cross-site
/// form can add is what stands between a session cookie and a request the person never made. Its
/// <em>value</em> is immaterial and deliberately unchecked: an attacker who could set the header could
/// set any value in it, and a shared-secret reading of this control would be a secret shipped to every
/// client.
/// </para>
/// <para>
/// <b>It covers the anonymous routes too, and that is the half a reader will want to drop.</b> The
/// routes that <em>set</em> a cookie are anonymous by definition — nobody is signed in yet — and a
/// login-CSRF is exactly the attack of signing somebody into an account they do not own so that what
/// they do next is recorded under it. A control that started at authentication would leave those open.
/// </para>
/// <para>
/// The header name is a literal here for the reason
/// <see cref="SessionCookieAuthenticationTests" /> states once for both files.
/// </para>
/// </remarks>
public sealed class FirstPartyRequestTests
{
    /// <summary>
    /// The header a first-party client names itself with. See the remarks on
    /// <see cref="SessionCookieAuthenticationTests" /> for why this is a literal.
    /// </summary>
    internal const string ClientHeader = "X-Budgetoid-Client";

    /// <summary>
    /// Any non-empty value satisfies the control, so this one is arbitrary — and being arbitrary is the
    /// assertion: a test carrying a value the server recognised would hide a check nobody meant to add.
    /// </summary>
    internal const string ClientHeaderValue = "budgetoid-web";

    private const string HealthPath = "/health";
    private const string MePath = "/api/me";
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";

    [Test]
    public async Task ARequestWithoutTheClientHeader_IsRefused()
    {
        // Arrange — a real signed-in account, so the authenticated arm is a request that would
        // otherwise have succeeded. Refusing a request that was going to be refused anyway says
        // nothing.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        HttpClient client = CreateHeaderlessClient(factory);

        // Act — the authenticated route, then the anonymous one that begins a sign-in. Neither carries
        // the header.
        HttpResponseMessage authenticated = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, MePath));
        HttpResponseMessage anonymous = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, AssertionOptionsPath));

        // Assert — 403 on both, and the anonymous one is the load-bearing half: it is 401 that a
        // control starting at authentication would answer there, and 401 on a route nobody has to
        // authenticate for would mean the control never ran.
        await Assert.That(authenticated.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Health_NeedsNoClientHeader()
    {
        // Arrange — the container platform's probe carries no headers of ours and cannot be taught to.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);

        // Act
        HttpResponseMessage response = await CreateHeaderlessClient(factory)
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, HealthPath));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// That <c>/health</c> is exempt and that nothing else is — the set compared whole, in both
    /// directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Behavioural rather than structural, and it has to be.</b> <see cref="AnonymousSurfaceTests" />
    /// reads its permission off the route table because <c>IAllowAnonymous</c> is metadata a route
    /// carries. This exemption is not: it lives inside whatever the control is implemented as, so the
    /// only way to ask a route whether it is exempt is to send it a request without the header. That
    /// makes this a sample rather than a proof — it drives every route whose pattern takes no
    /// parameters, one method each, which is most of the table but not the whole of it. A route
    /// reachable only with an id in the path is not swept here.
    /// </para>
    /// <para>
    /// <b>The routes are read off the route table rather than written out</b>, which is the opposite of
    /// what <see cref="AnonymousSurfaceTests" /> does and for a reason: that test's value is that adding
    /// an anonymous route goes red until somebody argues for it, while this one's value is that a route
    /// added tomorrow is swept without anybody remembering to add it. The written-out half here is the
    /// answer — one exemption, named — not the input.
    /// </para>
    /// <para>
    /// <b>The count at the end is the control.</b> The comparison is satisfied by a sweep that found one
    /// route in total, so the route table has to have been read at all or a metadata change that emptied
    /// it would pass while proving nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheExemptSet_IsExactlyHealth()
    {
        // Arrange — a live host, because a request that is not refused by the control goes on to do
        // whatever the route does, and most of these reach the database.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        HttpClient client = CreateHeaderlessClient(factory);
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Parameterless patterns only: a route taking an id needs a value invented for it, and a 404 on
        // an invented id is a status this sweep would have to interpret rather than read.
        (string Method, string Pattern)[] routes = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } text && !text.Contains('{'))
            .Select(endpoint => (
                Method: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET",
                Pattern: endpoint.RoutePattern.RawText ?? string.Empty))
            .Distinct()
            .OrderBy(route => route.Pattern, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToArray();

        // Act — every one of them without the header. The body is a well-formed empty object so that a
        // route which reads one refuses it on its contents rather than on the parse, which would be a
        // 400 this sweep could not tell from a route the control let through.
        List<string> notRefused = [];
        foreach ((string method, string pattern) in routes)
        {
            HttpRequestMessage request = new(new HttpMethod(method), pattern);
            if (!string.Equals(method, "GET", StringComparison.Ordinal))
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response = await client.SendAsync(request);
            if (response.StatusCode is not HttpStatusCode.Forbidden)
            {
                notRefused.Add($"{method} {pattern}");
            }
        }

        // Assert — joined rather than compared as sets, so a failure names the route that is exempt and
        // should not be, or the one that is refused and must not be.
        await Assert.That(string.Join(", ", notRefused)).IsEqualTo($"GET {HealthPath}");
        await Assert.That(routes.Length).IsGreaterThan(1);
    }

    /// <summary>
    /// A client that is certain to send no client header, whatever the factory does by default.
    /// </summary>
    /// <remarks>
    /// The removal is a no-op today and is not decoration. Once the header is required, the whole
    /// existing suite has to carry it, and the one place that can give every client one is
    /// <see cref="ApiFactory" /> — at which point the three tests in this file would silently start
    /// sending the very header they exist to withhold, and each would report the answer to a different
    /// question while staying green. A test whose arrangement can be undone from outside it is a test
    /// that stops meaning what it says.
    /// </remarks>
    private static HttpClient CreateHeaderlessClient(ApiFactory factory)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ClientHeader);

        return client;
    }
}
