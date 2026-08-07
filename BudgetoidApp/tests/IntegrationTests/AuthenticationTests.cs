using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Npgsql;

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
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-auth", "auth@example.com");

        Guid accountId = await CreateAccountAsync(client);
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 10m,
            date = "2026-06-12",
            accountId,
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

    [Test]
    public async Task AuthenticatedRequest_EmailNotAssertedVerified_Returns401ProblemJson()
    {
        // Arrange
        await using ApiFactory factory = CreateFactoryWithoutDatabaseMigration();
        HttpClient client = factory.CreateAuthenticatedClientWithUnverifiedEmail("google-auth");

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 10m,
            date = "2026-06-12",
            description = "Unverified",
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
    }

    [Test]
    public async Task AuthenticatedRequest_EmailNotAssertedVerified_WritesNoUserRow()
    {
        // Arrange — a real database, because the claim this test makes is about what was written,
        // not about what came back. A 401 with an account already provisioned behind it would pass
        // the status assertion above and still have leaked the address into storage.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(
            "google-auth",
            UnverifiedEmail,
            emailVerified: "false");

        // Act
        await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 10m,
            date = "2026-06-12",
            description = "Unverified",
        });

        // Assert
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await Assert.That(await CountUsersByEmailAsync(connection, UnverifiedEmail)).IsEqualTo(0L);
    }

    [Test]
    public async Task AuthenticatedRequest_MissingEmailVerifiedClaim_Returns401ProblemJson()
    {
        // Arrange
        await using ApiFactory factory = CreateFactoryWithoutDatabaseMigration();
        HttpClient client = factory.CreateAuthenticatedClientWithoutEmailVerifiedClaim("google-auth");

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 10m,
            date = "2026-06-12",
            description = "No claim",
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
    }

    // This test and AuthenticatedRequest_EmailVerifiedClaimInMixedCase_ProvisionsUser are a pair and
    // neither is optional: "1" is truthy to a provider yet not a boolean, so it kills a gate that
    // merely checks the value is not "false"; "True" kills a gate that compares ordinally against
    // "true". Only bool.TryParse satisfies both at once.
    [Test]
    public async Task AuthenticatedRequest_NonBooleanEmailVerifiedClaim_Returns401ProblemJson()
    {
        // Arrange
        await using ApiFactory factory = CreateFactoryWithoutDatabaseMigration();
        HttpClient client = factory.CreateAuthenticatedClient("google-auth", "auth@example.com", emailVerified: "1");

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 10m,
            date = "2026-06-12",
            description = "Not a boolean",
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AuthenticatedRequest_EmailVerifiedClaimInMixedCase_ProvisionsUser()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(
            "google-auth",
            "auth@example.com",
            emailVerified: "True");

        // Act
        Guid accountId = await CreateAccountAsync(client);
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 10m,
            date = "2026-06-12",
            accountId,
            description = "Mixed case",
        });

        // Assert
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task AuthenticatedRequest_EmailNotAssertedVerified_Returns401NamingTheUnverifiedEmail()
    {
        // Arrange
        await using ApiFactory factory = CreateFactoryWithoutDatabaseMigration();
        HttpClient client = factory.CreateAuthenticatedClientWithUnverifiedEmail("google-auth");

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 10m,
            date = "2026-06-12",
            description = "Unverified",
        });
        JsonNode problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        // Assert — a distinct title, not the shared missing-claims one: the two rejections are the
        // same status but different corrective actions for the caller. Pinned through the middleware's
        // own constant, so the sentence cannot be reworded in one place and left behind in the other.
        await Assert.That(problem["title"]!.GetValue<string>())
            .IsEqualTo(UserProvisioningMiddleware.UnverifiedEmailTitle);
    }

    [Test]
    public async Task AuthenticatedRequest_ExistingAccount_EmailNotAssertedVerified_Returns401ProblemJson()
    {
        // Arrange — provision the account first, so the gate is met by a subject the system already
        // knows. Pins the "we already know this user, why re-gate" optimisation: moving the check
        // below the credential lookup leaves every fresh-subject test green.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient provisionedClient = host.Factory.CreateAuthenticatedClient(
            "existing-account",
            "existing@example.com");
        await CreateAccountAsync(provisionedClient);

        // Act
        HttpClient unverifiedClient = host.Factory.CreateAuthenticatedClient(
            "existing-account",
            "existing@example.com",
            emailVerified: "false");
        HttpResponseMessage response = await unverifiedClient.GetAsync("/api/transactions");
        JsonNode problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        await Assert.That(problem["title"]!.GetValue<string>())
            .IsEqualTo(UserProvisioningMiddleware.UnverifiedEmailTitle);
    }

    /// <summary>
    /// Address used by the tests that assert nothing was written. Held as a constant so the value
    /// sent on the request and the value counted in the database cannot drift apart.
    /// </summary>
    private const string UnverifiedEmail = "unverified@example.com";

    /// <summary>
    /// Counts <c>users</c> rows for an address with raw Npgsql. EF's escape hatches
    /// (<c>Find</c>, <c>FromSql*</c>) are banned symbols in this solution, and the count has to be
    /// taken on the container's superuser connection anyway so that row-level security cannot make
    /// an existing row look absent.
    /// </summary>
    private static async Task<long> CountUsersByEmailAsync(NpgsqlConnection connection, string email)
    {
        await using NpgsqlCommand command = new("select count(*) from users where email = @email", connection);
        command.Parameters.AddWithValue("email", email);

        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from 'users', got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = "Checking",
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
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
