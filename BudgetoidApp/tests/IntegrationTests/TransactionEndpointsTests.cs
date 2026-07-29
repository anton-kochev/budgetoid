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

    [Test]
    public async Task PatchTransaction_WithEveryMutableField_ReplacesAllOfThemAndLeavesCreatedAtUtcAlone()
    {
        // Arrange — a fully populated transaction pointing at a different account, payee and category
        // than the patch will name, so no assertion below can pass because the value happened to
        // match already.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid checkingId = await CreateAccountAsync(client);
        Guid savingsId = await CreateAccountAsync(client, "Savings");
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid groceriesId = await CreateCategoryAsync(client, groupId, "Groceries");
        Guid housingId = await CreateCategoryAsync(client, groupId, "Housing");
        Guid transactionId = await CreateTransactionAsync(client, checkingId, "Starbucks", groceriesId);
        JsonNode before = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync($"/api/transactions/{transactionId}", new
        {
            amount = 99.99m,
            date = "2027-01-31",
            description = "Rent",
            accountId = savingsId,
            payeeName = "Landlord",
            categoryId = housingId,
        });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Assert
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after["amount"]!.GetValue<decimal>()).IsEqualTo(99.99m);
        await Assert.That(after["date"]!.GetValue<string>()).IsEqualTo("2027-01-31");
        await Assert.That(after["description"]!.GetValue<string>()).IsEqualTo("Rent");
        await Assert.That(after["accountId"]!.GetValue<Guid>()).IsEqualTo(savingsId);
        await Assert.That(after["accountName"]!.GetValue<string>()).IsEqualTo("Savings");
        await Assert.That(after["payeeName"]!.GetValue<string>()).IsEqualTo("Landlord");
        await Assert.That(after["categoryId"]!.GetValue<Guid>()).IsEqualTo(housingId);
        await Assert.That(after["categoryName"]!.GetValue<string>()).IsEqualTo("Housing");

        // The id and createdAtUtc are not mutable and are not accepted in the body. createdAtUtc
        // records when the row was written, not when it was last touched, so an edit that moves it
        // would quietly rewrite history — this is the only assertion that would notice.
        await Assert.That(after["id"]!.GetValue<Guid>()).IsEqualTo(transactionId);
        await Assert.That(after["createdAtUtc"]!.GetValue<string>())
            .IsEqualTo(before["createdAtUtc"]!.GetValue<string>());
    }

    [Test]
    public async Task PatchTransaction_WithEmptyBody_ReturnsNoContentAndChangesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, groupId, "Groceries");
        Guid transactionId = await CreateTransactionAsync(client, accountId, "Starbucks", categoryId);
        JsonNode before = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync($"/api/transactions/{transactionId}", new { });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Assert — a patch that names no field is a valid no-op, not a request to blank the row.
        // Comparing the whole serialized DTO catches a field this test did not think to name.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after.ToJsonString()).IsEqualTo(before.ToJsonString());
    }

    [Test]
    public async Task PatchTransaction_WithOnlyAmount_LeavesEveryOtherFieldUntouched()
    {
        // Arrange — this test and PatchTransaction_WithExplicitNulls_ClearsDescriptionPayeeAndCategory
        // are a pair and neither means anything alone. An implementation that reads an absent property
        // as null passes the clearing test and fails this one; an implementation that reads an
        // explicit null as "absent" passes this one and fails that. Only the two together pin down
        // the three-state contract: absent means leave alone, null means clear.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, groupId, "Groceries");
        Guid transactionId = await CreateTransactionAsync(client, accountId, "Starbucks", categoryId);

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { amount = -12.75m });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Assert
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after["amount"]!.GetValue<decimal>()).IsEqualTo(-12.75m);
        await Assert.That(after["description"]!.GetValue<string>()).IsEqualTo("Coffee");
        await Assert.That(after["payeeName"]!.GetValue<string>()).IsEqualTo("Starbucks");
        await Assert.That(after["categoryId"]!.GetValue<Guid>()).IsEqualTo(categoryId);
        await Assert.That(after["date"]!.GetValue<string>()).IsEqualTo("2026-06-26");
        await Assert.That(after["accountId"]!.GetValue<Guid>()).IsEqualTo(accountId);
    }

    [Test]
    public async Task PatchTransaction_WithExplicitNulls_ClearsDescriptionPayeeAndCategory()
    {
        // Arrange — the other half of the pair described on
        // PatchTransaction_WithOnlyAmount_LeavesEveryOtherFieldUntouched. Read them together.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, groupId, "Groceries");
        Guid transactionId = await CreateTransactionAsync(client, accountId, "Starbucks", categoryId);

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync($"/api/transactions/{transactionId}", new
        {
            description = (string?)null,
            payeeName = (string?)null,
            categoryId = (Guid?)null,
        });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Assert — the DTO declares Description as non-nullable, so a cleared description reads back
        // as the empty string. Payee and category are nullable all the way out and read back as null.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after["description"]!.GetValue<string>()).IsEqualTo("");
        await Assert.That(after["payeeId"] is null).IsTrue();
        await Assert.That(after["payeeName"] is null).IsTrue();
        await Assert.That(after["categoryId"] is null).IsTrue();
        await Assert.That(after["categoryName"] is null).IsTrue();
    }

    [Test]
    public async Task PatchTransaction_WithNewPayeeName_CreatesThePayee()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid transactionId = await CreateTransactionAsync(client, accountId);

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { payeeName = "Landlord" });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — the payee must land in the budget's payee list, not just on the transaction row.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after["payeeName"]!.GetValue<string>()).IsEqualTo("Landlord");
        await Assert.That(payees["items"]!.AsArray()
            .Any(node => node!["name"]!.GetValue<string>() == "Landlord")).IsTrue();
    }

    [Test]
    public async Task PatchTransaction_WithExistingPayeeNameInAnotherCase_ReusesThatPayee()
    {
        // Arrange — one transaction already owns the payee "Starbucks"; a second one has none.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        await CreateTransactionAsync(client, accountId, "Starbucks");
        Guid transactionId = await CreateTransactionAsync(client, accountId);

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { payeeName = "STARBUCKS" });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — the count is the assertion that matters. Matching on the name alone would stay
        // green against a handler that minted a second "STARBUCKS" row beside the first.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(after["payeeName"]!.GetValue<string>()).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task PatchTransaction_WithUnknownId_ReturnsNotFound()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/transactions/{Guid.CreateVersion7()}",
            new { amount = 1m });

        // Assert
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task PatchTransaction_FromAnotherBudget_ReturnsNotFoundButSucceedsForItsOwner()
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
        HttpResponseMessage stranger = await clientB.PatchAsJsonAsync(
            $"/api/transactions/{transactionA}",
            new { amount = 999m, description = "Hijacked" });
        JsonNode afterStranger = await GetJsonAsync(clientA, $"/api/transactions/{transactionA}");
        HttpResponseMessage owner = await clientA.PatchAsJsonAsync(
            $"/api/transactions/{transactionA}",
            new { amount = 999m });

        // Assert
        await Assert.That(stranger.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The survival check is not redundant with the 404. A handler that wrote the row and only
        // then reported it missing would satisfy the status code alone.
        await Assert.That(afterStranger["amount"]!.GetValue<decimal>()).IsEqualTo(-10m);
        await Assert.That(afterStranger["description"]!.GetValue<string>()).IsEqualTo("Coffee");

        // The 204 for the owner is load-bearing for the 404 above, not a duplicate of the other patch
        // tests. An unmapped route answers for every caller alike, so without a success on the very
        // same id the cross-budget assertion would hold for a route that does not exist at all.
        await Assert.That(owner.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task PatchTransaction_WithAccountFromAnotherBudget_ReturnsBadRequestAndKeepsItsOwnAccount()
    {
        // Arrange — B's account is a real, existing row; it is only out of reach because it belongs to
        // another budget. Moving a transaction into it would put one budget's money in another's ledger.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        HttpClient clientA = factoryA.CreateAuthenticatedClient();
        HttpClient clientB = factoryB.CreateAuthenticatedClient();
        Guid accountA = await CreateAccountAsync(clientA);
        Guid accountB = await CreateAccountAsync(clientB, "Checking B");
        Guid transactionA = await CreateTransactionAsync(clientA, accountA);

        // Act
        HttpResponseMessage patch = await clientA.PatchAsJsonAsync(
            $"/api/transactions/{transactionA}",
            new { accountId = accountB });
        JsonNode after = await GetJsonAsync(clientA, $"/api/transactions/{transactionA}");

        // Assert — the stranger's account reads as absent, so this is a 400 about an unknown account,
        // not a 403 or a 404 about the transaction.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(after["accountId"]!.GetValue<Guid>()).IsEqualTo(accountA);
    }

    [Test]
    public async Task PatchTransaction_WithUnknownAccountOrCategory_ReturnsBadRequest()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid transactionId = await CreateTransactionAsync(client, accountId);

        // Act
        HttpResponseMessage unknownAccount = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { accountId = Guid.CreateVersion7() });
        HttpResponseMessage unknownCategory = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { categoryId = Guid.CreateVersion7() });

        // Assert — a body that names a row that does not exist is a bad request, not a missing
        // transaction; the transaction in the route is right there.
        await Assert.That(unknownAccount.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unknownCategory.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task PatchTransaction_WithTooManyDecimalPlacesForTheCurrency_ReturnsBadRequest()
    {
        // Arrange — the account is in USD, which admits two decimal places.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid transactionId = await CreateTransactionAsync(client, accountId);

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { amount = 1.234m });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Assert — an edit is held to the same currency rule as a creation.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(after["amount"]!.GetValue<decimal>()).IsEqualTo(-10m);
    }

    [Test]
    public async Task PatchTransaction_WithNullAmountDateOrAccountId_ReturnsBadRequest()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid transactionId = await CreateTransactionAsync(client, accountId);

        // Act
        HttpResponseMessage nullAmount = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { amount = (decimal?)null });
        HttpResponseMessage nullDate = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { date = (string?)null });
        HttpResponseMessage nullAccountId = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { accountId = (Guid?)null });

        // Assert — null means "clear this field", and these three have nothing to clear to: a
        // transaction without an amount, a date or an account is not a transaction. Silently treating
        // the null as "leave it alone" would hide a client bug rather than report it.
        await Assert.That(nullAmount.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(nullDate.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(nullAccountId.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
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

    private static async Task<Guid> CreateAccountAsync(HttpClient client, string name = "Checking")
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

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
