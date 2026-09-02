using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Budgets;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That an account owning more than the budget its request operates inside is refused an export, and
/// that the refusal hands back nothing of the document it would not assemble.
/// </summary>
/// <remarks>
/// <para>
/// The decision itself is unit-tested in <c>ExportDataHandlerTests</c>, where both directions of the
/// set equality can be arranged. What only the real pipeline can show is the other half: <b>what the
/// caller receives</b>. A refusal that answered a truncated document with an error status attached
/// would be the exact defect the refusal exists to prevent, and a status assertion cannot see it —
/// which is why the body is searched for every id the account owns rather than merely checked for the
/// absence of a <c>budgets</c> key.
/// </para>
/// <para>
/// <b>500, and deliberately so.</b> <c>ExportCompletenessException</c> gets no
/// <c>IExceptionHandler</c> of its own, so <c>GlobalExceptionHandler</c> — the catch-all, registered
/// last — turns it into <c>application/problem+json</c> titled <c>"An unexpected error occurred."</c>.
/// Every alternative says something false: 404 claims the export does not exist when it is the server
/// that cannot assemble one, 400 blames a well-formed request with no field to correct, and 409
/// implies a resolution the client could perform when it can neither create, delete nor select a
/// budget. A named 5xx mapping is worse than all three — it is a seam a later reader can soften into
/// "return the ambient budget and a warning", which is the truncation itself.
/// </para>
/// <para>
/// The second budget goes in out of band on the container superuser, because no endpoint creates a
/// budget: an account is created with exactly one default budget, and nothing else in the product
/// writes that table. It carries a <b>name</b>, and that is load-bearing rather than
/// decorative — <c>IX_budgets_user_id_name</c> is <c>UNIQUE … NULLS NOT DISTINCT</c>, so a second
/// nameless budget for one owner is refused by the index and the test would be measuring the index
/// instead of the export. It is written through <c>BudgetoidDbContext</c> and the domain factory
/// rather than as raw SQL, the way <c>ErasureAtomicityTests.SeedIdentityRowsAsync</c> does, so the row
/// is one the application itself could have produced.
/// </para>
/// <para>
/// <b>Its instant is derived from the first budget's own <c>created_at_utc</c> and shifted
/// forward, never written here as a constant.</b> The property that has to hold is a relative one —
/// later than the account's own budget — because <c>IBudgetRepository.FindFirstForUserAsync</c> returns
/// the <b>earliest</b> budget an owner holds, and an instant in the past would quietly make the empty
/// seeded row the ambient one. Every furnished id would then live in a budget the export never reads,
/// and the eight-id body search below could not find one even against an implementation that
/// truncates. Deriving it makes that true by construction rather than by an unstated assumption about
/// where the system clock happens to be, and it keeps the seeded row one the application itself could
/// have produced.
/// </para>
/// <para>
/// <b>Both tests authenticate from a session cookie, over an account
/// <see cref="ApiFactory.CreateSignedInClientAsync" /> seeded whole.</b> Nothing here is about how a
/// request proves who is asking, and the export is a fallback-policy route like any other — what the
/// cookie buys is that the ids the refusal must not leak come back from the call that wrote them
/// instead of from a subject lookup that can match the wrong row.
/// </para>
/// <para>
/// <see cref="Export_ForAnOwnerOfTheProvisionedBudgetAlone_IsAnswered" /> is the control, and it is
/// the seeding that it controls for. Every step of the refusal's Arrange block except the second
/// budget runs there too, so a furnishing call that silently stopped creating rows, or an owner lookup
/// that resolved the wrong account, goes red as a failed success rather than passing the refusal for a
/// reason nobody chose — a 500 is, after all, what a broken seeder produces on its own.
/// </para>
/// <para>
/// <c>FurnishAccountAsync</c> is duplicated from <c>ErasureAtomicityTests</c> rather than extracted,
/// which is the local convention in this folder: three files already carry their own copy, and a
/// drive-by extraction across four is a change to those files rather than to this one.
/// </para>
/// </remarks>
public sealed class DataExportRefusalTests
{
    private const string ExportPath = "/api/me/export";

