using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace IntegrationTests;

public sealed class TransactionEndpointsTests
{
    [Test]
    public async Task PostValidTransaction_ReturnsCreatedLocationAndCamelCaseDto()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -42.50m,
            date = "2026-06-12",
            accountId,
            description = "Groceries"
        });
        JsonNode? json = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(response.Headers.Location?.ToString().StartsWith("/api/transactions/")).IsTrue();
        await Assert.That(json!["amount"]!.GetValue<decimal>()).IsEqualTo(-42.50m);
        await Assert.That(json["date"]!.GetValue<string>()).IsEqualTo("2026-06-12");
        await Assert.That(json["accountId"]!.GetValue<Guid>()).IsEqualTo(accountId);
        await Assert.That(json["accountName"]!.GetValue<string>()).IsEqualTo("Checking");
        await Assert.That(json["description"]!.GetValue<string>()).IsEqualTo("Groceries");
        await Assert.That(json["createdAtUtc"] is not null).IsTrue();
    }

    [Test]
    public async Task PostInvalidTransaction_ReturnsValidationProblemDetails()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);

        // The invalid amount has more decimal places than the account's USD allows. A zero amount
        // used to serve here and no longer can: zero is a legitimate ledger entry.
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = 1.234m,
            date = "2026-06-12",
            accountId,
            description = "Groceries"
        });
        JsonNode? json = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        await Assert.That(json!["errors"]!["Amount"] is not null).IsTrue();
    }

    [Test]
    public async Task PostTransaction_WithoutDescription_ReturnsCreatedWithEmptyDescription()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -42.50m,
            date = "2026-06-12",
            accountId,
            description = ""
        });
        JsonNode? json = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(json!["description"]!.GetValue<string>()).IsEqualTo("");
    }

    [Test]
    public async Task GetTransactions_WithoutDescription_ReturnsEmptyDescription()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        await client.PostAsJsonAsync("/api/transactions",
            new { amount = 1m, date = "2026-06-12", accountId, description = "" });

        JsonNode? json = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));

        await Assert.That(json!["items"]!.AsArray()[0]!["description"]!.GetValue<string>()).IsEqualTo("");
    }

    [Test]
    public async Task PostMalformedJson_ReturnsBadRequest()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpResponseMessage response = await host.Factory.CreateAuthenticatedClient().PostAsync(
            "/api/transactions",
            new StringContent("{", Encoding.UTF8, "application/json"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task GetTransactions_ReturnsNewestFirst()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        await client.PostAsJsonAsync("/api/transactions",
            new { amount = 1m, date = "2026-06-11", accountId, description = "Older" });
        await Task.Delay(2);
        await client.PostAsJsonAsync("/api/transactions",
            new { amount = 2m, date = "2026-06-12", accountId, description = "Newest" });

        JsonNode? json = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));

        await Assert.That(json!["items"]!.AsArray()[0]!["description"]!.GetValue<string>()).IsEqualTo("Newest");
        await Assert.That(json["items"]!.AsArray()[1]!["description"]!.GetValue<string>()).IsEqualTo("Older");
    }

    [Test]
    public async Task GetTransactions_WhenEmpty_ReturnsEmptyItemsArray()
    {
        await using PostgresTestHost host = await StartHostAsync();
        JsonNode? json =
            await JsonNode.ParseAsync(await host.Factory.CreateAuthenticatedClient().GetStreamAsync("/api/transactions"));

        await Assert.That(json!["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task PostThenGet_PreservesDecimalDateAndAccountProjection()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        await client.PostAsJsonAsync("/api/transactions",
            new { amount = -42.50m, date = "2026-06-12", accountId, description = "Groceries" });

        JsonNode? json = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));
        JsonNode item = json!["items"]!.AsArray()[0]!;

        await Assert.That(item["amount"]!.GetValue<decimal>()).IsEqualTo(-42.50m);
        await Assert.That(item["date"]!.GetValue<string>()).IsEqualTo("2026-06-12");
        await Assert.That(item["accountId"]!.GetValue<Guid>()).IsEqualTo(accountId);
        await Assert.That(item["accountName"]!.GetValue<string>()).IsEqualTo("Checking");
    }

    [Test]
    public async Task DeleteTransaction_RemovesTheTransactionAndLeavesItsReferencesIntact()
    {
        // Arrange — the transaction names a payee and a category so the delete has something to
        // wrongly cascade into. Without them the test could not tell a scoped delete from a greedy one.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid categoryGroupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, categoryGroupId, "Groceries");
        Guid transactionId = await CreateTransactionAsync(client, accountId, "Starbucks", categoryId);

        // Act
        HttpResponseMessage delete = await client.DeleteAsync($"/api/transactions/{transactionId}");
        JsonNode transactions = await GetJsonAsync(client, "/api/transactions");
        JsonNode accounts = await GetJsonAsync(client, "/api/accounts");
        JsonNode payees = await GetJsonAsync(client, "/api/payees");
        JsonNode categories = await GetJsonAsync(client, "/api/categories");

        // Assert
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(transactions["items"]!.AsArray().Count).IsEqualTo(0);

        // The account, payee and category were merely referenced by the transaction, so deleting it
        // must leave all three standing. Nothing else in the suite deletes a transaction, so no other
        // test would notice if the delete reached upward into them.
        await Assert.That(accounts["items"]!.AsArray()
            .Any(node => node!["id"]!.GetValue<Guid>() == accountId)).IsTrue();
        await Assert.That(payees["items"]!.AsArray()
            .Any(node => node!["name"]!.GetValue<string>() == "Starbucks")).IsTrue();
        await Assert.That(categories["items"]!.AsArray()
            .Any(node => node!["id"]!.GetValue<Guid>() == categoryId)).IsTrue();
    }

    [Test]
    public async Task DeleteTransaction_WithUnknownId_ReturnsNotFound()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        // Act
        HttpResponseMessage delete = await client.DeleteAsync($"/api/transactions/{Guid.CreateVersion7()}");

        // Assert
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteTransaction_FromAnotherBudget_ReturnsNotFoundAndLeavesTheRowInPlace()
    {
        // Arrange — two budgets over one database. The budget query filter is what makes A's
        // transaction invisible to B; there is deliberately no 403 path in this API.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        HttpClient clientA = factoryA.CreateAuthenticatedClient();
        HttpClient clientB = factoryB.CreateAuthenticatedClient();
        Guid accountA = await CreateAccountAsync(clientA);
        Guid transactionA = await CreateTransactionAsync(clientA, accountA);

        // Act
        HttpResponseMessage delete = await clientB.DeleteAsync($"/api/transactions/{transactionA}");
        JsonNode transactionsA = await GetJsonAsync(clientA, "/api/transactions");

        // Assert
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The survival check is not redundant with the 404. A handler that deleted the row first and
        // only then reported it missing would satisfy the status code alone; this assertion is the
        // only thing standing between that bug and a green suite. Do not remove it.
        await Assert.That(transactionsA["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(transactionsA["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>())
            .IsEqualTo(transactionA);
    }

    [Test]
    public async Task DeleteAccount_AfterItsLastTransactionIsDeleted_Succeeds()
    {
        // Arrange — an account pinned by one transaction. DeleteAccountHandler refuses it, and until
        // transactions could be deleted that refusal was a dead end with no way out.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid transactionId = await CreateTransactionAsync(client, accountId);

        // Act
        HttpResponseMessage blockedDelete = await client.DeleteAsync($"/api/accounts/{accountId}");
        HttpResponseMessage deleteTransaction = await client.DeleteAsync($"/api/transactions/{transactionId}");
        HttpResponseMessage allowedDelete = await client.DeleteAsync($"/api/accounts/{accountId}");
        JsonNode accounts = await GetJsonAsync(client, "/api/accounts");

        // Assert — the same request that was refused now succeeds, which is what makes the
        // "Account cannot be deleted because it has transactions." message actionable.
        await Assert.That(blockedDelete.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(deleteTransaction.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(allowedDelete.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(accounts["items"]!.AsArray().Count).IsEqualTo(0);
    }

    private static async Task<Guid> CreateTransactionAsync(
        HttpClient client,
        Guid accountId,
        string? payeeName = null,
        Guid? categoryId = null)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = "Coffee",
            payeeName,
            categoryId,
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static async Task<Guid> CreateCategoryGroupAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/category-groups", new
        {
            name,
            description = (string?)null,
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static async Task<Guid> CreateCategoryAsync(
        HttpClient client,
        Guid categoryGroupId,
        string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/categories", new
        {
            name,
            description = (string?)null,
            categoryGroupId,
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path) =>
        (await JsonNode.ParseAsync(await client.GetStreamAsync(path)))!;

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

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
