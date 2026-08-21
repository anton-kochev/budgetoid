using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// Every route this application serves without a token, read off the route table and compared against a
/// written-out set — and, for the two claims the route table cannot make, the requests that make them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the claim it executes was, until now, only asserted in a comment.</b>
/// <c>PasskeyEndpoints</c> says the two groups over one prefix are there so "the anonymous surface of the
/// whole application is a single line a reviewer can see". That was already one line short — the health
/// check in <c>ServiceDefaults</c> is anonymous too, and it is nowhere near that file — and it stops
/// being a line at all the moment a second feature needs an anonymous route. A reviewer can miss a line;
/// a set compared whole cannot be missed. The comment is not wrong about the <em>intent</em>, so it stays
/// where it is; this test is what makes the intent checkable.
/// </para>
/// <para>
/// <b>The list is written out rather than derived.</b> A list read off the route table agrees with
/// whatever the route table says, which is the one thing this may not do: the value here is that adding
/// an anonymous route is a red test somebody has to answer for in the same commit, by adding the pattern
/// <em>and</em> the argument for it below.
/// </para>
/// <para>
/// <b>The set is pinned structurally, and the reason is that a sweep cannot pin a set.</b> A run of
/// unauthenticated requests could only report what happened on the routes it thought to try, while the
/// permission is a property of the route table and has to be readable whole. That is why
/// <see cref="EveryAnonymousRoute_IsOneOfTheOnesArguedFor" /> reads <see cref="IAllowAnonymous" /> off
/// the endpoints and issues no request at all.
/// </para>
/// <para>
/// <b>Three tests below issue real requests, and they are not a duplicate of that one.</b> Reading the
/// marker says a route is <em>declared</em> anonymous; it says nothing about whether the route answers
/// <c>200</c> when reached that way, and for <c>/health</c> that is the entire claim the container
/// platform depends on. Nor does it say what happens on the other side of the line — that a route
/// carrying no marker refuses an unauthenticated caller with a body a client can read. They arrived here
/// from a deleted <c>AuthenticationTests</c>, whose remaining tests were all about a provisioning
/// middleware that no longer exists.
/// </para>
/// <para>
/// <b>Production, and the environment is load-bearing.</b> <c>Program.cs</c> maps the OpenAPI document
/// anonymously in Development only, so the surface genuinely differs by environment and this pins the one
/// that ships. Production also skips the Development startup block, so the connection string below
/// reaches nothing — reading the route table needs no database. A Development pin would be a second list
/// differing from this one by a document nobody deploys.
/// </para>
/// </remarks>
public sealed class AnonymousSurfaceTests
{
    /// <summary>
    /// Every route pattern that may be reached without authenticating, and the argument for each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>/health</c> — the container platform's liveness probe, which runs before anything could
    /// authenticate to it and carries no dependency checks, so it discloses nothing but that the process
    /// is up.
    /// </para>
    /// <para>
    /// <c>/api/passkeys/assertion/options</c> and <c>/api/passkeys/assertion</c> — the sign-in exchange,
    /// which by definition runs before anyone is signed in. Both mint or spend a nonce and neither reads
    /// tenant data; the account a sign-in lands on is taken from the verified credential, never from the
    /// request.
    /// </para>
    /// <para>
    /// <c>/api/recovery-codes/redemption</c> — signing in with a recovery code, which by definition runs
    /// before anyone is signed in and, unlike the passkey exchange, cannot be made to run any other way:
    /// somebody redeeming a code has lost the authenticator that would have proved who they are. It
    /// creates no account and must never start: an account now comes into existence on exactly one
    /// path, <c>POST /api/registration</c>, and a route that redeems a code for an account that no
    /// longer exists must answer nothing rather than mint one.
    /// <b>Before it knows who is asking</b> it
    /// does exactly two things: it decodes the presented verifier against an exact 32-byte ceiling, and
    /// it looks one <c>recovery_code_hashes</c> row up by <c>SHA-256</c> of it. That lookup is one of the
    /// three reads in the system naming no owner — the others being the passkey discovery lookup and the
    /// challenge consume, each on a table exempt for the same reason — and it is what
    /// <c>recovery_code_hashes</c>' own row-level-security exemption exists for (ADR 0016); the account
    /// it lands on is taken from the matched row, never from the request, so a bearer token a client
    /// interceptor attached changes nothing. Everything after it — the consume and the session insert —
    /// runs with an identity published and a transaction opened in that order, which is the property
    /// <see cref="RecoveryCodeRedemptionTests" /> holds. <b>From its refusal a caller learns nothing.</b>
    /// Every cause — absent member, not base64url, wrong width, past the ceiling, no such code, already
    /// spent — leaves as one 401 with one title, byte for byte, because telling "no such code" from
    /// "that code was already used" would say that a value the caller presented was once real.
    /// </para>
    /// <para>
    /// A pattern added here needs its own paragraph, and the paragraph is the review. Two questions have
    /// to be answered in it: what the route does before it knows who is asking, and what a caller learns
    /// from its refusal.
    /// </para>
    /// </remarks>
    private static readonly string[] AnonymousRoutes =
    [
        "/health",
        "/api/passkeys/assertion/options",
        "/api/passkeys/assertion",
        "/api/recovery-codes/redemption",
    ];

