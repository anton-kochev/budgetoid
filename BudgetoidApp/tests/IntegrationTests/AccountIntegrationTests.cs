using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Accounts;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using TestSupport;
using TUnit.Assertions.Enums;

namespace IntegrationTests;

public sealed class AccountIntegrationTests
{
    /// <summary>
    /// Minor unit of the USD rows this file seeds out of band. Precision is not what any case here is
    /// about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// The instant the ordering case counts backwards from, far enough in the past that no row written
    /// by an ordinary request can land between two of its stamps.
    /// </summary>
    private static readonly DateTime InversionBaseInstant = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task AccountsCrud_WorksThroughApiAndUsesStringEnum()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();

        Guid id = Guid.CreateVersion7();
        HttpResponseMessage create = await client.PostAsJsonAsync("/api/accounts", CreateBody(id, "Checking"));
        JsonNode created = (await JsonNode.ParseAsync(await create.Content.ReadAsStreamAsync()))!;

        JsonNode listAfterCreate = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;
        HttpResponseMessage update = await client.PutAsJsonAsync($"/api/accounts/{id}", UpdateBody("Savings", "Savings"));
        JsonNode listAfterUpdate = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;
        HttpResponseMessage delete = await client.DeleteAsync($"/api/accounts/{id}");
        JsonNode listAfterDelete = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;

