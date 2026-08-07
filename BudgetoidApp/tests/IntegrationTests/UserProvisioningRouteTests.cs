using Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// Which endpoints are allowed to bring an account into existence, read off the route table itself.
/// </summary>
/// <remarks>
/// <para>
/// Provisioning is opt-in per route group, and that direction is the whole design: a marker a route
/// has to ask for cannot be forgotten into existence. Opt-out would put every route added later on the
/// minting path by default, and the route added later is exactly the one nobody reviews for this.
/// </para>
/// <para>
/// Structural rather than behavioural on purpose. A behavioural sweep would need one authenticated
/// request per endpoint and could still only say what happened, not which routes carry the
/// permission — and the permission is the thing a reviewer has to be able to see whole.
/// </para>
/// <para>
/// The pattern is <c>BudgetRouteConstructionTests</c>': the factory runs in <c>Production</c> against
/// a connection string nothing connects to. Reading the route table needs no database, and Production
/// skips the Development startup block that would migrate one.
/// </para>
/// </remarks>
public sealed class UserProvisioningRouteTests
{
    /// <summary>
    /// The six route groups a client's first authenticated request may legitimately land on, and the
    /// only ones that may mint an account. Not all six read tenant data — currencies serves a shared
    /// reference table owned by nobody, and is in the set because the server cannot constrain which of
    /// the six the client opens on. Written out rather than derived, because a list derived from the
    /// route table would agree with whatever the route table said.
    /// </summary>
    private static readonly string[] DataRouteGroups =
    [
        "/api/accounts",
        "/api/categories",
        "/api/category-groups",
        "/api/currencies",
        "/api/payees",
        "/api/transactions",
    ];

    /// <summary>
    /// The route prefixes that must never mint: erasure, and both passkey groups. A stale provider
    /// token outlives an erasure by up to an hour, so an erasure route that provisions turns one
    /// in-flight request into a resurrected account — and the resurrected account holds no passkey, so
    /// erasure refuses it forever.
    /// </summary>
    private static readonly string[] MintingIsForbiddenUnder =
    [
        "/api/me",
        "/api/passkeys",
    ];

    [Test]
    public async Task ProvisionsUserMetadata_IsCarriedByExactlyTheDataRouteGroups()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        string[] markedPatterns = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ProvisionsUserAttribute>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();
        string[] markedGroups = markedPatterns
            .Select(GroupPrefixOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — joined rather than compared as collections so a failure names the offending group
        // instead of only reporting that two sets differ.
        await Assert.That(string.Join(", ", markedGroups))
            .IsEqualTo(string.Join(", ", DataRouteGroups.Order(StringComparer.Ordinal)));

        // The first control. A marker nobody ever applied satisfies every "this route is not marked"
        // assertion below perfectly, and would leave the whole application unable to provision at all.
        await Assert.That(markedPatterns.Length).IsGreaterThan(0);

        // The second control, stated separately from the set equality even though it follows from it:
        // this is the assertion whose failure message says which forbidden route acquired the marker,
        // and it is the one a reader checking the privacy claim looks for.
        string[] forbidden = markedPatterns
            .Where(pattern => MintingIsForbiddenUnder.Any(prefix =>
                pattern.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await Assert.That(string.Join(", ", forbidden)).IsEqualTo(string.Empty);

        // And the route table really was read: an enumeration that came back empty would satisfy every
        // "no offender" assertion above.
        await Assert.That(endpoints.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// No route carries both markers, because the middleware can only honour one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two markers answer questions that are compatible on paper — "this route may mint an account"
    /// and "this route serves callers who have none" — and mutually exclusive in the middleware, because
    /// the anonymous arm is read <b>first</b> and returns. A route carrying both takes the anonymous arm,
    /// never reaches find-or-create, and quietly mints nothing for a group whose author asked it to. The
    /// failure is silent: the endpoint answers normally for everyone who already has an account, and only
    /// a brand-new subject ever sees it. Nothing else in this suite would catch that, and a route
    /// declaring a permission the pipeline ignores is worse than one declaring nothing.
    /// </para>
    /// <para>
    /// Ordering-specific, and it belongs to the ordering rather than to either marker. Before the
    /// anonymous arm moved above the claim gate and credential resolution, a route carrying both would
    /// have minted — the combination was merely redundant. It is the new order that makes it a
    /// contradiction, which is why this is stated as its own test rather than folded into the group
    /// membership assertion above.
    /// </para>
    /// <para>
    /// The two counts below are the controls, and they are not decoration. This assertion is about an
    /// <em>absence</em>, and a metadata lookup that came back null for every endpoint — a renamed
    /// attribute, a group that stopped applying it, a route table read before it was populated — would
    /// satisfy it perfectly while proving nothing. Each marker has to be found somewhere for the
    /// "nowhere together" claim to mean anything.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AnonymousAndProvisioningMarkers_AreCarriedByDisjointRouteGroups()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        string[] anonymousPatterns = PatternsCarrying<IAllowAnonymous>(endpoints);
        string[] markedPatterns = PatternsCarrying<ProvisionsUserAttribute>(endpoints);
        string[] carryingBoth = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
                && endpoint.Metadata.GetMetadata<ProvisionsUserAttribute>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — joined rather than compared as a collection so a failure names the offending route
        // instead of only reporting that a set was not empty.
        await Assert.That(string.Join(", ", carryingBoth)).IsEqualTo(string.Empty);

        // The controls. Both markers exist on this route table; the claim above is that they never meet.
        await Assert.That(anonymousPatterns.Length).IsGreaterThan(0);
        await Assert.That(markedPatterns.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// The distinct route patterns whose endpoint metadata carries <typeparamref name="TMarker" />.
    /// </summary>
    private static string[] PatternsCarrying<TMarker>(IEnumerable<RouteEndpoint> endpoints)
        where TMarker : class =>
        endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<TMarker>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The group a route pattern belongs to — its first two segments, which is exactly the prefix each
    /// <c>MapGroup</c> declares. Comparing whole patterns instead would make this test move every time
    /// an endpoint is added to a group that is already allowed to provision, which is not a change this
    /// test is about.
    /// </summary>
    private static string GroupPrefixOf(string pattern)
    {
        string[] segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return $"/{string.Join('/', segments.Take(2))}";
    }
}
