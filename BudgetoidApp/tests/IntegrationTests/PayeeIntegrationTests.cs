using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Security;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The payee routes end to end, over a column this server cannot read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case here used to seed a payee by naming one on a transaction, and none of them can any
/// more.</b> <c>payeeName</c> is not a member of the transaction body, and the server that resolved it
/// held two capabilities it no longer has: reading a name back, and folding its case. A payee is created
/// through <c>POST /api/payees</c> and named on a transaction by its identifier.
/// </para>
/// <para>
/// <b>An assertion about a name is an assertion about an envelope.</b> There is no name on the wire, so
/// "this budget's row came back and that one's did not" is made against
/// <see cref="SealedNarrative.EncodedName" />, which is deterministic in its label and therefore still
/// per-row and still distinguishing. What it is not is readable, which is the product working.
/// </para>
/// </remarks>
public sealed class PayeeIntegrationTests
{
    [Test]
    public async Task PayeesTable_HasNoCollationAndAUniqueIndexOverTheBlindIndex()
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
        object? collation = await collationCommand.ExecuteScalarAsync();

        await using NpgsqlCommand nameTypeCommand = new(
            """
            select data_type
            from information_schema.columns
            where table_name = 'payees' and column_name = 'name'
            """, connection);
        string? nameType = (string?)await nameTypeCommand.ExecuteScalarAsync();

        await using NpgsqlCommand indexCommand = new(
            """
            select indexdef
            from pg_indexes
            where tablename = 'payees' and indexname = 'IX_payees_budget_id_name_key'
            """, connection);
        string? indexDef = (string?)await indexCommand.ExecuteScalarAsync();

        await using NpgsqlCommand retiredIndexCommand = new(
            """
            select indexdef
            from pg_indexes
            where tablename = 'payees' and indexname = 'IX_payees_budget_id_name'
            """, connection);
        object? retiredIndexDef = await retiredIndexCommand.ExecuteScalarAsync();

        // Assert — the collation is not merely a different one, it is gone: bytea is not collatable, so
        // the case_insensitive collation this column carried left BY FORCE rather than by choice. What
        // it used to do — make "Starbucks" and "starbucks" one payee — is now the client's, which folds
        // the text before it computes the index. Nothing on this side does it any more.
        await Assert.That(type).IsEqualTo("timestamp with time zone");
        await Assert.That(nameType).IsEqualTo("bytea");
        await Assert.That(collation is null or DBNull).IsTrue();

        // The index moved with it, and had to: equal names are unequal bytes, because every seal draws
        // a fresh nonce, so an index over `name` could not see a duplicate at all. Uniqueness survived
        // by moving to a column the database cannot interpret and can still compare for equality.
        //
        // THE WHOLE RENDERING, NEVER Contains. pg_get_indexdef prints the index NAME inside the string
        // it returns, and this index is called IX_payees_budget_id_name_key — so Contains("budget_id")
        // and Contains("name_key") are both satisfied by the name alone, and pass over an index whose
        // columns are (budget_id, name). Pointing the model's HasIndex back at Name while keeping
        // HasDatabaseName is a one-line mutation that leaves this case green under Contains and red
        // here. SchemaConstraintSnapshotTests pins the same line for the same reason.
        await Assert.That(indexDef).IsEqualTo(
            """CREATE UNIQUE INDEX "IX_payees_budget_id_name_key" ON public.payees USING btree (budget_id, name_key)""");

        // And the old one is not still standing beside it. Without this line a migration that added the
        // new index and forgot to drop the old one would pass every assertion above while leaving a
        // unique constraint over ciphertext that refuses nothing and nobody would ever see fire.
        await Assert.That(retiredIndexDef is null or DBNull).IsTrue();
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

