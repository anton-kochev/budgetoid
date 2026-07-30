using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace IntegrationTests;

/// <summary>
/// Covers the API-wide contract around single-resource reads: every <c>201 Created</c> promises a
/// <c>Location</c>, and a promise the client cannot follow is a broken one. Nothing else in the
/// suite notices a dangling <c>Location</c> — the creation tests assert the header's shape, the list
/// tests never ask for one resource, so a resource can ship with a header pointing at an unmapped
/// route and stay green everywhere. The cross-budget tests here guard the other half of the same
/// route: an id that belongs to another budget must read as absent, not as forbidden.
/// </summary>
public sealed class ResourceByIdIntegrationTests
{
    [Test]
    public async Task CreatedResources_AreRetrievableAtLocationHeader()
    {
        // Arrange — one of each resource whose creation returns a Location. There are exactly four.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        HttpResponseMessage createGroup = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryGroupId = await ReadIdAsync(createGroup);
        HttpResponseMessage createCategory =
            await CreateCategoryAsync(client, categoryGroupId, "Groceries");
        Guid categoryId = await ReadIdAsync(createCategory);
        HttpResponseMessage createAccount = await CreateAccountAsync(client, "Checking");
        Guid accountId = await ReadIdAsync(createAccount);
        HttpResponseMessage createTransaction = await CreateTransactionAsync(client, accountId);
        Guid transactionId = await ReadIdAsync(createTransaction);

        // Act — follow each header verbatim instead of rebuilding the URL from the id. Rebuilding it
        // would test this test's idea of the route; the header is what the client is handed and the
        // only thing whose resolvability is actually in question.
        HttpResponseMessage getGroup = await client.GetAsync(createGroup.Headers.Location);
        HttpResponseMessage getCategory = await client.GetAsync(createCategory.Headers.Location);
        HttpResponseMessage getAccount = await client.GetAsync(createAccount.Headers.Location);
        HttpResponseMessage getTransaction =
            await client.GetAsync(createTransaction.Headers.Location);
        HttpResponseMessage unknownGroup = await client.GetAsync(
            $"/api/category-groups/{Guid.CreateVersion7()}");
        HttpResponseMessage unknownCategory = await client.GetAsync(
            $"/api/categories/{Guid.CreateVersion7()}");
        HttpResponseMessage unknownAccount = await client.GetAsync(
            $"/api/accounts/{Guid.CreateVersion7()}");
        HttpResponseMessage unknownTransaction = await client.GetAsync(
            $"/api/transactions/{Guid.CreateVersion7()}");

        // Assert — the status alone would pass against a route that answers 200 with the wrong row,
        // so each body is checked for the fields that identify the resource just created.
        await Assert.That(getGroup.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonNode group = await ReadJsonAsync(getGroup);
        await Assert.That(group["id"]!.GetValue<Guid>()).IsEqualTo(categoryGroupId);
        await Assert.That(group["name"]!.GetValue<string>()).IsEqualTo("Essentials");

        await Assert.That(getCategory.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonNode category = await ReadJsonAsync(getCategory);
        await Assert.That(category["id"]!.GetValue<Guid>()).IsEqualTo(categoryId);
        await Assert.That(category["name"]!.GetValue<string>()).IsEqualTo("Groceries");
        await Assert.That(category["categoryGroupId"]!.GetValue<Guid>()).IsEqualTo(categoryGroupId);
        await Assert.That(category["categoryGroupName"]!.GetValue<string>()).IsEqualTo("Essentials");

        await Assert.That(getAccount.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonNode account = await ReadJsonAsync(getAccount);
        await Assert.That(account["id"]!.GetValue<Guid>()).IsEqualTo(accountId);
        await Assert.That(account["name"]!.GetValue<string>()).IsEqualTo("Checking");
        await Assert.That(account["currencyCode"]!.GetValue<string>()).IsEqualTo("USD");
        await Assert.That(account["currencyName"]!.GetValue<string>()).IsEqualTo("US Dollar");
        await Assert.That(account["currencySymbol"]!.GetValue<string>()).IsEqualTo("$");

        await Assert.That(getTransaction.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonNode transaction = await ReadJsonAsync(getTransaction);
        await Assert.That(transaction["id"]!.GetValue<Guid>()).IsEqualTo(transactionId);
        await Assert.That(transaction["amount"]!.GetValue<decimal>()).IsEqualTo(-42.50m);
        await Assert.That(transaction["date"]!.GetValue<string>()).IsEqualTo("2026-06-12");
        await Assert.That(transaction["accountId"]!.GetValue<Guid>()).IsEqualTo(accountId);
        await Assert.That(transaction["accountName"]!.GetValue<string>()).IsEqualTo("Checking");

        await Assert.That(unknownGroup.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownCategory.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownAccount.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownTransaction.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AccountById_IsHiddenFromAnotherBudgetButVisibleToItsOwner()
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        HttpClient clientA = factoryA.CreateAuthenticatedClient();
        HttpClient clientB = factoryB.CreateAuthenticatedClient();
        HttpResponseMessage create = await CreateAccountAsync(clientA, "Checking A");
        Guid accountId = await ReadIdAsync(create);

        // Act
        HttpResponseMessage owner = await clientA.GetAsync($"/api/accounts/{accountId}");
        HttpResponseMessage stranger = await clientB.GetAsync($"/api/accounts/{accountId}");

        // Assert — a resource in another budget must be indistinguishable from one that does not
        // exist, so this is 404 and there is deliberately no 403 path to add.
        await Assert.That(stranger.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Do not remove this as redundant with CreatedResources_AreRetrievableAtLocationHeader. An
        // unmapped route answers 404 as well, so the assertion above on its own would hold for a
        // route that does not exist at all, and would keep holding if GET /api/accounts/{id} were
        // later deleted. Pairing it with a 200 for the very same id is what makes the 404 mean "the
        // budget query filter hid it" rather than "there is no such route".
        await Assert.That(owner.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task TransactionById_IsHiddenFromAnotherBudgetButVisibleToItsOwner()
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        HttpClient clientA = factoryA.CreateAuthenticatedClient();
        HttpClient clientB = factoryB.CreateAuthenticatedClient();
        Guid accountId = await ReadIdAsync(await CreateAccountAsync(clientA, "Checking A"));
        HttpResponseMessage create = await CreateTransactionAsync(clientA, accountId);
        Guid transactionId = await ReadIdAsync(create);

        // Act
        HttpResponseMessage owner = await clientA.GetAsync($"/api/transactions/{transactionId}");
        HttpResponseMessage stranger = await clientB.GetAsync($"/api/transactions/{transactionId}");

        // Assert — same rule as accounts: another budget's transaction reads as absent, not denied.
        await Assert.That(stranger.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The 200 is load-bearing for the 404 above, not a duplicate of the Location test. Without
        // it, a missing GET /api/transactions/{id} would satisfy the cross-budget assertion for
        // entirely the wrong reason. See the sibling account test for the full argument.
        await Assert.That(owner.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> CreateCategoryGroupAsync(
        HttpClient client,
        string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/category-groups", new
        {
            name,
            description = (string?)null,
        });
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<HttpResponseMessage> CreateCategoryAsync(
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
        return response;
    }

    private static async Task<HttpResponseMessage> CreateAccountAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            name,
            type = "Checking",
            openingBalance = 100m,
            currencyCode = "USD",
        });
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<HttpResponseMessage> CreateTransactionAsync(
        HttpClient client,
        Guid accountId)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -42.50m,
            date = "2026-06-12",
            accountId,
            description = "Groceries",
        });
        response.EnsureSuccessStatusCode();
        return response;
    }

    // The creation helpers hand back the whole response because the Location header is the subject
    // here, not just the id. Each response body is read exactly once — the content stream is not
    // rewound between reads.
    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response))["id"]!.GetValue<Guid>();

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
