using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

public sealed class CategoryRouteConstructionTests
{
    [Test]
    public async Task Api_MapsCleanCategoryRoutesAndNoLegacyGroupsRoute()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        string[] routes = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        // Assert
        await Assert.That(routes).Contains("/api/category-groups/");
        await Assert.That(routes).Contains("/api/category-groups/{id:guid}/position");
        await Assert.That(routes).Contains("/api/categories/");
        await Assert.That(routes).Contains("/api/categories/{id:guid}/placement");
        await Assert.That(routes.Any(route => route.StartsWith("/api/groups"))).IsFalse();
    }
}
