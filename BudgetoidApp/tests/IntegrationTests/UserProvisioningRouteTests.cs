using Api.Infrastructure;
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
    /// The six route groups that serve a signed-in person's own data, and the only ones that may mint
    /// an account. Written out rather than derived, because a list derived from the route table would
    /// agree with whatever the route table said.
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
