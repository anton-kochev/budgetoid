using System.Net.Http.Headers;

namespace IntegrationTests;

public sealed class CorsTests
{
    private const string HealthPath = "/health";
    private const string ConfiguredOrigin = "https://budgetoid.example.com";
    private const string UnconfiguredOrigin = "https://evil.example.com";

    [Test]
    public async Task ConfiguredOrigin_IsAllowed()
    {
        // Arrange
        await using ApiFactory factory = CreateFactoryWithAllowedOrigin(ConfiguredOrigin);
        HttpRequestMessage request = new(HttpMethod.Get, HealthPath);
        request.Headers.Add("Origin", ConfiguredOrigin);

        // Act
        HttpResponseMessage response = await factory.CreateClient().SendAsync(request);

        // Assert
        await Assert.That(AllowOrigin(response)).IsEqualTo(ConfiguredOrigin);
    }

    [Test]
    public async Task UnconfiguredOrigin_IsNotAllowed()
    {
        // Arrange
        await using ApiFactory factory = CreateFactoryWithAllowedOrigin(ConfiguredOrigin);
        HttpRequestMessage request = new(HttpMethod.Get, HealthPath);
        request.Headers.Add("Origin", UnconfiguredOrigin);

        // Act
        HttpResponseMessage response = await factory.CreateClient().SendAsync(request);

        // Assert
        await Assert.That(AllowOrigin(response)).IsNull();
    }

    private static string? AllowOrigin(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    // Production environment skips the dev-only startup migration, so no live database is needed.
    private static ApiFactory CreateFactoryWithAllowedOrigin(string origin) =>
        new(
            "Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres",
            environment: "Production",
            settings: new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = origin,
            });
}
