using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace IntegrationTests;

public sealed class AccountIntegrationTests
{
    [Test]
    public async Task AccountsCrud_WorksThroughApiAndUsesStringEnum()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        HttpResponseMessage create = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = "  Checking  ",
            type = "Checking",
            openingBalance = 100m,
            currencyCode = "USD",
        });
        JsonNode created = (await JsonNode.ParseAsync(await create.Content.ReadAsStreamAsync()))!;
        Guid id = created["id"]!.GetValue<Guid>();

        JsonNode listAfterCreate = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;
        HttpResponseMessage update = await client.PutAsJsonAsync($"/api/accounts/{id}", new
        {
            name = "Savings",
            type = "Savings",
            openingBalance = 25m,
        });
        JsonNode listAfterUpdate = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;
        HttpResponseMessage delete = await client.DeleteAsync($"/api/accounts/{id}");
        JsonNode listAfterDelete = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;

        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created["name"]!.GetValue<string>()).IsEqualTo("Checking");
        await Assert.That(created["type"]!.GetValue<string>()).IsEqualTo("Checking");
        await Assert.That(created["openingBalance"]!.GetValue<decimal>()).IsEqualTo(100m);
        await Assert.That(created["currencyCode"]!.GetValue<string>()).IsEqualTo("USD");
        await Assert.That(created["currencyName"]!.GetValue<string>()).IsEqualTo("US Dollar");
        await Assert.That(created["currencySymbol"]!.GetValue<string>()).IsEqualTo("$");
        await Assert.That(listAfterCreate["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(update.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(listAfterUpdate["items"]!.AsArray()[0]!["name"]!.GetValue<string>()).IsEqualTo("Savings");
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(listAfterDelete["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task UserCannotCreateTransactionWithAnotherUsersAccountId()
    {
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        HttpClient clientA = factoryA.CreateAuthenticatedClient();
        HttpClient clientB = factoryB.CreateAuthenticatedClient();
        Guid accountA = await CreateAccountAsync(clientA, "Checking A");

        HttpResponseMessage response = await clientB.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId = accountA,
            description = "Should fail",
        });
        JsonNode? problem = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["AccountId"] is not null).IsTrue();
    }

    [Test]
    public async Task DeleteAccount_WithTransactions_IsRejected()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client, "Checking");
        await CreateTransactionAsync(client, accountId);

        HttpResponseMessage delete = await client.DeleteAsync($"/api/accounts/{accountId}");
        JsonNode? problem = await JsonNode.ParseAsync(await delete.Content.ReadAsStreamAsync());
        JsonNode list = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;

        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["Id"] is not null).IsTrue();
        await Assert.That(list["items"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task CreateAccount_WithDuplicateName_IsRejected()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await CreateAccountAsync(client, "Checking");

        // Different case: the case_insensitive collation makes "checking" collide with "Checking".
        HttpResponseMessage duplicate = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = "checking",
            type = "Savings",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        JsonNode? problem = await JsonNode.ParseAsync(await duplicate.Content.ReadAsStreamAsync());

        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["Name"] is not null).IsTrue();
    }

    [Test]
    public async Task RenameAccount_ToExistingName_IsRejected()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await CreateAccountAsync(client, "Checking");
        Guid savingsId = await CreateAccountAsync(client, "Savings");

        HttpResponseMessage rename = await client.PutAsJsonAsync($"/api/accounts/{savingsId}", new
        {
            name = "Checking",
            type = "Savings",
            openingBalance = 0m,
        });
        JsonNode? problem = await JsonNode.ParseAsync(await rename.Content.ReadAsStreamAsync());

        await Assert.That(rename.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["Name"] is not null).IsTrue();
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            name,
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static async Task CreateTransactionAsync(HttpClient client, Guid accountId)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = "Coffee",
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
