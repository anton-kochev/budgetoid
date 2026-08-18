using Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// The three properties of the registration group that live in the route table rather than in any
/// request: which routes name the identity provider's scheme, which routes declare that they create an
/// account, and that no route declares both that and the permission to mint one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Structural rather than behavioural, for the reason <see cref="UserProvisioningRouteTests" /> gives
/// about its own marker.</b> A sweep of requests can only report what happened on the routes somebody
/// thought to try; a permission is a property of the route table and has to be readable whole. That is
/// sharper here than anywhere else on this surface, because the thing being read — a policy naming a
/// scheme — is invisible from the outside: every route in this application answers <b>401</b> to a caller
/// it will not admit, including a route that does not exist, so no status code distinguishes "this route
/// names the provider's scheme" from "this route inherits the fallback policy" from "nobody wrote it".
/// </para>
/// <para>
/// <b>Production, and the environment is load-bearing</b> for the same reason it is in
/// <see cref="AnonymousSurfaceTests" />: <c>Program.cs</c> maps the OpenAPI document in Development only,
/// so the route table genuinely differs by environment and this pins the one that ships. Production also
/// skips the Development startup block, so the connection string below reaches nothing — reading the
/// route table needs no database.
/// </para>
/// <para>
/// <b>Every test here carries the two non-vacuity controls, and they are not decoration.</b> Each claim
/// below is a set comparison or an absence, and a route table that came back empty — a metadata shape
/// that changed, an enumeration read before the data source was populated — satisfies an absence
/// perfectly and satisfies a set comparison whenever the expectation happens to be empty too. So each
/// test asserts that what it is looking for was found <em>somewhere</em>, and that the table holds more
/// routes than the ones it found.
/// </para>
/// </remarks>
public sealed class RegistrationRouteTests
{
    /// <summary>
    /// The two routes of the registration group, exactly as the route table spells them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The finish leg carries a trailing slash because it is mapped as <c>MapPost("/")</c> under a group
    /// whose prefix has none, and route-pattern combination joins the two literally. Written out rather
    /// than normalised: the pattern is what the route table holds and what a request has to match, and a
    /// test that trimmed it would keep passing on the day the group's own prefix changed shape.
    /// </para>
    /// <para>
    /// Written out rather than derived, for the reason <see cref="AnonymousSurfaceTests" />' list is: a
    /// set read off the route table agrees with whatever the route table says, and the value here is that
    /// a third route joining this group is a red somebody has to answer for in the same commit.
    /// </para>
    /// </remarks>
    private static readonly string[] RegistrationRoutes =
    [
        "/api/registration/",
        "/api/registration/options",
    ];

    /// <summary>
    /// NFR-025: the identity provider's scheme is named by the two registration routes and by nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both directions, and each fails for its own reason.</b> A route that gained the scheme is a
    /// route taken out of the fallback policy — which carries <c>RequireAuthenticatedUser</c> <em>and</em>
    /// <c>FullSessionRequirement</c> — and put on a path where a bearer token this product cannot revoke
    /// is sufficient. A registration route that lost it is the more interesting half: the policy would
    /// fall back to the default scheme, which is the <c>Budgetoid.Bridge</c> policy scheme, and a browser
    /// already holding a session cookie would then reach these two routes on that session. The account it
    /// created is one no provider vouched for, and nothing else in this suite would notice.
    /// </para>
    /// <para>
    /// <b>The scheme is read from both places the framework can record it</b>, because
    /// <c>RequireAuthorization(Action&lt;AuthorizationPolicyBuilder&gt;)</c> builds the policy eagerly and
    /// the shape it lands in endpoint metadata as is a framework detail rather than a property of this
    /// application. Reading only one of them would make this test go quiet — reporting an empty set, which
    /// reads exactly like the rule holding — on a framework upgrade that moved it. Reading both fails
    /// closed instead: the comparison is against a written-out set of two, so a reader finding the scheme
    /// nowhere reports an empty set against a non-empty expectation.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheProviderScheme_IsNamedByExactlyTheTwoRegistrationRoutes()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        string[] namingTheProvider = endpoints
            .Where(endpoint => SchemesNamedBy(endpoint)
                .Contains(ProviderAuthentication.SchemeName, StringComparer.Ordinal))
            .Select(PatternOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — joined rather than compared as collections so a failure names the route that moved
        // instead of reporting that two sets differ.
        await Assert.That(string.Join(", ", namingTheProvider))
            .IsEqualTo(string.Join(", ", RegistrationRoutes.Order(StringComparer.Ordinal)));

        // The two controls AnonymousSurfaceTests carries, and they are what stop this passing over a
        // route table that came back empty: the scheme is named somewhere, and the table holds routes
        // that do not name it.
        await Assert.That(namingTheProvider.Length).IsGreaterThan(0);
        await Assert.That(endpoints.Length).IsGreaterThan(namingTheProvider.Length);
    }