    private const string Subject = "export-refusal-subject";

    /// <summary>
    /// The title <c>GlobalExceptionHandler</c> writes for anything no earlier handler claims. Asserted
    /// as a literal because the handler holds it as one — a constant to read would first have to be
    /// added there, and this test has no standing to ask for it.
    /// </summary>
    private const string CatchAllTitle = "An unexpected error occurred.";

    [Test]
    public async Task Export_ForAnOwnerOfASecondBudget_IsRefusedWithoutABody()
    {
        // Arrange — a furnished account, then a second budget written out of band for the same owner.
        // The furnishing is what gives the leak search something to find: an account, a category group,
        // a category, a payee and a transaction, each with an id no refusal may name.
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);
        (HttpClient client, Guid userId, Guid budgetId) = signedIn;

        FurnishedIds furnished = await FurnishAccountAsync(host, client);
        Guid secondBudgetId = await SeedSecondBudgetAsync(
            host,
            userId,
            (await BudgetCreatedAtUtcAsync(host, budgetId)).AddMinutes(1));

        // Act
        HttpResponseMessage response = await client.GetAsync(ExportPath);
        string body = await response.Content.ReadAsStringAsync();

        // Assert — the status the catch-all produces, and the title beside it. The title is what says
        // the 500 came from an unclaimed exception rather than from a host that failed to boot or a
        // pipeline that fell over before reaching the endpoint.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.Content.Headers.ContentType?.MediaType)
            .IsEqualTo("application/problem+json");

        JsonNode problem = JsonNode.Parse(body)
            ?? throw new InvalidOperationException("The refusal answered an empty body.");
        await Assert.That(problem["title"]!.GetValue<string>()).IsEqualTo(CatchAllTitle);

        // And no export filename. The endpoint sets the disposition after the handler returns, so a
        // refusal carries none today — but that ordering is one line, and moving it above the call
        // would have a browser save an application/problem+json body under a name that says it is
        // somebody's finances. Nothing else in the suite would notice.
        await Assert.That(response.Content.Headers.Contains("Content-Disposition")).IsFalse();

        // No document, whole or partial. A refusal that answered the ambient budget's contents under an
        // error status would satisfy the status assertion above and be the very truncation this refusal
        // exists to prevent, so the shape is checked as well as the code.
        await Assert.That(problem["budgets"]).IsNull();
        await Assert.That(problem["schemaVersion"]).IsNull();
        await Assert.That(problem["user"]).IsNull();

        // And no id of anything the account owns, anywhere in the body — including the development
        // branch's echoed message, which is why the refusal's message must name counts and never ids.
        // A key-shaped assertion alone would pass over an id spelled into a detail string.
        Guid[] ownedIds =
        [
            userId,
            budgetId,
            secondBudgetId,
            furnished.AccountId,
            furnished.CategoryGroupId,
            furnished.CategoryId,
            furnished.PayeeId,
            furnished.TransactionId,
        ];

        // Non-vacuity, both halves. Eight distinct ids means the furnishing really wrote eight rows, and
        // a body carrying the title means the search below ran over a real response rather than over an
        // empty string that trivially contains nothing.
        await Assert.That(ownedIds.Distinct().Count()).IsEqualTo(8);
        await Assert.That(body).Contains(CatchAllTitle);

