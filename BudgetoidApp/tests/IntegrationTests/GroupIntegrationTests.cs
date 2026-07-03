using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace IntegrationTests;

public sealed class GroupIntegrationTests
{
    [Test]
    public async Task GroupsTable_HasTimestampWithTimeZoneCaseInsensitiveUniqueIndexAndDescriptionColumn()
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
            where table_name = 'groups' and column_name = 'created_at_utc'
            """, connection);
        string? type = (string?)await typeCommand.ExecuteScalarAsync();

        await using NpgsqlCommand collationCommand = new(
            """
            select collation_name
            from information_schema.columns
            where table_name = 'groups' and column_name = 'name'
            """, connection);
        string? collation = (string?)await collationCommand.ExecuteScalarAsync();

        await using NpgsqlCommand descriptionCommand = new(
            """
            select is_nullable
            from information_schema.columns
            where table_name = 'groups' and column_name = 'description'
            """, connection);
        string? descriptionNullable = (string?)await descriptionCommand.ExecuteScalarAsync();

        await using NpgsqlCommand indexCommand = new(
            """
            select indexdef
            from pg_indexes
            where tablename = 'groups' and indexname = 'IX_groups_user_id_name'
            """, connection);
        string? indexDef = (string?)await indexCommand.ExecuteScalarAsync();

        // Assert
        await Assert.That(type).IsEqualTo("timestamp with time zone");
        await Assert.That(collation).IsEqualTo("case_insensitive");
        await Assert.That(descriptionNullable).IsEqualTo("YES");
        await Assert.That(indexDef).Contains("UNIQUE INDEX");
        await Assert.That(indexDef).Contains("name");
    }

    [Test]
    public async Task GroupsCrud_WorksThroughApi()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        // Act
        HttpResponseMessage create = await client.PostAsJsonAsync("/api/groups", new
        {
            name = "  Food  ",
            description = "  Weekly food  ",
        });
        JsonNode created = (await JsonNode.ParseAsync(await create.Content.ReadAsStreamAsync()))!;
        Guid id = created["id"]!.GetValue<Guid>();

        JsonNode listAfterCreate = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/groups")))!;
        HttpResponseMessage update = await client.PutAsJsonAsync($"/api/groups/{id}", new
        {
            name = "Groceries",
            description = "Monthly",
        });
        JsonNode listAfterUpdate = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/groups")))!;
        HttpResponseMessage delete = await client.DeleteAsync($"/api/groups/{id}");
        JsonNode listAfterDelete = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/groups")))!;

        // Assert
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created["name"]!.GetValue<string>()).IsEqualTo("Food");
        await Assert.That(created["description"]!.GetValue<string>()).IsEqualTo("Weekly food");
        await Assert.That(listAfterCreate["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(update.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(listAfterUpdate["items"]!.AsArray()[0]!["name"]!.GetValue<string>()).IsEqualTo("Groceries");
        await Assert.That(listAfterUpdate["items"]!.AsArray()[0]!["description"]!.GetValue<string>()).IsEqualTo("Monthly");
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(listAfterDelete["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task CreateGroup_WithBlankDescription_ReturnsNullDescription()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        // Act
        HttpResponseMessage create = await client.PostAsJsonAsync("/api/groups", new
        {
            name = "Food",
            description = "   ",
        });
        JsonNode created = (await JsonNode.ParseAsync(await create.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created["description"]).IsNull();
    }

    [Test]
    public async Task CreateGroup_WithDuplicateName_IsRejected()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await CreateGroupAsync(client, "Groceries");

        // Act — different case: the case_insensitive collation makes "groceries" collide with "Groceries".
        HttpResponseMessage duplicate = await client.PostAsJsonAsync("/api/groups", new
        {
            name = "groceries",
            description = (string?)null,
        });
        JsonNode? problem = await JsonNode.ParseAsync(await duplicate.Content.ReadAsStreamAsync());

        // Assert
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["Name"] is not null).IsTrue();
    }

    [Test]
    public async Task RenameGroup_ToExistingName_IsRejected()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await CreateGroupAsync(client, "Groceries");
        Guid travelId = await CreateGroupAsync(client, "Travel");

        // Act
        HttpResponseMessage rename = await client.PutAsJsonAsync($"/api/groups/{travelId}", new
        {
            name = "Groceries",
            description = (string?)null,
        });
        JsonNode? problem = await JsonNode.ParseAsync(await rename.Content.ReadAsStreamAsync());

        // Assert
        await Assert.That(rename.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["Name"] is not null).IsTrue();
    }

    [Test]
    public async Task Groups_AreIsolatedPerUser()
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");

        // Act
        await CreateGroupAsync(factoryA.CreateAuthenticatedClient(), "Groceries");
        JsonNode? groupsB = await JsonNode.ParseAsync(
            await factoryB.CreateAuthenticatedClient().GetStreamAsync("/api/groups"));

        // Assert
        await Assert.That(groupsB!["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task PostTransaction_WithGroupId_LinksAndReturnsGroupFields()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid groupId = await CreateGroupAsync(client, "Groceries");

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-07-03",
            accountId,
            description = "Coffee",
            groupId,
        });
        JsonNode created = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        JsonNode? list = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));
        JsonNode item = list!["items"]!.AsArray()[0]!;

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created["groupId"]!.GetValue<Guid>()).IsEqualTo(groupId);
        await Assert.That(created["groupName"]!.GetValue<string>()).IsEqualTo("Groceries");
        await Assert.That(item["groupId"]!.GetValue<Guid>()).IsEqualTo(groupId);
        await Assert.That(item["groupName"]!.GetValue<string>()).IsEqualTo("Groceries");
    }

    [Test]
    public async Task PostTransaction_WithAnotherUsersGroupId_IsRejected()
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        HttpClient clientA = factoryA.CreateAuthenticatedClient();
        HttpClient clientB = factoryB.CreateAuthenticatedClient();
        Guid groupA = await CreateGroupAsync(clientA, "Groceries");
        Guid accountB = await CreateAccountAsync(clientB);

        // Act
        HttpResponseMessage response = await clientB.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-07-03",
            accountId = accountB,
            description = "Should fail",
            groupId = groupA,
        });
        JsonNode? problem = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["GroupId"] is not null).IsTrue();
    }

    [Test]
    public async Task DeleteGroup_WithLinkedTransaction_IsRejected()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid groupId = await CreateGroupAsync(client, "Groceries");
        HttpResponseMessage transaction = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-07-03",
            accountId,
            description = "Coffee",
            groupId,
        });
        transaction.EnsureSuccessStatusCode();

        // Act
        HttpResponseMessage delete = await client.DeleteAsync($"/api/groups/{groupId}");
        JsonNode? problem = await JsonNode.ParseAsync(await delete.Content.ReadAsStreamAsync());
        JsonNode list = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/groups")))!;

        // Assert
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["Id"] is not null).IsTrue();
        await Assert.That(list["items"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task UpdateGroup_ForAnotherUsersGroup_ReturnsNotFound()
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        Guid groupA = await CreateGroupAsync(factoryA.CreateAuthenticatedClient(), "Groceries");

        // Act
        HttpResponseMessage response = await factoryB.CreateAuthenticatedClient()
            .PutAsJsonAsync($"/api/groups/{groupA}", new { name = "Hacked", description = (string?)null });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteGroup_ForAnotherUsersGroup_ReturnsNotFound()
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        Guid groupA = await CreateGroupAsync(factoryA.CreateAuthenticatedClient(), "Groceries");

        // Act
        HttpResponseMessage response = await factoryB.CreateAuthenticatedClient()
            .DeleteAsync($"/api/groups/{groupA}");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task<Guid> CreateGroupAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/groups", new
        {
            name,
            description = (string?)null,
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
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
