using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using TestSupport;

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
            where tablename = 'payees' and indexname = 'IX_payees_budget_id_name'
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

        // Act
        JsonNode? json = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/payees"));

        // Assert
        await Assert.That(json!["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task PostTransaction_WithNewPayee_CreatesPayeeAndReturnsPayeeFields()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
    public async Task DeletingAReferencedPayee_IsRefusedByTheDatabase()
    {
        // Arrange — no application code path deletes a payee, so raw SQL is the only way to
        // exercise the constraint.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        JsonNode created = await PostTransactionAsync(client, "Starbucks");
        Guid payeeId = created["payeeId"]!.GetValue<Guid>();

        // Act — the transactions -> payees foreign key is the composite same-budget pair
        // (payee_id, budget_id); it cannot use ON DELETE SET NULL because budget_id is NOT NULL.
        // Refusing the delete forces an explicit decision about historical rows instead of
        // silently erasing the counterparty from transactions that already happened.
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand delete = new("delete from payees where id = @id", connection);
        delete.Parameters.AddWithValue("id", payeeId);
        PostgresException? caught = null;
        try
        {
            await delete.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            caught = exception;
        }

        JsonNode? list = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));
        JsonNode item = list!["items"]!.AsArray()[0]!;

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(list["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(item["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeId);
        await Assert.That(item["payeeName"]!.GetValue<string>()).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task GetTransactions_ReturnsResolvedPayeeName()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

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
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;

        // Act
        await PostTransactionAsync(clientA, "Starbucks");
        JsonNode? payeesB = await JsonNode.ParseAsync(await clientB.GetStreamAsync("/api/payees"));
        JsonNode? transactionsB = await JsonNode.ParseAsync(
            await clientB.GetStreamAsync("/api/transactions"));

        // Assert
        await Assert.That(payeesB!["items"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(transactionsB!["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task PatchPayee_WithANewName_ReturnsNoContentAndRenamesTheRowInPlace()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        JsonNode created = await PostTransactionAsync(client, "Starbux");
        Guid payeeId = created["payeeId"]!.GetValue<Guid>();

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = "Starbucks" });
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — the count is as load-bearing as the name. A handler that inserted a second row
        // called "Starbucks" instead of renaming the first would satisfy a name-only assertion.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payees["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>()).IsEqualTo(payeeId);
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>()).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task PatchPayee_WithACaseOnlyChangeOfItsOwnName_ReturnsNoContent()
    {
        // Arrange — the payee is created lower-case by the transaction that first named it, which is
        // exactly how a payee acquires the casing its owner later wants to fix.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        JsonNode created = await PostTransactionAsync(client, "starbucks");
        Guid payeeId = created["payeeId"]!.GetValue<Guid>();

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = "Starbucks" });
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — a row cannot collide with itself. The unique index on (budget_id, name) is
        // case-insensitive, so "starbucks" and "Starbucks" are the same key, but the row's own index
        // entry is replaced in the same update and is never compared against its former self. A naive
        // "does any payee already use this name?" pre-check would reject this and break the most
        // common real use of the feature, so this case gets its own test.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>()).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task PatchPayee_WithANameHeldByAnotherPayeeInTheSameBudget_ReturnsBadRequest()
    {
        // Arrange — two payees in one budget. The second one is the subject; the first one owns the
        // name it will try to take.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        await PostTransactionAsync(client, "Starbucks");
        JsonNode created = await PostTransactionAsync(client, "Costco");
        Guid costcoId = created["payeeId"]!.GetValue<Guid>();

        // Act — the exact name, then a case-differing one. The index is case-insensitive, so both are
        // the same collision and a handler that only compared ordinally would pass the first and fail
        // the second.
        HttpResponseMessage exact = await client.PatchAsJsonAsync(
            $"/api/payees/{costcoId}",
            new { name = "Starbucks" });
        HttpResponseMessage differentCase = await client.PatchAsJsonAsync(
            $"/api/payees/{costcoId}",
            new { name = "STARBUCKS" });
        JsonNode payees = await GetJsonAsync(client, "/api/payees");
        string[] names = payees["items"]!.AsArray()
            .Select(node => node!["name"]!.GetValue<string>())
            .ToArray();

        // Assert — a refused rename must leave both rows exactly as they were, not half-apply.
        await Assert.That(exact.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(differentCase.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(names).Contains("Costco");
        await Assert.That(names).Contains("Starbucks");
    }

    [Test]
    public async Task PatchPayee_WithBlankOverlongOrMissingName_ReturnsBadRequest()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        JsonNode created = await PostTransactionAsync(client, "Starbucks");
        Guid payeeId = created["payeeId"]!.GetValue<Guid>();

        // Act
        HttpResponseMessage blank = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = "   " });
        HttpResponseMessage tooLong = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = new string('a', 201) });
        HttpResponseMessage absent = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { });
        HttpResponseMessage explicitNull = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = (string?)null });
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — the name is required. Unlike PATCH /api/transactions, this is a targeted state
        // change with one field, so an absent or null name is a malformed request rather than a
        // no-op: there is nothing else the caller could have meant.
        await Assert.That(blank.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(tooLong.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(explicitNull.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>()).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task PatchPayee_WithAnUnknownId_ReturnsNotFoundButSucceedsForARealPayee()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        JsonNode created = await PostTransactionAsync(client, "Starbux");
        Guid payeeId = created["payeeId"]!.GetValue<Guid>();

        // Act
        HttpResponseMessage unknown = await client.PatchAsJsonAsync(
            $"/api/payees/{Guid.CreateVersion7()}",
            new { name = "Starbucks" });
        HttpResponseMessage real = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = "Starbucks" });

        // Assert
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The 204 is load-bearing for the 404 above, not a stray extra assertion. An unmapped route
        // answers 404 as well, so the assertion above on its own would hold today, before
        // PATCH /api/payees/{id} exists at all, and would keep holding if the route were later
        // deleted. Pairing it with a success on a real payee is what makes the 404 mean "the handler
        // looked and found nothing" rather than "there is no such route".
        await Assert.That(real.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task PatchPayee_FromAnotherBudget_ReturnsNotFoundButSucceedsForItsOwner()
    {
        // Arrange — two budgets over one database. The budget query filter is what makes A's payee
        // invisible to B; there is deliberately no 403 path in this API.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
        JsonNode created = await PostTransactionAsync(clientA, "Starbucks");
        Guid payeeId = created["payeeId"]!.GetValue<Guid>();

        // Act
        HttpResponseMessage stranger = await clientB.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = "Hijacked" });
        JsonNode payeesAfterStranger = await GetJsonAsync(clientA, "/api/payees");
        HttpResponseMessage owner = await clientA.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = "Starbucks Reserve" });
        JsonNode payeesAfterOwner = await GetJsonAsync(clientA, "/api/payees");

        // Assert
        await Assert.That(stranger.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The survival check is not redundant with the 404. A handler that wrote the row and only
        // then reported it missing would satisfy the status code alone.
        await Assert.That(payeesAfterStranger["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payeesAfterStranger["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo("Starbucks");

        // The 204 for the owner on that very same id is what makes the 404 above mean "the budget
        // query filter hid it". An unmapped route answers 404 for every caller alike, so without a
        // success on the same id the cross-budget assertion would hold for a route that does not
        // exist at all — which is exactly the state of the code this test was written against.
        await Assert.That(owner.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(payeesAfterOwner["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo("Starbucks Reserve");
    }

    [Test]
    public async Task PatchPayee_WithANameAnotherBudgetUses_ReturnsNoContentAndLeavesThatBudgetAlone()
    {
        // Arrange — the unique index is on (budget_id, name), so two budgets may each hold a payee
        // called "Starbucks". Budget A's rename must not be judged against budget B's rows.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
        JsonNode createdA = await PostTransactionAsync(clientA, "Starbux");
        await PostTransactionAsync(clientB, "Starbucks");
        Guid payeeA = createdA["payeeId"]!.GetValue<Guid>();

        // Act
        HttpResponseMessage patch = await clientA.PatchAsJsonAsync(
            $"/api/payees/{payeeA}",
            new { name = "Starbucks" });
        JsonNode payeesA = await GetJsonAsync(clientA, "/api/payees");
        JsonNode payeesB = await GetJsonAsync(clientB, "/api/payees");

        // Assert — each budget ends up with its own "Starbucks", two distinct rows.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(payeesA["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payeesA["items"]!.AsArray()[0]!["name"]!.GetValue<string>()).IsEqualTo("Starbucks");
        await Assert.That(payeesB["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payeesB["items"]!.AsArray()[0]!["name"]!.GetValue<string>()).IsEqualTo("Starbucks");
        await Assert.That(payeesB["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>()).IsNotEqualTo(payeeA);
    }

    [Test]
    public async Task PatchPayee_RenamesThePayeeOnEveryTransactionThatAlreadyNamedIt()
    {
        // Arrange — two past transactions pointing at the same payee.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        JsonNode first = await PostTransactionAsync(client, "Starbux");
        JsonNode second = await PostTransactionAsync(client, "Starbux");
        Guid payeeId = first["payeeId"]!.GetValue<Guid>();

        // Act
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = "Starbucks" });
        JsonNode transactions = await GetJsonAsync(client, "/api/transactions");

        // Assert — retroactivity is the intended behaviour of a rename, not an accident of how
        // TransactionDto is projected. A payee is one counterparty over time, so correcting its name
        // corrects every transaction that ever named it; a rename that only applied going forward
        // would leave the ledger showing two counterparties where there is one, which is what makes
        // this rule the difference between a meaningful rename and a cosmetic one.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(transactions["items"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(transactions["items"]!.AsArray()
            .All(node => node!["payeeName"]!.GetValue<string>() == "Starbucks")).IsTrue();
        await Assert.That(transactions["items"]!.AsArray()
            .All(node => node!["payeeId"]!.GetValue<Guid>() == payeeId)).IsTrue();
        await Assert.That(second["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeId);
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path) =>
        (await JsonNode.ParseAsync(await client.GetStreamAsync(path)))!;

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
        string label = $"Checking {Guid.CreateVersion7()}";
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            // Sealed, indexed and identified through SealedNarrative rather than sent as a flat name:
            // accounts.name is an AEAD envelope and accounts.name_key a blind index, so plain text is a
            // 400 from CreateAccountHandler and this seeding would never reach the subject of the test.
            // The label stays unique per call for the reason it always was — IX_accounts_budget_id_name_key
            // refuses two accounts indexing alike in one budget, and the index is deterministic in the
            // label, so a fixed label would make the second call in a budget a 23505.
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
    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
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