        foreach (Guid ownedId in ownedIds)
        {
            await Assert.That(body).DoesNotContain(ownedId.ToString());
        }
    }

    /// <summary>
    /// The control for the refusal above: the same account, furnished the same way, owning only the
    /// budget it was created with.
    /// </summary>
    /// <remarks>
    /// Without it the refusal is untrustworthy. A seeding step that quietly created nothing, a subject
    /// whose account never resolved, or an export that had stopped working for everybody all produce a
    /// 500 with no owned id in the body — the refusal's exact expectation. This is the test that says
    /// the seeding path can produce a success, so the one difference between the two Arrange blocks is
    /// really the second budget.
    /// </remarks>
    [Test]
    public async Task Export_ForAnOwnerOfTheProvisionedBudgetAlone_IsAnswered()
    {
        // Arrange — every line of the refusal's Arrange except the second budget.
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);
        (HttpClient client, Guid userId, Guid budgetId) = signedIn;

        FurnishedIds furnished = await FurnishAccountAsync(host, client);

        // Act
        HttpResponseMessage response = await client.GetAsync(ExportPath);
        string body = await response.Content.ReadAsStringAsync();

        // Assert — answered, and answered about the budget this owner actually holds. The id is what
        // makes it a control for the leak search rather than only for the status: it proves the search
        // in the refusal test is capable of finding an id when the body carries one.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonNode document = JsonNode.Parse(body)
            ?? throw new InvalidOperationException("The export answered an empty body.");
        await Assert.That(document["budgets"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(document["budgets"]![0]!["id"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(document["user"]!["id"]!.GetValue<Guid>()).IsEqualTo(userId);
        await Assert.That(body).Contains(budgetId.ToString());

        // Every furnished id, in the body, by the same search the refusal runs. This is what makes the
        // pair measure what its prose claims: the refusal asserts that eight ids are absent, and only a
        // success carrying those same ids says the search can find one at all. A distinctness guard over
        // five ids returned by five separate creations is true by construction and no production change
        // can break it — this is not.
        Guid[] furnishedIds =
        [
            furnished.AccountId,
            furnished.CategoryGroupId,
            furnished.CategoryId,
            furnished.PayeeId,
            furnished.TransactionId,
        ];

        foreach (Guid furnishedId in furnishedIds)
        {
            await Assert.That(body).Contains(furnishedId.ToString());
        }
    }

    /// <summary>
    /// The ids of the rows one furnished account owns — every one of them a value a refusal must not
    /// name.
    /// </summary>
    private sealed record FurnishedIds(
        Guid AccountId,
        Guid CategoryGroupId,
        Guid CategoryId,
        Guid PayeeId,
        Guid TransactionId);

    /// <summary>
    /// Writes one row into every budget-owned table over HTTP and returns the ids the assertions key
    /// on.
    /// </summary>
    /// <remarks>
    /// The money data goes in through the real endpoints, so every row is one the application itself
    /// could have written — same validation, same repositories, same least-privilege role. Nothing is
    /// read out of band, and the payee is what changed: the sentence here used to say no endpoint
    /// returned a payee id and that the row existed solely because a transaction named one. Both
    /// halves are false now — <c>POST /api/payees</c> creates the row and answers with its id, and
    /// <c>POST /api/transactions</c> NAMES a payee by that id rather than describing one by name,
    /// because <c>payees.name</c> is an AEAD envelope this server cannot resolve a name against.
    /// </remarks>
    private static async Task<FurnishedIds> FurnishAccountAsync(PostgresTestHost host, HttpClient client)
    {
        Guid accountId = await CreateAsync(client, "/api/accounts", new
        {
            // Sealed, indexed and identified through SealedNarrative rather than sent as the word
            // "Checking": accounts.name is an AEAD envelope and accounts.name_key a blind index, so a flat
            // name is a 400 from CreateAccountHandler and this seeding would never reach the subject of
            // the test. The id is on the body because the client mints it — it is the associated data the
            // name was sealed against, so this API has to hand back the spelling it was sent.
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Checking"),
            nameKey = SealedNarrative.EncodedIndex("Checking"),
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        Guid categoryGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            // Sealed and indexed through SealedNarrative rather than sent as a flat name:
            // category_groups.name is an AEAD envelope and category_groups.name_key a blind index, so
            // plain text is a 400 from CreateCategoryGroupHandler and this seeding would never reach
            // the subject of the test. The id is client-minted because it is the associated data the
            // name is sealed against; CreateAsync reads the same value back off the 201.
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = (string?)null,
        });
        Guid categoryId = await CreateAsync(client, "/api/categories", new
        {
            name = "Groceries",
            description = (string?)null,
            categoryGroupId,
        });
        // The payee is a request of its own now: POST /api/transactions takes an identifier, and the
        // server can no longer resolve a name into a row — payees.name is an AEAD envelope drawn under
        // a fresh nonce, so two seals of one name are different bytes. Seeded here rather than dropped
        // because a budget with no payee row would leave this file measuring one relation fewer than
        // its name claims, silently.
        Guid payeeId = await CreateAsync(client, "/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Starbucks"),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        Guid transactionId = await CreateAsync(client, "/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = "Coffee",
            payeeId,
            categoryId,
        });

        return new FurnishedIds(accountId, categoryGroupId, categoryId, payeeId, transactionId);
    }

    /// <summary>
    /// Adds the second budget no endpoint can create, through the domain factory on the container
    /// superuser, and returns its id.
    /// </summary>
    /// <remarks>
    /// Superuser rather than the application role for two reasons at once: <c>budgets</c> is policed by
    /// <c>user_isolation</c> and this connection carries no <c>app.current_user_id</c>, and the grant
    /// matrix gives the application role no way to insert a budget outside provisioning anyway.
    /// <see cref="Budget.Create" /> rather than an <c>INSERT</c> so the row satisfies every rule the
    /// domain would have applied — a hand-written row that violated one would fail this test for a
    /// reason that has nothing to do with the export.
    /// </remarks>
    /// <param name="host">The running host, for its superuser connection string.</param>
    /// <param name="userId">The owner the second budget is filed under.</param>
    /// <param name="createdAtUtc">
    /// The instant the row claims, which the caller derives from the provisioned budget rather than
    /// choosing. It must be <see cref="DateTimeKind.Utc" /> — PostgreSQL's <c>timestamptz</c> rejects
    /// any other kind outright — and it must be later than the provisioned budget's, for the reason
    /// the comment inside spells out.
    /// </param>
    private static async Task<Guid> SeedSecondBudgetAsync(
        PostgresTestHost host,
        Guid userId,
        DateTime createdAtUtc)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);

        // Named, and it must be: IX_budgets_user_id_name is UNIQUE … NULLS NOT DISTINCT, so a second
        // nameless budget collides with the one provisioning wrote and this seeding would be testing
        // the index.
        //
        // The instant is the provisioned budget's own, shifted a minute forward — a minute rather than
        // a tick because timestamptz resolves microseconds and a minute reads as deliberate in a
        // failure message. It is load-bearing, not tidy: FindFirstForUserAsync returns the EARLIEST
        // budget an owner holds, so any instant in the past would make this empty row the ambient one,
        // move every furnished id into a budget the export never reads, and leave the caller's eight-id
        // body search unable to find anything even against an implementation that truncates.
        Budget second = Budget.Create(
            Guid.CreateVersion7(), userId, SealedNarrative.Name("Holiday"), createdAtUtc);
        db.Budgets.Add(second);
        await db.SaveChangesAsync();

        return second.Id;
    }

    /// <summary>
    /// The instant the sign-in's own budget claims.
    /// </summary>
    /// <remarks>
    /// The only thing still read back out of band here. Both ids come off
    /// <see cref="ApiFactory.CreateSignedInClientAsync" />, which wrote them, but the instant does not —
    /// and the second budget's has to be derived from it rather than written down, for the reason
    /// <see cref="SeedSecondBudgetAsync" /> gives. A row that is not there fails here rather than
    /// silently handing the seeding a default instant.
    /// </remarks>
    private static async Task<DateTime> BudgetCreatedAtUtcAsync(PostgresTestHost host, Guid budgetId)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select created_at_utc from budgets where id = @budgetId",
            connection);
        command.Parameters.AddWithValue("budgetId", budgetId);

        return await command.ExecuteScalarAsync() switch
        {
            DateTime createdAtUtc => createdAtUtc,
            var unexpected => throw new InvalidOperationException(
                $"No budget stands under id '{budgetId}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Posts <paramref name="body" /> and returns the id of the row it created, failing loudly on any
    /// status other than success. A furnishing step that quietly did nothing would leave the
    /// non-vacuity guard to notice, one assertion too late.
    /// </summary>
    private static async Task<Guid> CreateAsync(HttpClient client, string path, object body)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every request
    /// below authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
