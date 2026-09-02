using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using TestSupport;

namespace IntegrationTests;

public sealed class TransactionEndpointsTests
{
    [Test]
    public async Task PostValidTransaction_ReturnsCreatedLocationAndCamelCaseDto()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        // accountName IS THE ACCOUNT'S SEALED NAME, forwarded rather than resolved to text: the
        // transaction projection reads accounts.name, which is an AEAD envelope this server holds no key
        // for. The member kept its name because it still answers "which account", and the assertion
        // still pins the join — the envelope is deterministic in the seeding label, so a projection
        // that joined the wrong account, or none, fails here.
        await Assert.That(json["accountName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Checking"));
        await Assert.That(json["description"]!.GetValue<string>()).IsEqualTo("Groceries");
        await Assert.That(json["createdAtUtc"] is not null).IsTrue();
    }

    [Test]
    public async Task PostInvalidTransaction_ReturnsValidationProblemDetails()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        HttpResponseMessage response = await client.PostAsync(
            "/api/transactions",
            new StringContent("{", Encoding.UTF8, "application/json"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task GetTransactions_ReturnsNewestFirst()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        JsonNode? json = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));

        await Assert.That(json!["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task PostThenGet_PreservesDecimalDateAndAccountProjection()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);
        await client.PostAsJsonAsync("/api/transactions",
            new { amount = -42.50m, date = "2026-06-12", accountId, description = "Groceries" });

        JsonNode? json = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));
        JsonNode item = json!["items"]!.AsArray()[0]!;

        await Assert.That(item["amount"]!.GetValue<decimal>()).IsEqualTo(-42.50m);
        await Assert.That(item["date"]!.GetValue<string>()).IsEqualTo("2026-06-12");
        await Assert.That(item["accountId"]!.GetValue<Guid>()).IsEqualTo(accountId);
        await Assert.That(item["accountName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Checking"));
    }

    [Test]
    public async Task DeleteTransaction_RemovesTheTransactionAndLeavesItsReferencesIntact()
    {
        // Arrange — the transaction names a payee and a category so the delete has something to
        // wrongly cascade into. Without them the test could not tell a scoped delete from a greedy one.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);
        Guid categoryGroupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, categoryGroupId, "Groceries");
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");
        Guid transactionId = await CreateTransactionAsync(client, accountId, payeeId, categoryId);

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
            .Any(node => node!["id"]!.GetValue<Guid>() == payeeId)).IsTrue();
        await Assert.That(categories["items"]!.AsArray()
            .Any(node => node!["id"]!.GetValue<Guid>() == categoryId)).IsTrue();
    }

    [Test]
    public async Task DeleteTransaction_WithUnknownId_ReturnsNotFound()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

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
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid checkingId = await CreateAccountAsync(client);
        Guid savingsId = await CreateAccountAsync(client, "Savings");
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid groceriesId = await CreateCategoryAsync(client, groupId, "Groceries");
        Guid housingId = await CreateCategoryAsync(client, groupId, "Housing");
        Guid starbucksId = await CreatePayeeAsync(client, "Starbucks");
        Guid landlordId = await CreatePayeeAsync(client, "Landlord");
        Guid transactionId = await CreateTransactionAsync(client, checkingId, starbucksId, groceriesId);
        JsonNode before = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync($"/api/transactions/{transactionId}", new
        {
            amount = 99.99m,
            date = "2027-01-31",
            description = "Rent",
            accountId = savingsId,
            payeeId = landlordId,
            categoryId = housingId,
        });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Assert
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after["amount"]!.GetValue<decimal>()).IsEqualTo(99.99m);
        await Assert.That(after["date"]!.GetValue<string>()).IsEqualTo("2027-01-31");
        await Assert.That(after["description"]!.GetValue<string>()).IsEqualTo("Rent");
        await Assert.That(after["accountId"]!.GetValue<Guid>()).IsEqualTo(savingsId);
        await Assert.That(after["accountName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Savings"));
        await Assert.That(after["payeeId"]!.GetValue<Guid>()).IsEqualTo(landlordId);
        await Assert.That(after["payeeName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Landlord"));
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, groupId, "Groceries");
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");
        Guid transactionId = await CreateTransactionAsync(client, accountId, payeeId, categoryId);
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, groupId, "Groceries");
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");
        Guid transactionId = await CreateTransactionAsync(client, accountId, payeeId, categoryId);

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { amount = -12.75m });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Assert
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after["amount"]!.GetValue<decimal>()).IsEqualTo(-12.75m);
        await Assert.That(after["description"]!.GetValue<string>()).IsEqualTo("Coffee");
        await Assert.That(after["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeId);
        await Assert.That(after["payeeName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, groupId, "Groceries");
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");
        Guid transactionId = await CreateTransactionAsync(client, accountId, payeeId, categoryId);

        // Act — payeeId and NOT payeeName. That member no longer binds anything, and a body still
        // sending it is now a 400: UpdateTransactionRequest carries
        // [JsonUnmappedMemberHandling(Disallow)], which is per-type and reaches exactly it and
        // CreateTransactionCommand — the two shapes that held payeeName. It deliberately does NOT reach
        // CreatePayeeCommand or RenamePayeeRequest, so an extra member on POST /api/payees is still
        // ignored in silence. The refusal itself is asserted by
        // PatchTransaction_WithTheRetiredPayeeNameMember_IsRefusedAndChangesNothing, not here.
        //
        // This is the ONLY case that kills an inverted three-state branch: one written as
        // `if (command.PayeeId.Value is { } id)` with no IsSet arm silently drops present-and-null and
        // leaves the payee attached.
        HttpResponseMessage patch = await client.PatchAsJsonAsync($"/api/transactions/{transactionId}", new
        {
            description = (string?)null,
            payeeId = (Guid?)null,
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

    /// <summary>
    /// Attaching a payee to a transaction that had none, by naming a row the caller created first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaced <c>PatchTransaction_WithNewPayeeName_CreatesThePayee</c>, and the replacement is
    /// not a rename. That case asserted the patch <b>created</b> a payee, which was the whole of what
    /// it was for; a patch creates nothing now, so the assertion "the payee landed in the budget's
    /// list" moved to the create route and what is left here is the attachment.
    /// </para>
    /// <para>
    /// The count is what stops this passing against a handler that minted a second row beside the one
    /// it was pointed at — which no longer has a code path to arrive by, and is asserted anyway because
    /// nothing but this line would notice one coming back.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PatchTransaction_WithAPayeeId_AttachesThatPayeeAndCreatesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);
        Guid transactionId = await CreateTransactionAsync(client, accountId);
        Guid landlordId = await CreatePayeeAsync(client, "Landlord");

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { payeeId = landlordId });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after["payeeId"]!.GetValue<Guid>()).IsEqualTo(landlordId);
        await Assert.That(after["payeeName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Landlord"));
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(1);
    }

    /// <summary>
    /// A <c>payeeId</c> naming no payee this budget holds is a <b>400</b>, whether it names no row at
    /// all or a row another budget owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>payees.GetByIdAsync</c> guard in the handler can be deleted today and nothing
    /// objects.</b> <c>TransactionRepository.AddAsync</c> has no <c>catch</c> of any kind, so an
    /// unsatisfiable foreign key reaches <c>GlobalExceptionHandler</c> as a <c>23503</c> and the caller
    /// gets a 500 — a defect report for what is a bad request. These two cases are the whole of what
    /// closes it.
    /// </para>
    /// <para>
    /// <b>The cross-budget case is what separates "unknown id" from "foreign id", and neither covers
    /// the other.</b> A foreign payee SATISFIES the foreign key: the row exists, so a handler with no
    /// guard would answer 201 and file a transaction pointing at another budget's counterparty, with
    /// nothing red anywhere. What refuses it is the <c>BudgetIsolation</c> query filter making the read
    /// come back null, which is why it lands on the same sentence as an identifier matching nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PostTransaction_WithAnUnknownOrForeignPayeeId_ReturnsBadRequest()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
        Guid accountB = await CreateAccountAsync(clientB);
        Guid payeeA = await CreatePayeeAsync(clientA, "Starbucks");

        // Act
        HttpResponseMessage unknown = await clientB.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId = accountB,
            description = "Coffee",
            payeeId = Guid.CreateVersion7(),
        });
        JsonNode unknownProblem =
            (await JsonNode.ParseAsync(await unknown.Content.ReadAsStreamAsync()))!;

        HttpResponseMessage foreign = await clientB.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId = accountB,
            description = "Coffee",
            payeeId = payeeA,
        });
        JsonNode foreignProblem =
            (await JsonNode.ParseAsync(await foreign.Content.ReadAsStreamAsync()))!;

        JsonNode transactionsB = await GetJsonAsync(clientB, "/api/transactions");

        // Assert — 400 and not 500, keyed on the member the caller can correct.
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unknownProblem["errors"]!["PayeeId"] is not null).IsTrue();
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(foreignProblem["errors"]!["PayeeId"] is not null).IsTrue();

        // Neither refusal wrote a transaction. A handler that saved first and validated afterwards
        // would satisfy both status codes.
        await Assert.That(transactionsB["items"]!.AsArray().Count).IsEqualTo(0);
    }

    /// <summary>
    /// The patch leg's half of
    /// <see cref="PostTransaction_WithAnUnknownOrForeignPayeeId_ReturnsBadRequest" />, and it is owed
    /// separately: the two handlers resolve the payee in two different blocks of code.
    /// </summary>
    /// <remarks>
    /// <c>UpdateTransactionHandler</c> reads the payee <b>above every mutation</b>, so a refused patch
    /// must leave the transaction exactly as it was — including the payee it already had. A guard that
    /// ran after <c>transaction.Update(...)</c> would answer 400 having already changed the row in
    /// memory, and the assertions below are what would notice if that save ever went through.
    /// </remarks>
    [Test]
    public async Task PatchTransaction_WithAnUnknownOrForeignPayeeId_ReturnsBadRequestAndChangesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
        Guid accountB = await CreateAccountAsync(clientB);
        Guid payeeB = await CreatePayeeAsync(clientB, "Landlord");
        Guid transactionB = await CreateTransactionAsync(clientB, accountB, payeeB);
        Guid payeeA = await CreatePayeeAsync(clientA, "Starbucks");

        // Act — an amount rides along on both, so a handler that applied the rest of the patch before
        // refusing the payee is visible in the row rather than only in the status.
        HttpResponseMessage unknown = await clientB.PatchAsJsonAsync(
            $"/api/transactions/{transactionB}",
            new { amount = -99m, payeeId = Guid.CreateVersion7() });
        JsonNode unknownProblem =
            (await JsonNode.ParseAsync(await unknown.Content.ReadAsStreamAsync()))!;

        HttpResponseMessage foreign = await clientB.PatchAsJsonAsync(
            $"/api/transactions/{transactionB}",
            new { amount = -99m, payeeId = payeeA });
        JsonNode foreignProblem =
            (await JsonNode.ParseAsync(await foreign.Content.ReadAsStreamAsync()))!;

        JsonNode after = await GetJsonAsync(clientB, $"/api/transactions/{transactionB}");

        // Assert
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unknownProblem["errors"]!["PayeeId"] is not null).IsTrue();
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(foreignProblem["errors"]!["PayeeId"] is not null).IsTrue();

        // Untouched: the payee it already had, and the amount the refused patches carried.
        await Assert.That(after["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeB);
        await Assert.That(after["amount"]!.GetValue<decimal>()).IsEqualTo(-10m);
    }

    /// <summary>
    /// A create body still carrying the retired <c>payeeName</c> member is <b>refused</b>, and nothing
    /// is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the exact shape the shipped Angular client sends — <c>transactions-api.service.ts</c> still
    /// declares <c>payeeName?: string</c>. With nothing declared, <c>System.Text.Json</c> drops a
    /// property matching no parameter without a word, so this body used to answer <b>201</b> with a
    /// transaction naming no counterparty: a person's counterparty lost with nothing on either side
    /// seeing it. <c>[JsonUnmappedMemberHandling(Disallow)]</c> on
    /// <see cref="Application.Transactions.CreateTransaction.CreateTransactionCommand" /> is what turns
    /// that silence into a 400.
    /// </para>
    /// <para>
    /// <b>The refusal is per-type and reaches exactly two wire shapes</b> — this command and
    /// <c>TransactionEndpoints.UpdateTransactionRequest</c>, the two that carried <c>payeeName</c>. It
    /// is deliberately <b>not</b> on <c>CreatePayeeCommand</c> or <c>RenamePayeeRequest</c>, so an extra
    /// member on <c>POST /api/payees</c> is still ignored in silence; whether a shape refuses what it
    /// was not asked for is a contract decision that shape makes for itself, and no
    /// <c>UnmappedMemberHandling</c> belongs in <c>Api/Program.cs</c>, whose options every route shares.
    /// </para>
    /// <para>
    /// <b>The empty transaction list is the half a reader will drop.</b> A refusal that still persisted
    /// the row would be worse than the silent 201 it replaced: the caller is told the write failed while
    /// the payee-less transaction is filed anyway.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PostTransaction_WithTheRetiredPayeeNameMember_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);

        // Act — the stale client's body verbatim: a payee NAME and no payeeId.
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = "Coffee",
            payeeName = "Starbucks",
        });
        JsonNode transactions = await GetJsonAsync(client, "/api/transactions");
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — refused, and nothing landed. The two empty lists are the point: neither a
        // payee-less transaction nor a payee minted from the word.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(transactions["items"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(0);
    }

    /// <summary>
    /// The patch leg's half of
    /// <see cref="PostTransaction_WithTheRetiredPayeeNameMember_IsRefusedAndWritesNothing" />, and it is
    /// owed separately: the attribute is per-type, so the create command carrying it says nothing about
    /// the patch request, which is a different declaration in a different file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body names an <c>amount</c> beside the retired member, so a binder that dropped
    /// <c>payeeName</c> and bound the rest is visible in the row and not only in the status. Without
    /// that, an implementation that refused nothing and applied the amount would still fail the status
    /// assertion for a reason nobody could read off the case.
    /// </para>
    /// <para>
    /// The payee the transaction already holds is asserted for the same reason it is on
    /// <see cref="PatchTransaction_WithAnUnknownOrForeignPayeeId_ReturnsBadRequestAndChangesNothing" />:
    /// a refusal that detached the counterparty on its way out is the failure this member was retired
    /// to stop.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PatchTransaction_WithTheRetiredPayeeNameMember_IsRefusedAndChangesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);
        Guid payeeId = await CreatePayeeAsync(client, "Landlord");
        Guid transactionId = await CreateTransactionAsync(client, accountId, payeeId);

        // Act — the stale client's patch body: a payee NAME riding beside a field that does bind.
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/transactions/{transactionId}",
            new { amount = -99m, payeeName = "Starbucks" });
        JsonNode after = await GetJsonAsync(client, $"/api/transactions/{transactionId}");

        // Assert — refused, and the row is exactly as it was: the amount the refused patch carried
        // never landed, and the counterparty it did not name is still attached.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(after["amount"]!.GetValue<decimal>()).IsEqualTo(-10m);
        await Assert.That(after["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeId);
    }

    [Test]
    public async Task PatchTransaction_WithUnknownId_ReturnsNotFound()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

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
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
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
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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

    /// <summary>
    /// Creates one transaction, naming a payee <b>this caller already created</b> rather than
    /// describing one by name.
    /// </summary>
    /// <remarks>
    /// The parameter used to be a <c>string? payeeName</c> that the server resolved into a row,
    /// creating one if no row held that name. It cannot: <c>payees.name</c> is an AEAD envelope drawn
    /// under a fresh nonce, so two seals of one name are different bytes and no lookup by name is a
    /// question this side can answer. Creating the payee is a request of its own now — see
    /// <see cref="CreatePayeeAsync" /> — and what arrives here is the row it created.
    /// </remarks>
    private static async Task<Guid> CreateTransactionAsync(
        HttpClient client,
        Guid accountId,
        Guid? payeeId = null,
        Guid? categoryId = null)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = "Coffee",
            payeeId,
            categoryId,
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// Creates one payee through the route that now owns creation and hands back its identifier.
    /// </summary>
    /// <remarks>
    /// The id is on the body because the client mints it: it is the associated data
    /// <paramref name="label" />'s envelope was sealed against, so this API has to be sent the spelling
    /// it will hand back. <c>"D"</c> is the one spelling <c>CanonicalIdentifier</c> accepts.
    /// </remarks>
    private static async Task<Guid> CreatePayeeAsync(HttpClient client, string label)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(label),
            nameKey = SealedNarrative.EncodedIndex(label),
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// Creates one category group from <paramref name="label" /> and returns the identifier it minted.
    /// </summary>
    /// <remarks>
    /// <b>The parameter is a LABEL, not a name.</b> category_groups.name is an AEAD envelope and
    /// category_groups.name_key a blind index, so a flat string is a 400 from
    /// CreateCategoryGroupHandler and this seeding would never reach the subject of any case below.
    /// The identifier is minted here rather than read off the 201 for the reason the sibling helper in
    /// CategoryIntegrationTests writes out: the row id is the associated data the name is sealed
    /// against, so reading a server-invented one back would hand out a group nobody can open.
    /// The description is left absent — every caller here seeds a group only to hang a category off
    /// it, and a description would add two CHECKs for these cases to trip over for nothing.
    /// </remarks>
    private static async Task<Guid> CreateCategoryGroupAsync(HttpClient client, string label)
    {
        Guid id = Guid.CreateVersion7();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName(label),
            nameKey = SealedNarrative.EncodedIndex(label),
            description = (string?)null,
        });
        response.EnsureSuccessStatusCode();
        return id;
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

    /// <summary>
    /// Creates one account, taking <paramref name="label" /> as the text both halves of the name are
    /// built from rather than as a value any column holds.
    /// </summary>
    /// <remarks>
    /// The parameter kept its job — keeping two seeded accounts apart, which it still does because
    /// SealedNarrative is deterministic in its label — and lost its old meaning, which is why it was
    /// renamed. accounts.name is an AEAD envelope and accounts.name_key a blind index, so a flat name is
    /// a 400 from CreateAccountHandler and every case in this file would fail in its Arrange.
    /// </remarks>
    private static async Task<Guid> CreateAccountAsync(HttpClient client, string label = "Checking")
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(label),
            nameKey = SealedNarrative.EncodedIndex(label),
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every
    /// request below authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
