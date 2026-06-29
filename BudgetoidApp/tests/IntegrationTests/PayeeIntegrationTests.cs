using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace IntegrationTests;

public sealed class PayeeIntegrationTests
{
    [Test]
    public async Task PayeesTable_HasTimestampWithTimeZoneAndCaseInsensitiveUniqueIndex()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        await using NpgsqlCommand typeCommand = new(
            """
            select data_type
            from information_schema.columns
            where table_name = 'payees' and column_name = 'created_at_utc'
            """, connection);
        string? type = (string?)await typeCommand.ExecuteScalarAsync();

        await using NpgsqlCommand collationCommand = new(
            """
            select collation_name
            from information_schema.columns
            where table_name = 'payees' and column_name = 'name'
            """, connection);
        string? collation = (string?)await collationCommand.ExecuteScalarAsync();

        await using NpgsqlCommand indexCommand = new(
            """
            select indexdef
            from pg_indexes
            where tablename = 'payees' and indexname = 'IX_payees_user_id_name'
            """, connection);
        string? indexDef = (string?)await indexCommand.ExecuteScalarAsync();

        // Assert
        await Assert.That(type).IsEqualTo("timestamp with time zone");
        await Assert.That(collation).IsEqualTo("case_insensitive");
        await Assert.That(indexDef).Contains("UNIQUE INDEX");
        await Assert.That(indexDef).Contains("name");
    }

    [Test]
    public async Task GetPayees_WhenEmpty_ReturnsEmptyItemsArray()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();

        // Act
        JsonNode? json = await JsonNode.ParseAsync(
            await host.Factory.CreateAuthenticatedClient().GetStreamAsync("/api/payees"));

        // Assert
        await Assert.That(json!["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task PostTransaction_WithNewPayee_CreatesPayeeAndReturnsPayeeFields()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        // Act
        Guid accountId = await CreateAccountAsync(client);
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-24",
            accountId,
            description = "Coffee",
            payeeName = "  Starbucks  ",
        });
        JsonNode? created = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonNode? payees = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/payees"));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created!["payeeId"] is not null).IsTrue();
        await Assert.That(created["payeeName"]!.GetValue<string>()).IsEqualTo("Starbucks");
        await Assert.That(payees!["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>()).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task PostTransaction_WithExistingPayeeDifferentCase_ReusesPayee()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        // Act
        JsonNode first = await PostTransactionAsync(client, "Starbucks");
        JsonNode second = await PostTransactionAsync(client, "starbucks");
        JsonNode? payees = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/payees"));

        // Assert
        await Assert.That(second["payeeId"]!.GetValue<Guid>()).IsEqualTo(first["payeeId"]!.GetValue<Guid>());
        await Assert.That(second["payeeName"]!.GetValue<string>()).IsEqualTo("Starbucks");
        await Assert.That(payees!["items"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetPayees_ReturnsPayeesOrderedByName()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await PostTransactionAsync(client, "Zoo");
        await PostTransactionAsync(client, "Apple");
        await PostTransactionAsync(client, "Mango");

        // Act
        JsonNode? payees = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/payees"));
        string[] names = payees!["items"]!.AsArray()
            .Select(node => node!["name"]!.GetValue<string>())
            .ToArray();

        // Assert
        await Assert.That(names[0]).IsEqualTo("Apple");
        await Assert.That(names[1]).IsEqualTo("Mango");
        await Assert.That(names[2]).IsEqualTo("Zoo");
    }

    [Test]
    public async Task DeletingPayee_NullsTransactionPayeeId()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        JsonNode created = await PostTransactionAsync(client, "Starbucks");
        Guid payeeId = created["payeeId"]!.GetValue<Guid>();

        // Act
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand delete = new("delete from payees where id = @id", connection);
        delete.Parameters.AddWithValue("id", payeeId);
        await delete.ExecuteNonQueryAsync();

        JsonNode? list = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));
        JsonNode item = list!["items"]!.AsArray()[0]!;

        // Assert
        await Assert.That(list["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(item["payeeId"]).IsNull();
        await Assert.That(item["payeeName"]).IsNull();
    }

    [Test]
    public async Task GetTransactions_ReturnsResolvedPayeeName()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        JsonNode created = await PostTransactionAsync(client, "Starbucks");

        // Act
        JsonNode? list = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));
        JsonNode item = list!["items"]!.AsArray()[0]!;

        // Assert
        await Assert.That(item["payeeId"]!.GetValue<Guid>()).IsEqualTo(created["payeeId"]!.GetValue<Guid>());
        await Assert.That(item["payeeName"]!.GetValue<string>()).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task ConcurrentTransactions_WithSameNewPayeeName_CreateOnePayee()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        // Act
        JsonNode[] created = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => PostTransactionAsync(client, "Starbucks")));
        JsonNode? payees = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/payees"));
        Guid firstPayeeId = created[0]["payeeId"]!.GetValue<Guid>();

        // Assert
        await Assert.That(created.All(node => node["payeeId"]!.GetValue<Guid>() == firstPayeeId)).IsTrue();
        await Assert.That(payees!["items"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Payees_AreIsolatedPerUser()
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");

        // Act
        await PostTransactionAsync(factoryA.CreateAuthenticatedClient(), "Starbucks");
        JsonNode? payeesB = await JsonNode.ParseAsync(
            await factoryB.CreateAuthenticatedClient().GetStreamAsync("/api/payees"));
        JsonNode? transactionsB = await JsonNode.ParseAsync(
            await factoryB.CreateAuthenticatedClient().GetStreamAsync("/api/transactions"));

        // Assert
        await Assert.That(payeesB!["items"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(transactionsB!["items"]!.AsArray().Count).IsEqualTo(0);
    }

    private static async Task<JsonNode> PostTransactionAsync(HttpClient client, string payeeName)
    {
        Guid accountId = await CreateAccountAsync(client);
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-24",
            accountId,
            description = "Coffee",
            payeeName,
        });
        response.EnsureSuccessStatusCode();
        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = $"Checking {Guid.CreateVersion7()}",
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }

    private static async Task<RepositoryTestHost> StartRepositoryHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
