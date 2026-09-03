using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TestSupport;

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
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();
        // "Sinking Funds" AND NOT "Essentials", and the label is chosen rather than picked. Two members
        // below are assertions about an ENCODED response — group.name and category.categoryGroupName —
        // and Name("Essentials") is 39 bytes that spell identically under padded standard base64 and
        // unpadded base64url, so both would have been green whichever encoder the read services reached
        // for. SealedNarrative.EncodedName carries the measurement and the list of the four such labels
        // in this suite. This one's 42-byte envelope carries a 62/63 byte, so the two alphabets differ.
        HttpResponseMessage createGroup = await CreateCategoryGroupAsync(client, "Sinking Funds");
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
        // The ENVELOPE, not the word, on the account's terms one table over: category_groups.name is
        // sealed, so "Sinking Funds" is in no payload this API can produce. The check does two jobs now
        // — the envelope is deterministic in its label, so it says the row that came back is the row
        // this test created, AND the label is one the two base64 alphabets disagree about, so it also
        // says CategoryGroupReadService encoded it as base64url. Dropping it would let a 200 carrying
        // somebody else's group pass.
        await Assert.That(group["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Sinking Funds"));

        await Assert.That(getCategory.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonNode category = await ReadJsonAsync(getCategory);
        await Assert.That(category["id"]!.GetValue<Guid>()).IsEqualTo(categoryId);
        await Assert.That(category["categoryGroupId"]!.GetValue<Guid>()).IsEqualTo(categoryGroupId);

        // ONE RECORD, TWO KINDS OF NAME, AND THE PAIR IS THE POINT. CategoryDto carries the category's
        // own name as text and the GROUP's name as an envelope, because categories.name is not sealed
        // yet and category_groups.name is. That mixed state lasts one slice; until it ends, these two
        // lines sitting beside each other are what stops somebody "correcting" either into the other's
        // shape. The envelope member is bound to the GROUP's row id, which is already on the wire above.
        //
        // This line is also the ONE place CategoryReadService's encoding of categoryGroupName is
        // observable outside CategoryIntegrationTests, which is why the label had to stop being blind:
        // the two sites are a different projection over a different query and either could re-encode
        // with the other still green.
        await Assert.That(category["name"]!.GetValue<string>()).IsEqualTo("Groceries");
        await Assert.That(category["categoryGroupName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Sinking Funds"));

        await Assert.That(getAccount.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonNode account = await ReadJsonAsync(getAccount);
        await Assert.That(account["id"]!.GetValue<Guid>()).IsEqualTo(accountId);
        // The ENVELOPE, not the word, and the assertion still does its job. This case is about a
        // Location header pointing at a route that really answers, so what it needs from the body is
        // that the row it got back is the row it created; the envelope is deterministic in its label,
        // so it says exactly that. accounts.name is sealed, so the word "Checking" is in no payload
        // this API can produce — and dropping the check instead would leave a 200 from a route that
        // answered with somebody else's account passing.
        await Assert.That(account["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Checking"));
        await Assert.That(account["currencyCode"]!.GetValue<string>()).IsEqualTo("USD");
        await Assert.That(account["currencyName"]!.GetValue<string>()).IsEqualTo("US Dollar");
        await Assert.That(account["currencySymbol"]!.GetValue<string>()).IsEqualTo("$");

        await Assert.That(getTransaction.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonNode transaction = await ReadJsonAsync(getTransaction);
        await Assert.That(transaction["id"]!.GetValue<Guid>()).IsEqualTo(transactionId);
        await Assert.That(transaction["amount"]!.GetValue<decimal>()).IsEqualTo(-42.50m);
        await Assert.That(transaction["date"]!.GetValue<string>()).IsEqualTo("2026-06-12");
        await Assert.That(transaction["accountId"]!.GetValue<Guid>()).IsEqualTo(accountId);
        await Assert.That(transaction["accountName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Checking"));

        await Assert.That(unknownGroup.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownCategory.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownAccount.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownTransaction.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AccountById_IsHiddenFromAnotherBudgetButVisibleToItsOwner()
    {
        // Arrange
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        (HttpClient clientA, _, _) = await factoryA.CreateSignedInClientAsync();
        (HttpClient clientB, _, _) = await factoryB.CreateSignedInClientAsync();
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
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        (HttpClient clientA, _, _) = await factoryA.CreateSignedInClientAsync();
        (HttpClient clientB, _, _) = await factoryB.CreateSignedInClientAsync();
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

    /// <summary>
    /// Creates one category group from <paramref name="label" /> and returns the whole response, whose
    /// <c>Location</c> header is what the caller is here to read.
    /// </summary>
    /// <remarks>
    /// <b>The parameter is a LABEL, not a name</b> — category_groups.name is an AEAD envelope and
    /// category_groups.name_key a blind index, so a flat string is a 400 and the Location header this
    /// helper exists to produce would never be written. The response, not the id, is returned because
    /// the caller's subject is the header; the body's id and the minted id agree, and asserting that
    /// belongs to the create route's own cases rather than to this seeding.
    /// </remarks>
    private static async Task<HttpResponseMessage> CreateCategoryGroupAsync(
        HttpClient client,
        string label)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(label),
            nameKey = SealedNarrative.EncodedIndex(label),
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

    /// <summary>
    /// Creates one account, taking <paramref name="label" /> as the text both halves of the name are
    /// built from rather than as a value any column holds.
    /// </summary>
    /// <remarks>
    /// The parameter kept its job and changed its meaning, which is why it was renamed: callers pass it
    /// to keep two seeded accounts apart, and it still does that, because SealedNarrative is
    /// deterministic in its label. What no longer happens is the words reaching the database — the
    /// envelope and the blind index are what travel, and a flat name is a 400 before any case here
    /// begins.
    /// </remarks>
    private static async Task<HttpResponseMessage> CreateAccountAsync(HttpClient client, string label)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(label),
            nameKey = SealedNarrative.EncodedIndex(label),
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

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every request
    /// in this file authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
