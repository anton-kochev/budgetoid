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

    /// <summary>
    /// That a configured origin's response says credentials may travel with it.
    /// </summary>
    /// <remarks>
    /// A browser drops a cross-origin response that carries a cookie unless this header says
    /// <c>true</c>, and it drops it silently — the request succeeded, the server wrote the
    /// <c>Set-Cookie</c>, and the cookie jar is simply empty afterwards. So a session cookie issued to
    /// a frontend on another origin is a sign-in that appears to work and then does not, with nothing
    /// in either log naming the cause. The allow-list itself is unchanged and stays the control:
    /// credentials may travel to the origins already argued for and to no others, which is what
    /// <see cref="UnconfiguredOrigin_IsNotAllowed" /> holds.
    /// </remarks>
    [Test]
    public async Task ConfiguredOrigin_MayCarryCredentials()
    {
        // Arrange
        await using ApiFactory factory = CreateFactoryWithAllowedOrigin(ConfiguredOrigin);
        HttpRequestMessage request = new(HttpMethod.Get, HealthPath);
        request.Headers.Add("Origin", ConfiguredOrigin);

        // Act
        HttpResponseMessage response = await factory.CreateClient().SendAsync(request);

        // Assert — the allowed origin too, because the credentials header means nothing beside a
        // response the browser was going to reject anyway.
        await Assert.That(AllowOrigin(response)).IsEqualTo(ConfiguredOrigin);
        await Assert.That(AllowCredentials(response)).IsEqualTo("true");
    }

    private static string? AllowOrigin(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    private static string? AllowCredentials(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Credentials", out IEnumerable<string>? values)
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
