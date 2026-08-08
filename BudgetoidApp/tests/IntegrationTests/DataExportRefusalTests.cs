using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Budgets;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
/// budget: provisioning writes the one default budget alongside the user, and nothing else in the
/// product writes that table. It carries a <b>name</b>, and that is load-bearing rather than
/// decorative — <c>IX_budgets_user_id_name</c> is <c>UNIQUE … NULLS NOT DISTINCT</c>, so a second
/// nameless budget for one owner is refused by the index and the test would be measuring the index
/// instead of the export. It is written through <c>BudgetoidDbContext</c> and the domain factory
/// rather than as raw SQL, the way <c>ErasureAtomicityTests.SeedIdentityRowsAsync</c> does, so the row
/// is one the application itself could have produced.
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
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        FurnishedIds furnished = await FurnishAccountAsync(host, client);
        (Guid userId, Guid budgetId) = await ResolveOwnerAsync(host, Subject);
        Guid secondBudgetId = await SeedSecondBudgetAsync(host, userId);

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
    /// budget provisioning gave it.
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
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        FurnishedIds furnished = await FurnishAccountAsync(host, client);
        (Guid userId, Guid budgetId) = await ResolveOwnerAsync(host, Subject);

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

        // Non-vacuity for the furnishing, matching the refusal's guard: the same five rows were written
        // here, so the two Arrange blocks differ by the second budget and nothing else.
        await Assert.That(
                new[]
                {
                    furnished.AccountId,
                    furnished.CategoryGroupId,
                    furnished.CategoryId,
                    furnished.PayeeId,
                    furnished.TransactionId,
                }.Distinct()
                .Count())
            .IsEqualTo(5);
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
    /// could have written — same validation, same repositories, same least-privilege role. Only the
    /// payee id is read back out of band: no endpoint returns it, the payee row exists solely because
    /// the transaction named one, and reading it through <c>GET /api/payees</c> would couple this file
    /// to that endpoint's wire shape for a value it uses as an opaque string.
    /// </remarks>
    private static async Task<FurnishedIds> FurnishAccountAsync(PostgresTestHost host, HttpClient client)
    {
        Guid accountId = await CreateAsync(client, "/api/accounts", new
        {
            name = "Checking",
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        Guid categoryGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            name = "Essentials",
            description = (string?)null,
        });
        Guid categoryId = await CreateAsync(client, "/api/categories", new
        {
            name = "Groceries",
            description = (string?)null,
            categoryGroupId,
        });
        Guid transactionId = await CreateAsync(client, "/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = "Coffee",
            payeeName = "Starbucks",
            categoryId,
        });

        Guid payeeId = await ReadSingleIdAsync(host, "select id from payees");

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
    private static async Task<Guid> SeedSecondBudgetAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);

        // Named, and it must be: IX_budgets_user_id_name is UNIQUE … NULLS NOT DISTINCT, so a second
        // nameless budget collides with the one provisioning wrote and this seeding would be testing
        // the index. Created after the default budget, so the ambient budget stays the provisioned one
        // and the refusal is reached rather than an unresolved-budget failure earlier in the request.
        Budget second = Budget.Create(userId, "Holiday", SeedInstant);
        db.Budgets.Add(second);
        await db.SaveChangesAsync();

        return second.Id;
    }

    /// <summary>
    /// Reads back the user and default budget provisioning minted for <paramref name="subject" />.
    /// Nothing the API returns names either id, so the lookup goes through the credential the
    /// middleware resolved the request on.
    /// </summary>
    private static async Task<(Guid UserId, Guid BudgetId)> ResolveOwnerAsync(
        PostgresTestHost host,
        string subject)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            select credentials.user_id, budgets.id
            from credentials
            join budgets on budgets.user_id = credentials.user_id
            where credentials.provider = 'google' and credentials.subject = @subject
            """,
            connection);
        command.Parameters.AddWithValue("subject", subject);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"Provisioning wrote no account for subject '{subject}'.");
        }

        (Guid userId, Guid budgetId) = (reader.GetGuid(0), reader.GetGuid(1));

        // A second row here would mean the seeding already ran, which would silently scope the caller
        // to whichever budget came back first.
        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"Subject '{subject}' owns more than one budget; this lookup assumes exactly one.");
        }

        return (userId, budgetId);
    }

    /// <summary>
    /// Runs <paramref name="sql" /> on the container superuser and returns the one id it selects,
    /// refusing anything else — a furnishing step that quietly wrote no row would otherwise reach the
    /// assertions as an empty <see cref="Guid" /> that no body contains, which reads as a pass.
    /// </summary>
    private static async Task<Guid> ReadSingleIdAsync(PostgresTestHost host, string sql)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);

        return await command.ExecuteScalarAsync() switch
        {
            Guid id => id,
            var unexpected => throw new InvalidOperationException(
                $"Expected exactly one id from '{sql}', got '{unexpected ?? "null"}'."),
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
    /// Fixed UTC instant for the out-of-band budget. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