    /// <summary>
    /// <c>POST /api/payees</c> as the subject rather than as the seeding step every other case here
    /// uses it as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is no "the id comes back in the spelling it was sent" assertion, and writing one would
    /// be a decoration.</b> <c>PayeeDto.Id</c> is a <see cref="Guid" />, so
    /// <c>System.Text.Json</c> renders it in the one form <see cref="Guid" /> renders whatever text
    /// arrived — the echo can only differ for input the handler already refuses, which is
    /// <see cref="PostPayee_WithANonCanonicalId_IsRejected" />'s subject and not this one. What is
    /// asserted instead is that the row carries the identifier the CLIENT minted: a route that minted
    /// its own would satisfy every other line here and produce a name no browser can ever open,
    /// because the id is the associated data the envelope was sealed against.
    /// </para>
    /// <para>
    /// <b>The <c>Location</c> is asserted because it is the only reason
    /// <c>GET /api/payees/{id}</c> exists</b>, and a header naming an address that answers 404 is
    /// worse than no header at all. That it RESOLVES is
    /// <see cref="GetPayee_FollowedFromTheCreatedLocation_CarriesTheSameEnvelopeAsThe201" />'s job.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PostPayee_CreatesTheRowWithTheEnvelopeAndNoBlindIndex()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var id = Guid.CreateVersion7();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/payees", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Starbucks"),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        JsonNode created = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        JsonNode list = await GetJsonAsync(client, "/api/payees");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created["id"]!.GetValue<Guid>()).IsEqualTo(id);
        await Assert.That(created["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));

        // NO BLIND INDEX ON ANY READ, and its absence is a decision rather than an omission. A payee
        // list is the set of counterparties one person deals with, and its index column is a
        // deterministic per-budget fingerprint of every one of those names; handing it back would let
        // anybody who saw two responses tell which counterparties they had in common, with no key
        // anywhere in the exchange. The client recomputes it from the name it just decrypted.
        await Assert.That(created["nameKey"]).IsNull();

        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo($"/api/payees/{id}");

        // One row, carrying the client's identifier. The count is what stops a handler that inserted
        // twice from passing, and the id is what stops one that minted its own.
        await Assert.That(list["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(list["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>()).IsEqualTo(id);
        await Assert.That(list["items"]!.AsArray()[0]!["nameKey"]).IsNull();
    }

    [Test]
    public async Task PostPayee_WithANonCanonicalId_IsRejected()
    {
        // Arrange — CanonicalIdentifierTests covers the parser and CreatePayeeHandlerTests covers the
        // handler; NOTHING PROVED IT WAS WIRED INTO THIS ROUTE. Every seed in this assembly sends the
        // "D" spelling, so deleting the TryParse call reddens nothing that exists: System.Text.Json
        // would fold all four spellings below to one value before a handler saw text if the command
        // bound Id as a Guid, and the refusal would become unwritable while every 201 looked identical.
        // It fails in a browser months later, as a payee name that will not open.
        //
        // The set is DERIVED from one identifier rather than typed out, so it cannot drift from what
        // Guid can render: the same value upper-cased, braced, unhyphenated, and with surrounding
        // whitespace — four different texts on the wire and one Guid in the row.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

        var canonical = Guid.CreateVersion7();
        string[] refused =
        [
            canonical.ToString("D").ToUpperInvariant(),
            canonical.ToString("B"),
            canonical.ToString("N"),
            $" {canonical.ToString("D")} ",

            // Canonically SPELLED and refused anyway, for its own reason: the all-zero uuid is what an
            // unset field sends and arrives indistinguishable from a value somebody chose. It is here
            // to keep a reader from concluding the route only judges spelling.
            Guid.Empty.ToString("D"),
        ];

        // Act
        List<string> accepted = [];
        List<string> misattributed = [];
        foreach (string spelling in refused)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/payees", new
            {
                id = spelling,
                name = SealedNarrative.EncodedName("Starbucks"),
                nameKey = SealedNarrative.EncodedIndex("Starbucks"),
            });
            if (response.StatusCode is not HttpStatusCode.BadRequest)
            {
                accepted.Add($"'{spelling}' answered {(int)response.StatusCode}");
                continue;
            }

            JsonNode? problem = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (problem?["errors"]?["Id"] is null)
            {
                misattributed.Add($"'{spelling}' was refused without naming Id");
            }
        }

        // Assert — refused, AND keyed on the member a caller can correct. A 400 blamed on something
        // else would be a refusal the client cannot act on.
        await Assert.That(accepted).IsEmpty();
        await Assert.That(misattributed).IsEmpty();

        // Non-vacuity: the canonical spelling of the same value, with the same name beside it, is
        // accepted — so the loop above measured the spelling and not a route that refuses every create.
        HttpResponseMessage good = await client.PostAsJsonAsync("/api/payees", new
        {
            id = canonical.ToString("D"),
            name = SealedNarrative.EncodedName("Starbucks"),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        await Assert.That(good.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    /// <summary>
    /// A create colliding on <c>IX_payees_budget_id_name_key</c> answers <b>409</b>, carrying the one
    /// sentence that tells the caller what to do next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The repository's translation is covered by unit tests; the ROUTE's mapping is not.</b>
    /// <c>ConflictExceptionHandler</c> is what turns the domain's <c>ConflictException</c> into this
    /// status, and it is registered in <c>Program.cs</c> — a registration nothing on the payee surface
    /// exercised. Unregistered, the same exception reaches <c>GlobalExceptionHandler</c> and this
    /// becomes a 500.
    /// </para>
    /// <para>
    /// <b>The detail sentence is asserted in full and that is not over-specification.</b> The handler
    /// is shared by every conflict in the product: it writes one fixed title, adds no extension member
    /// and carries no field errors, so this sentence is the WHOLE of what distinguishes this 409 from
    /// any other and the whole of what a client is told. A create's remedy is to adopt the row that
    /// already exists, which is not a correction to a field — which is why it is a 409 here and a 400
    /// keyed on <c>Name</c> on the rename leg, on the very same index.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PostPayee_WithABlindIndexAnotherPayeeHolds_AnswersConflict()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid firstId = await CreatePayeeAsync(client, "Starbucks");

        // Act — a different identifier and a different envelope would both be legal; what collides is
        // the index, which is deterministic in the label because a client folds and hashes the same
        // text to the same digest every time.
        HttpResponseMessage conflict = await client.PostAsJsonAsync("/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Starbucks"),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        JsonNode problem = (await JsonNode.ParseAsync(await conflict.Content.ReadAsStreamAsync()))!;
        JsonNode list = await GetJsonAsync(client, "/api/payees");

        // Assert
        await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(problem["title"]!.GetValue<string>())
            .IsEqualTo("The request conflicts with the current state of the resource.");
        await Assert.That(problem["detail"]!.GetValue<string>()).IsEqualTo(
            "A payee with this name already exists in this budget. "
            + "Re-read the payee list and use the payee it already holds.");

        // No field errors, because there is no field to correct. A 400-shaped body here would ask the
        // person to edit a name they typed correctly.
        await Assert.That(problem["errors"]).IsNull();

        // The refused create wrote nothing: still one payee, still the first one's identifier.
        await Assert.That(list["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(list["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>()).IsEqualTo(firstId);
    }

    /// <summary>
    /// Two malformed opaque members produce two keys in one problem document.
    /// </summary>
    /// <remarks>
    /// NOTHING SENT TWO MALFORMED MEMBERS AT ONCE, so a fail-fast handler that reported only the first
    /// was green everywhere. The three members are produced by one piece of client code and are opaque
    /// to this server in the same way, so a caller that got two of them wrong would otherwise learn
    /// about the second only after fixing the first and sending everything again. The COUNT is what
    /// makes this case impossible for a fail-fast handler to pass: any single key-presence assertion
    /// would be satisfied by whichever member it happened to stop on.
    /// </remarks>
    [Test]
    public async Task PostPayee_WithEveryOpaqueMemberMalformed_ReportsAllOfThemAtOnce()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D").ToUpperInvariant(),
            name = "not an envelope",
            nameKey = "not an index",
        });
        JsonNode problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem["errors"]!.AsObject().Count).IsEqualTo(3);
        await Assert.That(problem["errors"]!["Id"] is not null).IsTrue();
        await Assert.That(problem["errors"]!["Name"] is not null).IsTrue();
        await Assert.That(problem["errors"]!["NameKey"] is not null).IsTrue();
    }