    /// <summary>
    /// The anonymous surface is exactly the set argued for above — no more, and no fewer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both directions matter and neither is the interesting one on its own.</b> A route that gained
    /// the marker is the disclosure this test is for; a route that lost it is a sign-in nobody can reach
    /// without already being signed in, which is a lockout rather than a leak but is just as much a
    /// regression.
    /// </para>
    /// <para>
    /// <b>The two counts at the end are the controls.</b> This assertion is satisfied by an empty
    /// enumeration in exactly one case — an empty expectation — so the marker has to be found somewhere
    /// and the route table has to have been populated at all, or a metadata lookup that came back null
    /// for every endpoint would pass while proving nothing.
    /// </para>
    /// <para>
    /// Joined rather than compared as collections, so a failure names the route that moved instead of
    /// reporting that two sets differ. Ordered on both sides, because nothing promises the order the
    /// route table enumerates in.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryAnonymousRoute_IsOneOfTheOnesArguedFor()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        string[] anonymousPatterns = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert
        await Assert.That(string.Join(", ", anonymousPatterns))
            .IsEqualTo(string.Join(", ", AnonymousRoutes.Order(StringComparer.Ordinal)));

        // The controls: the marker exists on this route table, and the route table was really read.
        await Assert.That(anonymousPatterns.Length).IsGreaterThan(0);
        await Assert.That(endpoints.Length).IsGreaterThan(anonymousPatterns.Length);
    }

    /// <summary>
    /// A route carrying no anonymous marker refuses an unauthenticated caller, and says so in a body.
    /// </summary>
    /// <remarks>
    /// <b>This became more load-bearing, not less, when the provisioning middleware went.</b> While it
    /// stood, an unauthenticated request was turned away by a middleware that wrote its own titled
    /// ProblemDetails; now nothing between the cookie handler and the endpoint writes anything, and the
    /// refusal is produced entirely by the authorization middleware challenging the cookie scheme. The
    /// content type is half the assertion for that reason: a bare <c>401</c> with an empty body is what
    /// this path degrades to if the ProblemDetails registration is dropped, and every client in the
    /// product reads the body.
    /// </remarks>
    [Test]
    public async Task UnauthenticatedRequest_Returns401ProblemJson()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act
        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync("/api/transactions");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
    }

    /// <summary>
    /// <c>/health</c> really answers <c>200</c> to a caller holding nothing.
    /// </summary>
    /// <remarks>
    /// The set test above proves the marker is declared on this pattern. It cannot prove the route
    /// answers, and a health check that is anonymous and returns <c>503</c> fails a liveness probe just
    /// as thoroughly as one that demands a token.
    /// </remarks>
    [Test]
    public async Task Health_AllowsAnonymous()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act
        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync("/health");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// The only proof that <c>/health</c> answers <c>200</c> in Production with no database behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what a Container Apps liveness probe needs, and nothing else in the suite says it.</b>
    /// The factory names a connection string nothing is listening on and runs in Production, which skips
    /// the Development startup block that would migrate a database — so a health check that grew a
    /// dependency check, or a startup path that touched the database before the endpoint could answer,
    /// fails here and passes <see cref="Health_AllowsAnonymous" /> above, which has a live container
    /// behind it.
    /// </para>
    /// <para>
    /// The environment is load-bearing twice over: the anonymous surface itself differs by environment
    /// (Development maps the OpenAPI document anonymously), so a Development pin would be measuring a
    /// pipeline nobody deploys.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Health_AllowsAnonymousInProductionForContainerAppProbes()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres",
            environment: "Production");

        // Act
        HttpResponseMessage response = await factory.CreateClient().GetAsync("/health");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
