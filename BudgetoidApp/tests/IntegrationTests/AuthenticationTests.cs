using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace IntegrationTests;

public sealed class AuthenticationTests
{
    [Test]
    public async Task UnauthenticatedRequest_Returns401ProblemJson()
    {
        await using PostgresTestHost host = await StartHostAsync();

        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync("/api/transactions");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
    }

    [Test]
    public async Task Health_AllowsAnonymous()
    {
        await using PostgresTestHost host = await StartHostAsync();

        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync("/health");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Health_AllowsAnonymousInProductionForContainerAppProbes()
    {
        await using ApiFactory factory = CreateFactoryWithoutDatabaseMigration();

        HttpResponseMessage response = await factory.CreateClient().GetAsync("/health");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task AuthenticatedRequest_ProvisionsUserAndAllowsTransactionRequests()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-auth", "auth@example.com", "Auth User");

        HttpResponseMessage created = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 10m,
            date = "2026-06-12",
            description = "Authenticated",
        });
        JsonNode? list = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(list!["items"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task AuthenticatedRequest_MissingRequiredEmailClaim_Returns401ProblemJson()
    {
        await using ApiFactory factory = CreateFactoryWithoutDatabaseMigration();
        HttpClient client = factory.CreateAuthenticatedClientWithoutEmail("google-auth");

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 10m,
            date = "2026-06-12",
            description = "Authenticated",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
    }

    private static ApiFactory CreateFactoryWithoutDatabaseMigration() =>
        new(
            "Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres",
            environment: "Production");

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
