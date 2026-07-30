using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

public sealed class BudgetRouteConstructionTests
{
    [Test]
    public async Task Api_HasNoRouteContainingABudgetIdentifier()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act — ASM-004: the ambient budget is resolved from the authenticated principal, never
        // addressed by the client, so a budget must never appear in a path or a route parameter.
        RouteEndpoint[] endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        string[] routesWithABudgetSegment = endpoints
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(pattern => pattern
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.Contains("budget", StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToArray();
        string[] budgetRouteParameters = endpoints
            .SelectMany(endpoint => endpoint.RoutePattern.Parameters)
            .Select(parameter => parameter.Name)
            .Where(name => name.Contains("budget", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();

        // Assert — joined rather than counted so a failure names the offending route.
        await Assert.That(string.Join(", ", routesWithABudgetSegment)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", budgetRouteParameters)).IsEqualTo(string.Empty);
        await Assert.That(endpoints.Length).IsGreaterThan(0);
    }
}