    /// <summary>
    /// Every shape of name this route refuses, sent as a VALUE rather than as a missing member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this case the only thing proved about <c>CiphertextEnvelopeText</c> on the payee surface
    /// was that it rejects <see langword="null" />. A decoder that accepted anything base64url — no
    /// floor, no ceiling, no version byte — passed every payee case in this assembly, and each of the
    /// four arguments below is a different half of it.
    /// </para>
    /// <para>
    /// The version byte is the one a reader will call paranoid. It is what stops a row of bytes no
    /// version of this system can interpret being filed and discovered on the day somebody needs the
    /// name back — and version <c>0</c> is what an uninitialised member, a zero-filled allocation and
    /// a stubbed client all send.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("not base64url")]
    [Arguments("one byte under the framing floor")]
    [Arguments("a version this deployment has never implemented")]
    [Arguments("one byte over the column's cap")]
    public async Task PostPayee_WithAnUnacceptableNameEnvelope_IsRejectedNamingTheName(string shape)
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = MalformedEnvelope(shape),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        JsonNode problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        // Assert — keyed on Name alone, so an over-eager handler reporting the whole body cannot pass.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem["errors"]!.AsObject().Count).IsEqualTo(1);
        await Assert.That(problem["errors"]!["Name"] is not null).IsTrue();
    }

    /// <summary>
    /// Every shape of blind index this route refuses.
    /// </summary>
    /// <remarks>
    /// <b>31 and 33 are the case, and one of them alone is not.</b> The constraint is an EQUALITY
    /// rather than a ceiling: 33 bytes is refused by a <c>&lt;= 32</c> rule as well, so only the
    /// 31-byte argument tells a width apart from a bound. A short digest stores, reads back, keys
    /// perfectly, never collides and matches no payee the client will ever look for — and nothing on
    /// this side can recompute it to notice, because the index key lives in a browser. The width is
    /// the whole of the defence.
    /// </remarks>
    [Test]
    [Arguments("not base64url")]
    [Arguments("one byte short of the width")]
    [Arguments("one byte over the width")]
    public async Task PostPayee_WithAnUnacceptableBlindIndex_IsRejectedNamingTheIndex(string shape)
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Starbucks"),
            nameKey = MalformedBlindIndex(shape),
        });
        JsonNode problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem["errors"]!.AsObject().Count).IsEqualTo(1);
        await Assert.That(problem["errors"]!["NameKey"] is not null).IsTrue();
    }

    /// <summary>
    /// <c>GET /api/payees/{id}</c>, reached the way a client reaches it: by following the
    /// <c>Location</c> the create answered with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The route was exercised by nothing and could have been deleted with the suite green.</b>
    /// It exists only because <c>POST /api/payees</c> answers 201 with a <c>Location</c>, and a
    /// <c>Location</c> naming an address that answers 404 is a header that lies.
    /// </para>
    /// <para>
    /// <b>The assertion that matters is that the two <c>name</c> strings are EQUAL.</b> They are
    /// produced by two different pieces of code — <c>PayeeDto.FromPayee</c> over the entity the
    /// handler just wrote, and <c>PayeeReadService</c> over a row it read back — so one could emit
    /// padded standard base64 and the other unpadded base64url with every other case in this
    /// assembly green, and the client's strict decoder would refuse whichever half it met second.
    /// </para>
    /// </remarks>
    [Test]
    public async Task GetPayee_FollowedFromTheCreatedLocation_CarriesTheSameEnvelopeAsThe201()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var id = Guid.CreateVersion7();
        HttpResponseMessage create = await client.PostAsJsonAsync("/api/payees", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Starbucks"),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        JsonNode created = (await JsonNode.ParseAsync(await create.Content.ReadAsStreamAsync()))!;

        // Act — the header, not a path this test built. A Location assembled here would test the
        // string this file writes rather than the one the route answers with.
        string location = create.Headers.Location!.ToString();
        HttpResponseMessage read = await client.GetAsync(location);
        JsonNode fetched = (await JsonNode.ParseAsync(await read.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fetched["id"]!.GetValue<Guid>()).IsEqualTo(id);

        // THE TWO ENCODERS AGREE. Written as a comparison of the two responses and not of each against
        // the fixture, because either half agreeing with the fixture is a weaker claim than the two
        // agreeing with each other — the fixture is one more encoder, and it is this file's.
        await Assert.That(fetched["name"]!.GetValue<string>())
            .IsEqualTo(created["name"]!.GetValue<string>());

        // Non-vacuity: both of them are the envelope the client sent, so the line above is not two
        // routes agreeing on nothing.
        await Assert.That(fetched["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));

        // The read leg withholds the index for the same reason the create leg does.
        await Assert.That(fetched["nameKey"]).IsNull();
    }

    [Test]
    public async Task GetPayee_FromAnotherBudget_ReturnsNotFoundButSucceedsForItsOwner()
    {
        // Arrange — two budgets over one database. The BudgetIsolation query filter is what makes A's
        // payee invisible to B, with RLS behind it; there is deliberately no 403 path in this API.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
        Guid payeeId = await CreatePayeeAsync(clientA, "Starbucks");

        // Act
        HttpResponseMessage stranger = await clientB.GetAsync($"/api/payees/{payeeId}");
        HttpResponseMessage unknown = await clientA.GetAsync($"/api/payees/{Guid.CreateVersion7()}");
        HttpResponseMessage owner = await clientA.GetAsync($"/api/payees/{payeeId}");
        JsonNode fetched = (await JsonNode.ParseAsync(await owner.Content.ReadAsStreamAsync()))!;

        // Assert — a stranger's payee and a payee that never existed are one answer, on purpose: a 404
        // that differed from a 403 would tell B that this identifier names somebody's row.
        await Assert.That(stranger.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The 200 for the owner on that very same id is what makes the 404 above mean "the budget
        // filter hid it". An unmapped route answers 404 for every caller alike, so without this line
        // the cross-budget assertion would hold for a route that does not exist at all.
        await Assert.That(owner.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fetched["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
    }

    [Test]
    public async Task PostTransaction_WithACreatedPayee_NamesItOnTheTransactionAndOnTheList()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

        // Act — two requests where there used to be one. The payee is created first and named by its
        // identifier, because the transaction handler can no longer resolve a name into a row.
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");
        HttpResponseMessage response = await PostTransactionResponseAsync(client, payeeId);
        JsonNode? created = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonNode? payees = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/payees"));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created!["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeId);
        await Assert.That(created["payeeName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
        await Assert.That(payees!["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
    }

    [Test]
    public async Task DeletingAReferencedPayee_IsRefusedByTheDatabase()
    {
        // Arrange — no application code path deletes a payee, so raw SQL is the only way to
        // exercise the constraint.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");
        await PostTransactionAsync(client, payeeId);

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
        await Assert.That(item["payeeName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
    }

    [Test]
    public async Task GetTransactions_ReturnsTheSealedPayeeName()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");
        await PostTransactionAsync(client, payeeId);

        // Act
        JsonNode? list = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));
        JsonNode item = list!["items"]!.AsArray()[0]!;

        // Assert — the join still resolves; what it resolves to is the payee's envelope. The client
        // opens it under the binding for payees.name at the PAYEE's row id, which it rebuilds from
        // payeeId beside it — not the transaction's own, which would fail to authenticate.
        await Assert.That(item["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeId);
        await Assert.That(item["payeeName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
    }

    [Test]
    public async Task Payees_AreIsolatedPerUser()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;

        // Act
        Guid payeeId = await CreatePayeeAsync(clientA, "Starbucks");
        await PostTransactionAsync(clientA, payeeId);
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
        Guid payeeId = await CreatePayeeAsync(client, "Starbux");

        // Act
        HttpResponseMessage patch = await RenamePayeeAsync(client, payeeId, "Starbucks");
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — the count is as load-bearing as the name. A handler that inserted a second row
        // called "Starbucks" instead of renaming the first would satisfy a name-only assertion.
        //
        // This case is also the end-to-end half of the grant pair. Rename writes name AND name_key in
        // one statement, so a GRANT UPDATE list naming only one of them refuses the whole statement
        // with 42501 and this 204 becomes a 500. TenancySchemaTests says WHICH column; this says the
        // feature. Keep both.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payees["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>()).IsEqualTo(payeeId);
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
    }

    /// <summary>
    /// A payee renamed to a <b>different envelope carrying the index it already holds</b> answers 204,
    /// and the new envelope is what comes back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a case-only correction looks like from this side: the client folds before it
    /// hashes, so "starbucks" and "Starbucks" produce one index and only the envelope beside it
    /// differs. The row's own index entry is replaced in the same update and is never compared against
    /// its former self — a "does any payee already hold this index?" pre-check would refuse this and
    /// break the most common real use of the feature.
    /// </para>
    /// <para>
    /// <b>THE TWO HALVES ARE DELIBERATELY MADE TO DISAGREE, BY HAND, AND THAT IS THE CASE.</b> This
    /// case sent one label through <see cref="SealedNarrative.EncodedName" /> and
    /// <see cref="SealedNarrative.EncodedIndex" />, which meant it re-sent the row's existing envelope
    /// as well as its existing index — so it could not tell "a row does not collide with itself" from
    /// "the UPDATE wrote nothing at all". A handler that discarded the body and answered 204 passed it.
    /// The honest shape is a new envelope under the old index, and
    /// <see cref="SealedNarrative.Indexed" /> offers no spelling for that pair on purpose: it takes ONE
    /// label because a browser seals a name and indexes that same text, and a two-label overload would
    /// be a way to write a row whose index describes a name it does not hold. So the disagreement is
    /// built here, in the open, where a reviewer reads it — and it is not a defect being seeded, it is
    /// the one legitimate pairing where the two labels differ and the index does not.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PatchPayee_WithANewEnvelopeUnderTheBlindIndexItAlreadyHolds_ReturnsNoContent()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");

        // Act — a new envelope, the index the row already carries. Two labels, hand-paired.
        HttpResponseMessage patch = await client.PatchAsJsonAsync($"/api/payees/{payeeId}", new
        {
            name = SealedNarrative.EncodedName("starbucks"),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — non-vacuity first: the two envelopes really are different bytes, so "the new one
        // came back" is a claim about a write and not about two spellings of one value.
        await Assert.That(SealedNarrative.EncodedName("starbucks"))
            .IsNotEqualTo(SealedNarrative.EncodedName("Starbucks"));

        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payees["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>()).IsEqualTo(payeeId);

        // THE NEW ENVELOPE, which is the half the old shape of this case could not see. A handler that
        // answered 204 without writing anything fails here.
        //
        // WHAT IT DOES NOT COVER, SO NOBODY COUNTS THREE GUARDS WHERE THERE ARE TWO: this case says
        // nothing about the GRANT UPDATE column list. The index is unchanged by construction, so the
        // content comparer reports NameKey untouched and EF emits a ONE-COLUMN update — exactly the
        // one-column probe shape this slice condemns — which a GRANT UPDATE (name) role accepts. The
        // grant is covered by PatchPayee_WithANewName_ReturnsNoContentAndRenamesTheRowInPlace, whose
        // different index forces both columns into the statement, and by TenancySchemaTests, which
        // names the columns. Two guards, and this is neither of them.
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("starbucks"));
    }

    [Test]
    public async Task PatchPayee_WithANameHeldByAnotherPayeeInTheSameBudget_ReturnsBadRequest()
    {
        // Arrange — two payees in one budget. The second one is the subject; the first one owns the
        // index it will try to take.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        await CreatePayeeAsync(client, "Starbucks");
        Guid costcoId = await CreatePayeeAsync(client, "Costco");

        // Act
        HttpResponseMessage collision = await RenamePayeeAsync(client, costcoId, "Starbucks");
        JsonNode payees = await GetJsonAsync(client, "/api/payees");
        string[] names = payees["items"]!.AsArray()
            .Select(node => node!["name"]!.GetValue<string>())
            .ToArray();

        // Assert — 400 and not the 409 the CREATE path answers on this very same index. The remedy is
        // what differs: a rename's is a different name, which is a statement about a field of the
        // request; a create's is to adopt the row that already exists, which is not.
        //
        // A refused rename must leave both rows exactly as they were, not half-apply.
        await Assert.That(collision.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(names).Contains(SealedNarrative.EncodedName("Costco"));
        await Assert.That(names).Contains(SealedNarrative.EncodedName("Starbucks"));
    }

    [Test]
    public async Task PatchPayee_WithEitherHalfOfTheNameMissing_ReturnsBadRequest()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");

        // Act — the blank and the 201-character cases this test used to carry are GONE, and their
        // absence is a capability that moved rather than a rule quietly dropped. The server holds an
        // envelope it cannot count characters in, so "a name is not just spaces" and the 200-character
        // ceiling are the client's to enforce before it seals. Do not restore them here; there is
        // nothing on this side to check them against.
        HttpResponseMessage nameOnly = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = SealedNarrative.EncodedName("Starbucks Reserve") });
        HttpResponseMessage indexOnly = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { nameKey = SealedNarrative.EncodedIndex("Starbucks Reserve") });
        HttpResponseMessage absent = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { });
        HttpResponseMessage explicitNulls = await client.PatchAsJsonAsync(
            $"/api/payees/{payeeId}",
            new { name = (string?)null, nameKey = (string?)null });
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — both halves are required. A body carrying only `name` is worse than malformed: it
        // is the half-rename IndexedName exists to make unspellable, arriving as a shape the binder
        // would otherwise have accepted, and it would leave the row indexed under the name it no
        // longer holds — a payee the client can neither find nor re-create.
        await Assert.That(nameOnly.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(indexOnly.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(explicitNulls.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
    }

    /// <summary>
    /// The rename leg's half of
    /// <see cref="PostPayee_WithEveryOpaqueMemberMalformed_ReportsAllOfThemAtOnce" />: two malformed
    /// VALUES, two keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its neighbour above sends missing members and explicit nulls, which is all either decoder was
    /// ever proved to reject on this route. A decoder that accepted anything base64url — no floor, no
    /// cap, no version byte, no width — passed every payee case in this assembly, and the row it wrote
    /// would store, read back and open under nobody's key.
    /// </para>
    /// <para>
    /// Two members rather than three: a rename re-seals against the row's EXISTING identifier, which
    /// the client read back from this API in the one spelling <see cref="Guid" /> renders, so there is
    /// no spelling in the URL to preserve and no <c>Id</c> key to expect.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PatchPayee_WithBothOpaqueMembersMalformed_ReportsBothAtOnce()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid payeeId = await CreatePayeeAsync(client, "Starbucks");

        // Act — a short envelope and a short index, both well-formed base64url, so nothing but the two
        // decoders' own rules can refuse them.
        HttpResponseMessage response = await client.PatchAsJsonAsync($"/api/payees/{payeeId}", new
        {
            name = MalformedEnvelope("one byte under the framing floor"),
            nameKey = MalformedBlindIndex("one byte short of the width"),
        });
        JsonNode problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem["errors"]!.AsObject().Count).IsEqualTo(2);
        await Assert.That(problem["errors"]!["Name"] is not null).IsTrue();
        await Assert.That(problem["errors"]!["NameKey"] is not null).IsTrue();

        // The refused rename left the row exactly as it was, rather than half-applying.
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
    }

    [Test]
    public async Task PatchPayee_WithAnUnknownId_ReturnsNotFoundButSucceedsForARealPayee()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid payeeId = await CreatePayeeAsync(client, "Starbux");

        // Act
        HttpResponseMessage unknown = await RenamePayeeAsync(
            client, Guid.CreateVersion7(), "Starbucks");
        HttpResponseMessage real = await RenamePayeeAsync(client, payeeId, "Starbucks");

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
        Guid payeeId = await CreatePayeeAsync(clientA, "Starbucks");

        // Act
        HttpResponseMessage stranger = await RenamePayeeAsync(clientB, payeeId, "Hijacked");
        JsonNode payeesAfterStranger = await GetJsonAsync(clientA, "/api/payees");
        HttpResponseMessage owner = await RenamePayeeAsync(clientA, payeeId, "Starbucks Reserve");
        JsonNode payeesAfterOwner = await GetJsonAsync(clientA, "/api/payees");

        // Assert
        await Assert.That(stranger.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The survival check is not redundant with the 404. A handler that wrote the row and only
        // then reported it missing would satisfy the status code alone.
        await Assert.That(payeesAfterStranger["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payeesAfterStranger["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));

        // The 204 for the owner on that very same id is what makes the 404 above mean "the budget
        // query filter hid it". An unmapped route answers 404 for every caller alike, so without a
        // success on the same id the cross-budget assertion would hold for a route that does not
        // exist at all — which is exactly the state of the code this test was written against.
        await Assert.That(owner.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(payeesAfterOwner["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks Reserve"));
    }

    [Test]
    public async Task PatchPayee_WithANameAnotherBudgetUses_ReturnsNoContentAndLeavesThatBudgetAlone()
    {
        // Arrange — the unique index is on (budget_id, name_key), so two budgets may each hold a payee
        // indexing to "Starbucks". Budget A's rename must not be judged against budget B's rows.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
        Guid payeeA = await CreatePayeeAsync(clientA, "Starbux");
        await CreatePayeeAsync(clientB, "Starbucks");

        // Act
        HttpResponseMessage patch = await RenamePayeeAsync(clientA, payeeA, "Starbucks");
        JsonNode payeesA = await GetJsonAsync(clientA, "/api/payees");
        JsonNode payeesB = await GetJsonAsync(clientB, "/api/payees");

        // Assert — each budget ends up with its own "Starbucks", two distinct rows carrying the same
        // blind index. That the two indexes really are equal is what makes this a test of the
        // budget_id leg: SealedNarrative.BlindIndex is deterministic in its label.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(payeesA["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payeesA["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
        await Assert.That(payeesB["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payeesB["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Starbucks"));
        await Assert.That(payeesB["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>()).IsNotEqualTo(payeeA);
    }

    [Test]
    public async Task PatchPayee_RenamesThePayeeOnEveryTransactionThatAlreadyNamedIt()
    {
        // Arrange — two past transactions pointing at the same payee.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid payeeId = await CreatePayeeAsync(client, "Starbux");
        await PostTransactionAsync(client, payeeId);
        JsonNode second = await PostTransactionAsync(client, payeeId);

        // Act
        HttpResponseMessage patch = await RenamePayeeAsync(client, payeeId, "Starbucks");
        JsonNode transactions = await GetJsonAsync(client, "/api/transactions");

        // Assert — retroactivity is the intended behaviour of a rename, not an accident of how
        // TransactionDto is projected. A payee is one counterparty over time, so correcting its name
        // corrects every transaction that ever named it; a rename that only applied going forward
        // would leave the ledger showing two counterparties where there is one, which is what makes
        // this rule the difference between a meaningful rename and a cosmetic one.
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(transactions["items"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(transactions["items"]!.AsArray().All(node =>
            node!["payeeName"]!.GetValue<string>() == SealedNarrative.EncodedName("Starbucks")))
            .IsTrue();
        await Assert.That(transactions["items"]!.AsArray()
            .All(node => node!["payeeId"]!.GetValue<Guid>() == payeeId)).IsTrue();
        await Assert.That(second["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeId);
    }

    /// <summary>
    /// <c>GET /api/payees</c> comes back in creation order, proved against rows whose instants run
    /// backwards against the order they were written in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The seed is the whole test.</b> A payee written over HTTP takes its <c>created_at_utc</c>
    /// from the handler's clock at the moment of the request, and its id from the request body — this
    /// slice removed <see cref="Guid.CreateVersion7()" /> from <c>Payee</c>, and production mints the
    /// value in the browser, where it is the associated data the envelope beside it was sealed against.
    /// The <see cref="Guid.CreateVersion7()" /> below is <see cref="CreatePayeeAsync" />'s, this file's
    /// own helper standing in for that browser. Because it is v7, its leading 48 bits are a clock, so
    /// rows seeded the ordinary way arrive with insertion order, id order and creation order all
    /// pointing the same way, and <c>ORDER BY id</c> and <c>ORDER BY created_at_utc, id</c> emit
    /// BYTE-IDENTICAL arrays. An assertion written over that seed certifies nothing: it is satisfied by
    /// an id-only ordering, and usually by an implementation with no <c>OrderBy</c> at all, because
    /// PostgreSQL hands a freshly-filled heap back in insertion order.
    /// </para>
    /// <para>
    /// So the ids stay ascending and the INSTANTS are inverted afterwards, out of band. The rewrite
    /// goes over the container superuser connection and not the application's:
    /// <c>created_at_utc</c> is absent from the payees <c>GRANT UPDATE</c> column list by design, so
    /// the app role answers <c>42501</c> — a different failure wearing the same red.
    /// </para>
    /// <para>
    /// <b>Minting v4 ids to make an id-ordering fail would be the wrong fix.</b> It catches the wrong
    /// implementation for the wrong reason — random ids — and stops catching it the day somebody
    /// "corrects" the ids to v7. What is asserted here is that the ORDER FOLLOWS THE INSTANT, over ids
    /// this case ASSERTS ARE ASCENDING rather than assumes: the id order and the instant order are made
    /// to disagree, and the first block of assertions below pins that disagreement, so the seed
    /// certifies itself and nothing here rests on what any particular client happens to mint.
    /// </para>
    /// <para>
    /// <b>Dropping the <c>.ThenBy(Id)</c> tie-break is caught by nothing</b>, here or anywhere: it
    /// needs two payees claiming the same microsecond, and no route lets a caller choose an instant.
    /// That half is held by review. This case does not cover it and must not be read as though it did.
    /// </para>
    /// </remarks>
    [Test]
    public async Task GetPayees_OrdersByTheCreationInstantAndNotByInsertionOrder()
    {
        // Arrange — three payees through the route, so their ids are minted in insertion order.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid[] written =
        [
            await CreatePayeeAsync(client, "Ordered payee 0"),
            await CreatePayeeAsync(client, "Ordered payee 1"),
            await CreatePayeeAsync(client, "Ordered payee 2"),
        ];

        await using NpgsqlConnection elevated = new(host.ConnectionString);
        await elevated.OpenAsync();
        for (int index = 0; index < written.Length; index++)
        {
            // The inversion itself: the row written FIRST claims the LATEST instant.
            await using NpgsqlCommand stamp = new(
                "update payees set created_at_utc = @instant where id = @id",
                elevated);
            stamp.Parameters.AddWithValue("instant", InversionBaseInstant.AddMinutes(-index));
            stamp.Parameters.AddWithValue("id", written[index]);
            await stamp.ExecuteNonQueryAsync();
        }

        // Act
        JsonNode list = await GetJsonAsync(client, "/api/payees");
        Guid[] returned = [.. list["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>())];

        // Assert — the premise of the whole case, checked rather than assumed: read back by id under
        // PostgreSQL's uuid byte order, the three rows come out in the order they were written. Without
        // this line an accident in v7 minting could make the id order and the instant order agree
        // again, and the case below would go green while proving nothing.
        await Assert.That(await ReadPayeeIdsOrderedByIdAsync(elevated)).IsEqualTo(Join(written));

        // Reversed, because the instants were. An id-only ordering returns `written` and fails here;
        // so does an implementation with no ordering at all.
        //
        // COMPARED AS ONE JOINED STRING, and the shape is the assertion. IsEqualTo over two Guid[]
        // compares REFERENCES and fails even when the contents and the order agree, and the failure
        // message names IsEquivalentTo as the fix — whose default is CollectionOrdering.Any, which
        // passes on every permutation of three ids INCLUDING the insertion order this case exists to
        // rule out. Joining sidesteps both: order is inside the value being compared.
        await Assert.That(Join(returned)).IsEqualTo(Join(written.Reverse()));
    }

    /// <summary>
    /// Several simultaneous creates for one blind index leave exactly one payee.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server used to hold "one counterparty is one row" itself, by folding case and re-reading the
    /// table inside a find-or-create; the race that mechanism swallowed had a test of its own, and it
    /// died with the mechanism. What holds the property now is
    /// <c>IX_payees_budget_id_name_key</c> plus a client that computes the same digest for the same
    /// name — and a unique index is the only part of that this side can watch fire.
    /// </para>
    /// <para>
    /// <b>Every loser must be a 409 and not a 500.</b> The translation runs inside a
    /// <c>catch</c> matched on the index by NAME, so a race is the one situation where the SQLSTATE
    /// arrives from a statement the caller did not knowingly conflict with — and an unnamed catch, or
    /// none, turns "somebody else got there first" into a defect report.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PostPayee_RacedForOneBlindIndex_AnswersOneCreatedAndConflictsForTheRest()
    {
        // Arrange — five distinct identifiers, five distinct envelopes, one index.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

        // Act
        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(attempt => client.PostAsJsonAsync("/api/payees", new
            {
                id = Guid.CreateVersion7().ToString("D"),
                name = SealedNarrative.EncodedName($"Starbucks attempt {attempt}"),
                nameKey = SealedNarrative.EncodedIndex("Starbucks"),
            })));
        JsonNode list = await GetJsonAsync(client, "/api/payees");

        // Assert — the counts are stated as counts rather than as "at least one", because both halves
        // are the rule: two winners would mean the index refused nothing, and a loser answering
        // anything but 409 means the collision was not attributed.
        HttpStatusCode[] statuses = [.. responses.Select(response => response.StatusCode)];
        await Assert.That(statuses.Count(status => status is HttpStatusCode.Created)).IsEqualTo(1);
        await Assert.That(statuses.Count(status => status is HttpStatusCode.Conflict)).IsEqualTo(4);
        await Assert.That(list["items"]!.AsArray().Count).IsEqualTo(1);

        foreach (HttpResponseMessage response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// A zero-length name is refused with <c>23514</c> and never with <c>2202E</c>, on the way in and
    /// on the way through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>SchemaConstraintSnapshotTests</c> compares RENDERED TEXT, so swapping <c>substring</c> for
    /// <c>get_byte</c> moves a literal there — and a literal moving is a paste unless somebody reads
    /// why.</b> Nothing proved the SQLSTATE, which is the actual claim. <c>get_byte(name, 0)</c> RAISES
    /// on a zero-length <c>bytea</c> instead of answering false: <c>2202E</c> out of
    /// <c>byteaGetByte</c>, with no constraint name, no table and no failing row — nothing a
    /// <c>catch (PostgresException) when (SqlState is 23514)</c> will ever see.
    /// </para>
    /// <para>
    /// <b>WHICH constraint fires is deliberately not asserted.</b> PostgreSQL decides that by the
    /// constraint NAME, alphabetically, so a zero-length name answers the length check only because
    /// "length" sorts before "version" — an accident nobody chose. Pinning it would turn a rename into
    /// a failure. What <c>substring</c> buys is that no predicate on this column can raise, so the
    /// answer is <c>23514</c> under EVERY ordering, which is what is asserted.
    /// </para>
    /// <para>
    /// The UPDATE leg is not a duplicate of the INSERT leg: a CHECK is evaluated on both, and a value
    /// that is fatal rather than false on one path is fatal on the other.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Database_RefusesAZeroLengthPayeeName_WithACheckViolationRatherThanAFatal()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // A well-formed row first, so a refusal below cannot be the insert shape being wrong.
        Guid seeded = await InsertPayeeAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Corner Shop").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Corner Shop").ToArray());

        // Act
        PostgresException onInsert = await ThrowsPostgresExceptionAsync(() => InsertPayeeAsync(
            connection,
            budgetId,
            [],
            SealedNarrative.BlindIndex("Blank name").ToArray()));

        PostgresException onUpdate = await ThrowsPostgresExceptionAsync(async () =>
        {
            await using NpgsqlCommand blank = new(
                "update payees set name = @name where id = @id", connection);
            blank.Parameters.AddWithValue("name", Array.Empty<byte>());
            blank.Parameters.AddWithValue("id", seeded);
            await blank.ExecuteNonQueryAsync();
        });

        // Assert — 23514 is the whole claim, and it is what 2202E is not.
        await Assert.That(onInsert.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(onUpdate.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);

        // A constraint name, a table and a failing row all arrive — the three things a raise carries
        // none of. Asserted as membership in the pair the alphabet may choose between, never as one.
        string[] namesTheAlphabetMayChoose = ["CK_payees_name_length", "CK_payees_name_version"];
        await Assert.That(namesTheAlphabetMayChoose).Contains(onInsert.ConstraintName!);
        await Assert.That(namesTheAlphabetMayChoose).Contains(onUpdate.ConstraintName!);
        await Assert.That(onInsert.TableName).IsEqualTo("payees");
    }

    /// <summary>
    /// A blind index of any width but exactly 32 bytes is refused by
    /// <c>CK_payees_name_key_length</c>.
    /// </summary>
    /// <remarks>
    /// <b>31 AND 33, because one of them alone measures a ceiling rather than a width.</b> The
    /// constraint is written <c>= 32</c> and <c>SchemaConstraintSnapshotTests</c> pins that as text —
    /// but the constraint was never fired, so <c>&lt;= 32</c> shipped green in every sense that
    /// matters: it refuses 33 exactly as the equality does, and admits a 31-byte digest that stores,
    /// reads back, keys perfectly, never collides and matches no payee the client will ever look for.
    /// Nothing on this side can recompute it to notice, because the index key lives in a browser.
    /// </remarks>
    [Test]
    [Arguments(31)]
    [Arguments(33)]
    public async Task Database_RefusesABlindIndexThatIsNotExactlyThirtyTwoBytes(int width)
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Non-vacuity, and it has to come first: the same insert with the one legal width goes
        // through, so the refusal below is about the width and not about the statement.
        await InsertPayeeAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Corner Shop").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Corner Shop").ToArray());

        // Act — the envelope beside it is well-formed, or CK_payees_name_length would answer first and
        // this case would pass on the wrong refusal.
        PostgresException refusal = await ThrowsPostgresExceptionAsync(() => InsertPayeeAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Wrong width").Envelope.ToArray(),
            new byte[width]));

        // Assert — the constraint is named beside the SQLSTATE because every other check on this table
        // raises 23514 as well, and the case would otherwise pass on the wrong rejection.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo("CK_payees_name_key_length");
    }

    /// <summary>
    /// The other two rules on <c>payees.name</c> fired, rather than merely rendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neither of these is reachable through the API, and that is why they are written raw.</b>
    /// <c>CiphertextEnvelopeText</c> refuses an over-cap envelope and a version this deployment has
    /// never implemented before either reaches a row, so a route-level case measures the Application
    /// ring's copy of the rule and says nothing about the column's. Weaken the constraint to
    /// <c>length(name) &gt;= 29</c> or delete the version check and every route case stays green;
    /// <c>SchemaConstraintSnapshotTests</c> moves a literal, and a literal moving is a paste unless
    /// somebody reads why.
    /// </para>
    /// <para>
    /// The constraint names ARE asserted here, unlike on the zero-length case: each of these values
    /// violates exactly one check, so which one PostgreSQL reports is decided by the value rather than
    /// by the alphabet.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// The floor argument is the one a reader will think the zero-length case already covers, and it
    /// does not. A zero-length name violates the floor <em>and</em> the version rule, so it stays
    /// refused under a floor of 1 — only a well-versioned envelope ONE BYTE short can tell 29 from any
    /// smaller number, and 29 is where <c>CiphertextEnvelope.MinimumLength</c> puts it because a
    /// version, a nonce and a tag over an empty plaintext is the shortest thing the framing can
    /// produce.
    /// </remarks>
    [Test]
    [Arguments("one byte under the framing floor", "CK_payees_name_length")]
    [Arguments("one byte over the column's cap", "CK_payees_name_length")]
    [Arguments("a version this deployment has never implemented", "CK_payees_name_version")]
    public async Task Database_RefusesANameTheEnvelopeRulesForbid(string shape, string constraint)
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Non-vacuity: the same statement with a well-formed envelope goes through.
        await InsertPayeeAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Corner Shop").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Corner Shop").ToArray());

        // Act — the index beside it is exactly 32 bytes, or CK_payees_name_key_length would answer
        // first and this case would pass on the wrong refusal.
        PostgresException refusal = await ThrowsPostgresExceptionAsync(() => InsertPayeeAsync(
            connection,
            budgetId,
            Base64UrlText.Decode(MalformedEnvelope(shape)),
            SealedNarrative.BlindIndex(shape).ToArray()));

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(constraint);
    }

    /// <summary>
    /// The instant the ordering case counts backwards from, far enough in the past that no row written
    /// by an ordinary request can land between two of its stamps.
    /// </summary>
    private static readonly DateTime InversionBaseInstant =
        new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Identifiers as one ordered value, so an assertion about them is an assertion about their order.
    /// </summary>
    private static string Join(IEnumerable<Guid> ids) => string.Join(", ", ids);

    private static async Task<string> ReadPayeeIdsOrderedByIdAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new("select id from payees order by id", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<Guid> ids = [];
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return Join(ids);
    }

    /// <summary>
    /// Writes one payee as raw SQL, which is the only way to reach the column rules: the domain and
    /// the route refuse the same values client-side, so an EF insert never gets to PostgreSQL and
    /// would prove nothing about the constraint.
    /// </summary>
    private static async Task<Guid> InsertPayeeAsync(
        NpgsqlConnection connection,
        Guid budgetId,
        byte[] name,
        byte[] nameKey)
    {
        var id = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            """
            insert into payees (id, budget_id, name, name_key, created_at_utc)
            values (@id, @budget_id, @name, @name_key, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("budget_id", budgetId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("name_key", nameKey);
        command.Parameters.AddWithValue("created_at_utc", SchemaSeedInstant);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(Func<Task> statement)
    {
        try
        {
            await statement();
        }
        catch (PostgresException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected PostgresException.");
    }

    /// <summary>
    /// Fixed UTC instant for the rows the schema cases write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SchemaSeedInstant =
        new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// A <c>name</c> member this route must refuse, one shape per argument of
    /// <see cref="PostPayee_WithAnUnacceptableNameEnvelope_IsRejectedNamingTheName" />.
    /// </summary>
    /// <remarks>
    /// Built from the constants that own the rules rather than from literals, so a floor, a cap or a
    /// version that moves moves these values with it instead of leaving a case measuring a number
    /// nobody uses any more.
    /// </remarks>
    private static string MalformedEnvelope(string shape)
    {
        switch (shape)
        {
            case "not base64url":
                return "not an envelope";

            case "one byte under the framing floor":
                {
                    byte[] tooShort = new byte[CiphertextEnvelope.MinimumLength - 1];
                    tooShort[0] = CiphertextEnvelope.Version;
                    return Base64UrlText.Encode(tooShort);
                }

            case "a version this deployment has never implemented":
                {
                    byte[] wrongVersion = new byte[CiphertextEnvelope.MinimumLength];
                    wrongVersion[0] = CiphertextEnvelope.Version + 1;
                    return Base64UrlText.Encode(wrongVersion);
                }

            case "one byte over the column's cap":
                {
                    byte[] tooLong = new byte[NarrativeFieldLimits.NameBytes + 1];
                    tooLong[0] = CiphertextEnvelope.Version;
                    return Base64UrlText.Encode(tooLong);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown envelope shape.");
        }
    }

    /// <summary>
    /// A <c>nameKey</c> member this route must refuse, one shape per argument of
    /// <see cref="PostPayee_WithAnUnacceptableBlindIndex_IsRejectedNamingTheIndex" />.
    /// </summary>
    private static string MalformedBlindIndex(string shape) => shape switch
    {
        "not base64url" => "not an index",
        "one byte short of the width" =>
            Base64UrlText.Encode(new byte[IndexedName.BlindIndexLength - 1]),
        "one byte over the width" =>
            Base64UrlText.Encode(new byte[IndexedName.BlindIndexLength + 1]),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown index shape."),
    };

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path) =>
        (await JsonNode.ParseAsync(await client.GetStreamAsync(path)))!;

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
    /// Re-seals a payee's name under <paramref name="label" /> and sends both halves in one body.
    /// </summary>
    private static Task<HttpResponseMessage> RenamePayeeAsync(
        HttpClient client,
        Guid payeeId,
        string label) =>
        client.PatchAsJsonAsync($"/api/payees/{payeeId}", new
        {
            name = SealedNarrative.EncodedName(label),
            nameKey = SealedNarrative.EncodedIndex(label),
        });

    private static async Task<JsonNode> PostTransactionAsync(HttpClient client, Guid payeeId)
    {
        HttpResponseMessage response = await PostTransactionResponseAsync(client, payeeId);
        response.EnsureSuccessStatusCode();
        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
    }

    private static async Task<HttpResponseMessage> PostTransactionResponseAsync(
        HttpClient client,
        Guid payeeId)
    {
        Guid accountId = await CreateAccountAsync(client);
        return await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-24",
            accountId,
            description = "Coffee",
            payeeId,
        });
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