        // THE ID COMES BACK IN THE SPELLING IT WAS SENT, which is the whole reason the create body
        // carries one. It is the associated data the name was sealed against, so a route that minted
        // its own — or that folded the spelling on the way through — produces a name no client can
        // ever open, with a 201 and a well-formed row to show for it.
        //
        // The name is asserted as the base64url envelope and no longer as the word "Checking". It used
        // to be sent as "  Checking  " and asserted back as "Checking", pinning a trim; there is
        // nothing left to trim, because this server never sees the text.
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created["id"]!.GetValue<string>()).IsEqualTo(id.ToString("D"));
        await Assert.That(created["name"]!.GetValue<string>()).IsEqualTo(EncodedName("Checking"));
        await Assert.That(created["type"]!.GetValue<string>()).IsEqualTo("Checking");
        await Assert.That(created["openingBalance"]!.GetValue<decimal>()).IsEqualTo(100m);
        await Assert.That(created["currencyCode"]!.GetValue<string>()).IsEqualTo("USD");
        await Assert.That(created["currencyName"]!.GetValue<string>()).IsEqualTo("US Dollar");
        await Assert.That(created["currencySymbol"]!.GetValue<string>()).IsEqualTo("$");

        // NO BLIND INDEX ON ANY READ, and its absence is a decision rather than an omission. A client
        // recomputes the index from the name it just decrypted; returning it would hand every caller a
        // deterministic, per-account fingerprint of a name — the one property of the pair that survives
        // having no key.
        await Assert.That(created["nameKey"]).IsNull();

        await Assert.That(listAfterCreate["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(update.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(listAfterUpdate["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(EncodedName("Savings"));
        await Assert.That(listAfterUpdate["items"]!.AsArray()[0]!["nameKey"]).IsNull();
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(listAfterDelete["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task CreateAccount_WithANonCanonicalId_IsRejected()
    {
        // Arrange — CanonicalIdentifierTests covers the parser; NOTHING PROVED IT WAS WIRED INTO THIS
        // ROUTE. A handler that never called it, or a command that bound Id as a Guid instead of a
        // string, passes every other case in this file: System.Text.Json folds all four spellings below
        // to one value before a handler sees text, so the refusal becomes unwritable and the 201 looks
        // identical. It fails in a browser months later as a name that will not open.
        //
        // The set is DERIVED from one identifier rather than typed out, so it cannot drift from what
        // Guid can render: the same value in upper case, braced, unhyphenated, and with surrounding
        // whitespace — each a different text on the wire and the same Guid in the row.
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();

        var canonical = Guid.CreateVersion7();
        string[] refused =
        [
            canonical.ToString("D").ToUpperInvariant(),
            canonical.ToString("B"),
            canonical.ToString("N"),
            $" {canonical.ToString("D")} ",

            // Canonically SPELLED and refused anyway, for its own reason: the all-zero uuid is what an
            // unset field sends, and it arrives indistinguishable from a value somebody chose. It is in
            // this list to keep a reader from concluding the route only judges spelling.
            Guid.Empty.ToString("D"),
        ];

        // Act
        List<string> accepted = [];
        List<string> misattributed = [];
        foreach (string spelling in refused)
        {
            HttpResponseMessage response =
                await client.PostAsJsonAsync("/api/accounts", CreateBody(spelling, "Checking"));
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

        // Non-vacuity: the canonical spelling of the same value is accepted, so the case above is
        // measuring the spelling and not a route that refuses every create.
        HttpResponseMessage good =
            await client.PostAsJsonAsync("/api/accounts", CreateBody(canonical, "Checking"));
        await Assert.That(good.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task CreateAccount_WithEveryOpaqueMemberMalformed_ReportsAllOfThemAtOnce()
    {
        // Arrange — NOTHING SENT TWO MALFORMED MEMBERS AT ONCE, so a fail-fast handler that reported
        // only the first was green everywhere. The handler claims all three are attempted and all three
        // reported; this is the only shape of request that can tell those apart.
        //
        // The three are produced by one piece of client code and are opaque to this server in the same
        // way, so a caller that got them all wrong learns about the second only after fixing the first
        // — three round trips to correct one broken client.
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            id = Guid.CreateVersion7().ToString("D").ToUpperInvariant(),
            name = "not an envelope",
            nameKey = "not an index",
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        JsonNode? problem = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        // Assert — all three, in one refusal. A handler that returned after the first carries exactly
        // one of these, so the COUNT is what makes this case impossible for it to pass: any single
        // key-presence assertion would be satisfied by whichever member it happened to stop on.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!.AsObject().Count).IsEqualTo(3);
        await Assert.That(problem["errors"]!["Id"] is not null).IsTrue();
        await Assert.That(problem["errors"]!["Name"] is not null).IsTrue();
        await Assert.That(problem["errors"]!["NameKey"] is not null).IsTrue();
    }

    [Test]
    public async Task UpdateAccount_WithBothOpaqueMembersMalformed_ReportsBothAtOnce()
    {
        // Arrange — the update leg's half of the case above. It carries two opaque members rather than
        // three: the route parameter stays a uuid, because a rename re-seals against the row's EXISTING
        // id, which the client read back from this API in the one form Guid renders.
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();
        Guid id = await CreateAccountAsync(client, "Checking");

        // Act
        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/accounts/{id}", new
        {
            name = "not an envelope",
            nameKey = "not an index",
            type = "Savings",
            openingBalance = 0m,
        });
        JsonNode? problem = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!.AsObject().Count).IsEqualTo(2);
        await Assert.That(problem["errors"]!["Name"] is not null).IsTrue();
        await Assert.That(problem["errors"]!["NameKey"] is not null).IsTrue();
    }

    /// <summary>
    /// The account list comes back in creation order, proved against rows whose instants run backwards
    /// against the order they were written in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The seeding is the test.</b> A row written over HTTP takes its <c>created_at_utc</c> from the
    /// clock at the moment of the request and its id from <c>Guid.CreateVersion7</c>, whose leading 48
    /// bits are that same clock — so rows seeded the ordinary way arrive with insertion order, id order
    /// and creation order all pointing the same way. An assertion written about any one of them is
    /// satisfied by all three, and by an implementation with no <c>OrderBy</c> at all, because
    /// PostgreSQL usually hands a freshly-filled heap back in insertion order.
    /// </para>
    /// <para>
    /// So the rows go in out of band with their instants <b>inverted</b>: the row written first is
    /// stamped latest. This is the shape
    /// <c>DataExportCompletenessTests.Export_OrdersEachCollectionByCreationRatherThanByInsertionOrder</c>
    /// uses, for the same reason, and the seeding cannot go through the endpoints because nothing in
    /// the product lets a caller choose the instant a row claims — and must not.
    /// </para>
    /// <para>
    /// <b>Ordering by name is not merely unused here, it is unavailable</b>, which is why nothing below
    /// mentions one. <c>accounts.name</c> is bytea, so an ordering over it sorts by the first differing
    /// byte — the nonce, after the version — freshly drawn on every seal. That is stable within a read
    /// and reshuffled by every save, so a list would silently reorder itself when an unrelated account
    /// was renamed. Only the client holds the text, so only the client can sort by it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ListAccounts_OrdersByCreationRatherThanByInsertionOrder()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, Guid budgetId) = await host.Factory.CreateSignedInClientAsync();
        IReadOnlyList<Guid> written = await SeedWithCreationOrderInvertedAsync(host, budgetId);

        // Act
        JsonNode list = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;
        Guid[] returned = [.. list["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>())];

        // Assert — non-vacuity first: three distinct ids means the seeding really wrote three rows.
        await Assert.That(written.Distinct().Count()).IsEqualTo(3);

        // Reversed, because the instants were. An implementation returning rows in the order it found
        // them fails here, and so does one ordering by id: Guid.CreateVersion7 is not monotonic within
        // a millisecond, but these ids are minted in insertion order, which is the order this asserts
        // AGAINST.
        //
        // CollectionOrdering.Matching IS THE ASSERTION, and the argument is spelled out because the
        // obvious tidy-up deletes the test. IsEqualTo over two Guid[] compares REFERENCES and fails
        // even when the contents and the order agree, which TUnit's own message says; the failure
        // names IsEquivalentTo as the fix, and IsEquivalentTo DEFAULTS TO CollectionOrdering.Any.
        // Taking that suggestion unqualified leaves a case called OrdersByCreation that passes on any
        // permutation of the three ids — including the insertion order it exists to rule out — and
        // nothing goes red. Order is the whole subject here, so the ordering has to be named.
        await Assert.That(returned)
            .IsEquivalentTo(written.Reverse().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task UserCannotCreateTransactionWithAnotherUsersAccountId()
    {
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        (HttpClient clientA, _, _) = await factoryA.CreateSignedInClientAsync();
        (HttpClient clientB, _, _) = await factoryB.CreateSignedInClientAsync();
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
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();
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
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();
        await CreateAccountAsync(client, "Checking");

        // THE SAME RULE ENFORCED OVER A VALUE THE DATABASE CANNOT READ. Uniqueness is over
        // (budget_id, name_key) now, so a duplicate is a repeated BLIND INDEX rather than a repeated
        // name — every seal draws a fresh nonce, so two rows holding one name hold different envelopes,
        // and an index over `name` would enforce nothing.
        //
        // This case used to send "checking" against a seeded "Checking" and rely on the column's
        // case_insensitive collation. That collation is gone by force — bytea is not collatable — and
        // what it did MOVED rather than disappeared: case folding is part of the normalisation the
        // client applies before it computes the HMAC. This server cannot check that it happened, so
        // nothing here can test it; the fixture derives both halves from one label, which is the
        // client's behaviour and the only thing this side can observe.
        HttpResponseMessage duplicate =
            await client.PostAsJsonAsync("/api/accounts", CreateBody(Guid.CreateVersion7(), "Checking"));
        JsonNode? problem = await JsonNode.ParseAsync(await duplicate.Content.ReadAsStreamAsync());

        // Reported against Name and not NameKey, because the person typed a name and has never heard of
        // an index.
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["Name"] is not null).IsTrue();
    }

    /// <summary>
    /// The same create sent twice, byte for byte, answers <b>409</b> and says so as an
    /// <i>identifier</i> collision — not the 400 the same name would earn under a fresh identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the ordinary behaviour of an HTTP client, and it used to answer 500.</b> The
    /// identifier is minted by the caller — it is the associated data the name envelope was sealed
    /// against — so a POST retried after a network timeout carries a body identical to the first one,
    /// down to the byte, and lands on <c>PK_accounts</c>. Nothing in the product tells the client to
    /// change it, and nothing should: the second request is a repeat of the first.
    /// </para>
    /// <para>
    /// <b>The detail sentence is the assertion, not the status.</b> A 409 alone is satisfied by an
    /// implementation that reached for the wrong conflict; <c>ConflictExceptionHandler</c> writes one
    /// fixed title for every conflict in the product and adds no extension member, so the sentence is
    /// the whole of what a caller is told and the whole of what carries the remedy.
    /// </para>
    /// <para>
    /// <b>And a 400 is asserted against explicitly,</b> because this row breaks the name index as well
    /// as the key and the account routes answer a duplicate <i>name</i> with a 400 keyed on
    /// <c>Name</c>. Which of the two the caller gets is decided by which constraint PostgreSQL names,
    /// and the answer is the key — measured, and written out on
    /// <c>PayeeConfiguration.PrimaryKeyName</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CreateAccount_RetriedByteForByte_AnswersConflictNamingTheIdentifier()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();
        var id = Guid.CreateVersion7();
        object body = CreateBody(id, "Checking");
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/accounts", body);

        // Act — the identical object, sent again.
        HttpResponseMessage retry = await client.PostAsJsonAsync("/api/accounts", body);
        JsonNode problem = (await JsonNode.ParseAsync(await retry.Content.ReadAsStreamAsync()))!;
        JsonNode list = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;

        // Assert
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(problem["title"]!.GetValue<string>())
            .IsEqualTo("The request conflicts with the current state of the resource.");
        await Assert.That(problem["detail"]!.GetValue<string>()).IsEqualTo(
            "An account already exists with this identifier. If this request is a retry, read that "
            + "account back by its identifier instead of posting it again; otherwise mint a fresh "
            + "identifier and post again.");

        // No field errors, and that is the shape as well as the status: a duplicate name here would
        // have produced a problem document keyed on Name, asking the person to edit a name they typed
        // correctly and did not resend by choice.
        await Assert.That(problem["errors"]).IsNull();

        // The retry wrote nothing — one account, the one the first request created.
        await Assert.That(list["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(list["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>()).IsEqualTo(id);
    }

    /// <summary>
    /// One taken name, two answers, decided by whether the identifier beside it is fresh: <b>400</b>
    /// keyed on <c>Name</c>, or <b>409</b> naming the identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the trap, and nothing else in the suite can see it.</b>
    /// <see cref="CreateAccount_WithDuplicateName_IsRejected" /> mints a fresh identifier for its
    /// second create — correctly, and silently: nothing in it says the freshness is load-bearing. Inline
    /// that identifier, or reuse the first account's while editing the case later, and the same
    /// duplicate name answers 409 instead, because the primary key is the constraint PostgreSQL names
    /// when a row breaks both. Every assertion in that case is about a 400, so it would go red without
    /// saying why, and the natural repair is to change the expected status — which deletes the
    /// field-keyed refusal a person actually needs.
    /// </para>
    /// <para>
    /// <b>The asymmetry between the two tables survives this case rather than being flattened by it.</b>
    /// A duplicate name is a 400 on accounts and a 409 on payees; that is argued where the two
    /// repositories are written, and this case pins only the account side. What both tables now share
    /// is the identifier arm, and it is the one that wins when a row breaks both rules.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CreateAccount_WithATakenName_AnswersBadRequestOnlyUnderAFreshIdentifier()
    {
        // Arrange — one account to collide against.
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();
        var takenId = Guid.CreateVersion7();
        HttpResponseMessage seeded = await client.PostAsJsonAsync("/api/accounts", CreateBody(takenId, "Checking"));

        // Act — the same name twice. The only difference between the two bodies is the identifier: the
        // first is one nothing holds, the second is the seeded account's own.
        HttpResponseMessage underAFreshId =
            await client.PostAsJsonAsync("/api/accounts", CreateBody(Guid.CreateVersion7(), "Checking"));
        HttpResponseMessage underTheTakenId =
            await client.PostAsJsonAsync("/api/accounts", CreateBody(takenId, "Checking"));
        JsonNode freshProblem =
            (await JsonNode.ParseAsync(await underAFreshId.Content.ReadAsStreamAsync()))!;

        // Assert — the name index alone, reported as a correction to the field the person typed.
        await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(underAFreshId.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(freshProblem["errors"]!["Name"] is not null).IsTrue();

        // The key and the index together, reported as the key. Same name, same budget, same route —
        // and a different status, which is the whole point of writing the two side by side.
        await Assert.That(underTheTakenId.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await DetailOfAsync(underTheTakenId)).IsEqualTo(
            "An account already exists with this identifier. If this request is a retry, read that "
            + "account back by its identifier instead of posting it again; otherwise mint a fresh "
            + "identifier and post again.");
    }

    /// <summary>
    /// A create reusing an identifier under a <i>different</i> name answers <b>409</b> with the
    /// identifier sentence — the case that separates the two arms.
    /// </summary>
    /// <remarks>
    /// <b>Only the primary key is broken here,</b> which is what the route answered <b>500</b> on
    /// before the arm existed: no <c>catch</c> named <c>PK_accounts</c>, so the
    /// <c>DbUpdateException</c> reached the global handler. The name is one nothing in this budget
    /// holds, so an implementation that matched the name index and nothing else has no answer to give,
    /// and one that answered the duplicate-name 400 would key the refusal on a name that is free.
    /// </remarks>
    [Test]
    public async Task CreateAccount_ReusingAnIdentifierUnderAnotherName_AnswersConflictNamingTheIdentifier()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();
        var id = Guid.CreateVersion7();
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/accounts", CreateBody(id, "Checking"));

        // Act — same identifier, a name nothing in this budget holds.
        HttpResponseMessage conflict = await client.PostAsJsonAsync("/api/accounts", CreateBody(id, "Savings"));
        JsonNode problem = (await JsonNode.ParseAsync(await conflict.Content.ReadAsStreamAsync()))!;
        JsonNode list = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/accounts")))!;

        // Assert
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(problem["detail"]!.GetValue<string>()).IsEqualTo(
            "An account already exists with this identifier. If this request is a retry, read that "
            + "account back by its identifier instead of posting it again; otherwise mint a fresh "
            + "identifier and post again.");
        await Assert.That(problem["errors"]).IsNull();

        // Nothing was written and nothing was renamed: the row keeps the name it was created with.
        await Assert.That(list["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(list["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo(EncodedName("Checking"));
    }

    [Test]
    public async Task RenameAccount_ToExistingName_IsRejected()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();
        await CreateAccountAsync(client, "Checking");
        Guid savingsId = await CreateAccountAsync(client, "Savings");

        HttpResponseMessage rename =
            await client.PutAsJsonAsync($"/api/accounts/{savingsId}", UpdateBody("Checking", "Savings"));
        JsonNode? problem = await JsonNode.ParseAsync(await rename.Content.ReadAsStreamAsync());

        await Assert.That(rename.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["Name"] is not null).IsTrue();
    }

    /// <summary>
    /// Writes three accounts whose creation instants run backwards against the order they are written
    /// in, on the container superuser connection.
    /// </summary>
    /// <remarks>
    /// Through the domain factory rather than as raw <c>INSERT</c>s, so each row satisfies every rule
    /// the application would have applied. Superuser because <c>budget_isolation</c> is <c>FOR ALL</c>
    /// and this connection carries no ambient budget — and because the application role is not granted
    /// a way to write a row with an instant of the caller's choosing in the first place.
    /// </remarks>
    private static async Task<IReadOnlyList<Guid>> SeedWithCreationOrderInvertedAsync(
        PostgresTestHost host,
        Guid budgetId)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);

        List<Guid> written = [];
        for (int index = 0; index < 3; index++)
        {
            Account account = Account.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed($"Ordered account {index}"),
                AccountType.Checking,
                0m,
                "USD",
                UsdMinorUnit,

                // The inversion itself: the row written first claims the latest instant.
                InversionBaseInstant.AddMinutes(-index));
            db.Accounts.Add(account);
            written.Add(account.Id);
        }

        await db.SaveChangesAsync();
        return written;
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client, string label)
    {
        var id = Guid.CreateVersion7();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", CreateBody(id, label));
        response.EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>
    /// A well-formed create body. Both halves of the name come from one label, the way a client derives
    /// them from one text, and the id is sent in the one spelling this API accepts.
    /// </summary>
    private static object CreateBody(Guid id, string label) => CreateBody(id.ToString("D"), label);

    /// <summary>
    /// The same body with the identifier as raw text, for the cases whose subject is the spelling.
    /// </summary>
    private static object CreateBody(string id, string label) => new
    {
        id,
        name = EncodedName(label),
        nameKey = EncodedIndex(label),
        type = "Checking",
        openingBalance = 100m,
        currencyCode = "USD",
    };

    /// <summary>
    /// The <c>detail</c> member of a problem document, as text.
    /// </summary>
    /// <remarks>
    /// Non-null on purpose: every case that calls this is about <i>which</i> sentence came back, so a
    /// response carrying no detail is a failure of that case rather than a value worth comparing.
    /// </remarks>
    private static async Task<string> DetailOfAsync(HttpResponseMessage response)
    {
        JsonNode problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return problem["detail"]!.GetValue<string>();
    }

    private static object UpdateBody(string label, string type) => new
    {
        name = EncodedName(label),
        nameKey = EncodedIndex(label),
        type,
        openingBalance = 25m,
    };

    // The wire spelling of each half: unpadded base64url, the one alphabet every binary member of this
    // API crosses JSON in. Forwarded to the fixture rather than re-encoded here, because a dozen other
    // files in this assembly now write the same two members and a per-file encoder is a per-file chance
    // to reach for System.Text.Json's padded standard base64 instead.
    private static string EncodedName(string label) => SealedNarrative.EncodedName(label);

    private static string EncodedIndex(string label) => SealedNarrative.EncodedIndex(label);

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