    /// <summary>
    /// <see cref="RegistersAccountAttribute" /> is carried by the registration group and by nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both directions.</b> A route that gained the marker is a route
    /// <c>UserProvisioningMiddleware</c> lets through with nobody published — which is correct on exactly
    /// the two legs of the ceremony that creates an account, and on every other route is a request that
    /// reaches a handler while <c>app.current_user_id</c> is empty. A registration route that lost it is
    /// the loud direction: the middleware answers <c>NoAccountTitle</c> and nobody can ever register.
    /// </para>
    /// <para>
    /// Compared as whole patterns rather than as group prefixes, unlike
    /// <see cref="UserProvisioningRouteTests.ProvisionsUserMetadata_IsCarriedByExactlyTheDataRouteGroups" />.
    /// That test compares prefixes so adding an endpoint to an already-permitted group is not a red;
    /// here a third route under this prefix is precisely the change that has to be argued for, because
    /// the marker is what lets a request past the account gate.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegistersAccountMetadata_IsCarriedByExactlyTheRegistrationGroup()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        string[] marked = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<RegistersAccountAttribute>() is not null)
            .Select(PatternOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert
        await Assert.That(string.Join(", ", marked))
            .IsEqualTo(string.Join(", ", RegistrationRoutes.Order(StringComparer.Ordinal)));

        // The controls: the marker exists on this route table at all, and the table was really read.
        await Assert.That(marked.Length).IsGreaterThan(0);
        await Assert.That(endpoints.Length).IsGreaterThan(marked.Length);
    }

    /// <summary>
    /// No route carries both <see cref="RegistersAccountAttribute" /> and
    /// <see cref="ProvisionsUserAttribute" />, because the middleware can only honour one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror of
    /// <see cref="UserProvisioningRouteTests.AnonymousAndProvisioningMarkers_AreCarriedByDisjointRouteGroups" />,
    /// and it fails the same way: the two answer questions that read as compatible — "this route serves
    /// callers who have no account" and "this route may bring one into existence" — while the middleware
    /// reads the registers-an-account arm <b>first</b> and returns. A route carrying both never reaches
    /// find-or-create and quietly mints nothing for a group whose author asked it to, and the failure is
    /// invisible to everyone who already has an account.
    /// </para>
    /// <para>
    /// It is worse than redundant on this marker in one further way. A registration route publishes no
    /// identity by design, and the account identifier is derived from the ceremony's own challenge and
    /// published only after the signature verifies; the provisioning arm would name an account off a
    /// provider token instead, before a passkey had proved anything.
    /// </para>
    /// <para>
    /// The two counts are the controls, and they are load-bearing because this assertion is about an
    /// <em>absence</em>: a metadata lookup that came back null for every endpoint — a renamed attribute,
    /// a group that stopped applying one — satisfies "nowhere together" perfectly while proving nothing.
    /// Each marker has to be found somewhere for the claim to mean anything.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegistersAccountAndProvisionsUser_AreCarriedByDisjointRoutes()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        string[] registersAccount = PatternsCarrying<RegistersAccountAttribute>(endpoints);
        string[] provisionsUser = PatternsCarrying<ProvisionsUserAttribute>(endpoints);
        string[] carryingBoth = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<RegistersAccountAttribute>() is not null
                && endpoint.Metadata.GetMetadata<ProvisionsUserAttribute>() is not null)
            .Select(PatternOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — joined rather than compared as a collection so a failure names the offending route
        // instead of only reporting that a set was not empty.
        await Assert.That(string.Join(", ", carryingBoth)).IsEqualTo(string.Empty);

        // The controls. Both markers exist on this route table; the claim above is that they never meet.
        await Assert.That(registersAccount.Length).IsGreaterThan(0);
        await Assert.That(provisionsUser.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// Every authentication scheme this endpoint's authorization declares, however the framework records
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two sources, unioned. A policy built eagerly by
    /// <c>RequireAuthorization(Action&lt;AuthorizationPolicyBuilder&gt;)</c> can reach the metadata as an
    /// <see cref="AuthorizationPolicy" /> carrying <see cref="AuthorizationPolicy.AuthenticationSchemes" />,
    /// and a policy declared by attribute reaches it as <see cref="IAuthorizeData" /> carrying a
    /// comma-separated string. Which of the two a given framework version uses is not a property of this
    /// application, and a reader that knew only one of them would report an empty set on the upgrade that
    /// changed it — a green that means the test stopped looking.
    /// </para>
    /// <para>
    /// Entries are trimmed and empties dropped, because <see cref="IAuthorizeData.AuthenticationSchemes" />
    /// is a raw string that the framework itself splits that way.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> SchemesNamedBy(RouteEndpoint endpoint) =>
    [
        .. endpoint.Metadata.OfType<AuthorizationPolicy>()
            .SelectMany(policy => policy.AuthenticationSchemes),
        .. endpoint.Metadata.OfType<IAuthorizeData>()
            .Select(data => data.AuthenticationSchemes)
            .Where(schemes => !string.IsNullOrWhiteSpace(schemes))
            .SelectMany(schemes => schemes!.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
    ];

    /// <summary>
    /// The distinct route patterns whose endpoint metadata carries <typeparamref name="TMarker" />.
    /// </summary>
    private static string[] PatternsCarrying<TMarker>(IEnumerable<RouteEndpoint> endpoints)
        where TMarker : class =>
        endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<TMarker>() is not null)
            .Select(PatternOf)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The pattern an endpoint answers on, or a sentence in its place.
    /// </summary>
    /// <remarks>
    /// A named placeholder rather than <see cref="string.Empty" />, so an endpoint whose raw text is null
    /// shows up in a failure message as something a reader can look for instead of as a stray comma.
    /// </remarks>
    private static string PatternOf(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText ?? "<no pattern>";
}
