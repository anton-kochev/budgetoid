using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That two accounts exporting from one running API each receive their own budget's rows and none of
/// the other's.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the end-to-end half of FR-011</b> — "the export document shall contain no row belonging
/// to a budget the requesting user does not own". Each isolation layer is proven on its own elsewhere:
/// <c>RlsIsolationTests</c> probes the <c>budget_isolation</c> and <c>user_isolation</c> policies with
/// raw SQL on the application role, and <c>BudgetIsolationTests</c> probes EF's
/// <c>BudgetIsolation</c> query filters. With both layers in place, a mutation that removes one of them
/// cannot redden this file, because the other still holds. What this file adds is the whole path — the
/// session cookie, the ambient budget, the interceptor, the handler and the serializer — answering
/// two real tenants.
/// </para>
/// <para>
/// <b>Presence is asserted before absence, in the same test and by the same search.</b> An absence
/// search alone passes against an export whose content reads came back empty, and against a seeder
/// that silently wrote nothing. So <see cref="Export_ForEachOfTwoTenants_NamesEveryOwnIdAndNoneOfTheOthers" />
/// first shows every id each tenant owns present in its own export, through the same
/// <see cref="Names" /> the absence half runs — which proves the rows exist, the export read them, and
/// the search can find an id when the body carries one. Only then does it look for the other
/// tenant's ids.
/// </para>
/// <para>
/// <b>The raw body text is searched rather than parsed members</b>, the way
/// <c>DataExportRefusalTests</c> searches it. A leak does not have to arrive under a member this test
/// knows the name of: a foreign id inside a nested object, a new member the document grows, or an id
/// spelled into a string all appear in the text and would all slip past an assertion over
/// <c>budgets[*].id</c>. The search matches the hyphenated <c>"D"</c> spelling, ignoring case — not
/// every spelling: an id written as <c>"N"</c>, or as base64, would slip past it. That is enough here
/// because each of the five collections' rows declares its <c>budgetId</c> — and the budget row is
/// that id, the user row its own — and the API registers no <see cref="Guid" /> converter of its own,
/// so System.Text.Json writes every <see cref="Guid" /> in the document as <c>"D"</c>. A leaked row
/// therefore brings a foreign id this test lists with it, in the one spelling this search reads.
/// </para>
/// <para>
/// <b>Both tenants are signed in on one factory</b>, through two distinct subjects. That factory is
/// built over <see cref="PostgresTestHost.AppConnectionString" />, so both tenants' requests run on the
/// <c>budgetoid_app</c> role — which the policies bind — and through the
/// <c>SessionContextInterceptor</c> the API registers on its context. Two hosts would give each tenant
/// its own database, and a test in which the other tenant's rows are not even in the database proves
/// nothing about isolation.
/// </para>
/// <para>
/// An owner holding more than one budget is refused an export outright; that refusal belongs to
/// <c>DataExportRefusalTests</c> and is not arranged here. Each tenant here owns exactly the budget
/// its account was seeded with.
/// </para>
/// <para>
/// <c>FurnishAccountAsync</c>, <c>CreateAsync</c> and <c>StartHostAsync</c> are duplicated from
/// <c>DataExportRefusalTests</c> rather than extracted, which is the local convention in this folder.
/// </para>
/// </remarks>
public sealed class DataExportTenancyTests
{
    private const string ExportPath = "/api/me/export";

    private const string FirstSubject = "export-tenancy-subject-a";

    private const string SecondSubject = "export-tenancy-subject-b";

    [Test]
    public async Task Export_ForEachOfTwoTenants_NamesEveryOwnIdAndNoneOfTheOthers()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        (Tenant first, Tenant second) = await ArrangeTwoTenantsAsync(host);

        // Act
        (HttpStatusCode firstStatus, string firstBody) = await ExportAsync(first.Client);
        (HttpStatusCode secondStatus, string secondBody) = await ExportAsync(second.Client);

        // Assert
        // Presence first: each body is a real export carrying every id its tenant owns, so the absence
        // checks below cannot pass on an empty export or a seeder that wrote nothing.
        await AssertIsOwnExportAsync(first, firstStatus, firstBody);
        await AssertIsOwnExportAsync(second, secondStatus, secondBody);

        Guid[] secondIdsInFirstExport = second.OwnedIds.Where(id => Names(firstBody, id)).ToArray();
        Guid[] firstIdsInSecondExport = first.OwnedIds.Where(id => Names(secondBody, id)).ToArray();

        await Assert.That(secondIdsInFirstExport).IsEmpty();
        await Assert.That(firstIdsInSecondExport).IsEmpty();
    }

    /// <summary>
    /// One signed-in account with every budget-owned table furnished, and the ids that belong to it.
    /// </summary>
    private sealed record Tenant(HttpClient Client, Guid UserId, Guid BudgetId, FurnishedIds Furnished)
    {
        /// <summary>
        /// Every id this tenant owns that an export could name: the account, its budget, and one row per
        /// budget-owned table.
        /// </summary>
        public Guid[] OwnedIds =>
        [
            UserId,
            BudgetId,
            Furnished.AccountId,
            Furnished.CategoryGroupId,
            Furnished.CategoryId,
            Furnished.PayeeId,
            Furnished.TransactionId,
        ];
    }

    /// <summary>
    /// The ids of the rows one furnished account owns.
    /// </summary>
    private sealed record FurnishedIds(
        Guid AccountId,
        Guid CategoryGroupId,
        Guid CategoryId,
        Guid PayeeId,
        Guid TransactionId);

    /// <summary>
    /// Signs in two accounts on the host's one factory and furnishes both before either exports.
    /// </summary>
    /// <remarks>
    /// Both tenants are furnished before any export runs, so the second tenant's rows are
    /// in the database while the first tenant's export is assembled.
    /// </remarks>
    private static async Task<(Tenant First, Tenant Second)> ArrangeTwoTenantsAsync(PostgresTestHost host)
    {
        ApiFactory.SignedInClient first = await host.Factory.CreateSignedInClientAsync(FirstSubject);
        ApiFactory.SignedInClient second = await host.Factory.CreateSignedInClientAsync(SecondSubject);

        FurnishedIds firstFurnished = await FurnishAccountAsync(first.Client);
        FurnishedIds secondFurnished = await FurnishAccountAsync(second.Client);

        return (
            new Tenant(first.Client, first.UserId, first.BudgetId, firstFurnished),
            new Tenant(second.Client, second.UserId, second.BudgetId, secondFurnished));
    }

    /// <summary>
    /// Asserts that <paramref name="body" /> is a successful export of <paramref name="tenant" />'s own
    /// budget and carries every id the tenant owns.
    /// </summary>
    private static async Task AssertIsOwnExportAsync(Tenant tenant, HttpStatusCode status, string body)
    {
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);

        JsonNode document = JsonNode.Parse(body)
            ?? throw new InvalidOperationException("The export answered an empty body.");
        await Assert.That(document["user"]!["id"]!.GetValue<Guid>()).IsEqualTo(tenant.UserId);

        JsonArray budgets = document["budgets"]!.AsArray();
        await Assert.That(budgets.Count).IsEqualTo(1);
        await Assert.That(budgets[0]!["id"]!.GetValue<Guid>()).IsEqualTo(tenant.BudgetId);

        // By the same search the absence checks run, so a pass here means the search can find an id
        // when the body carries one.
        Guid[] missing = tenant.OwnedIds.Where(id => !Names(body, id)).ToArray();
        await Assert.That(missing).IsEmpty();
    }

    /// <summary>
    /// Whether <paramref name="body" /> spells <paramref name="id" /> anywhere, in either case.
    /// </summary>
    private static bool Names(string body, Guid id) =>
        body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase);

    private static async Task<(HttpStatusCode Status, string Body)> ExportAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(ExportPath);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Writes one row into every budget-owned table over HTTP and returns the ids the assertions key
    /// on.
    /// </summary>
    /// <remarks>
    /// Through the real endpoints, so every row is one the application itself could have written — same
    /// validation, same repositories, same least-privilege role. The narrative values are sealed through
    /// <see cref="SealedNarrative" /> and the ids minted here, because each narrative column is an AEAD
    /// envelope sealed against its own row id; see <c>DataExportRefusalTests.FurnishAccountAsync</c>.
    /// </remarks>
    private static async Task<FurnishedIds> FurnishAccountAsync(HttpClient client)
    {
        Guid accountId = await CreateAsync(client, "/api/accounts", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Checking"),
            nameKey = SealedNarrative.EncodedIndex("Checking"),
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        Guid categoryGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = (string?)null,
        });
        Guid categoryId = await CreateAsync(client, "/api/categories", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Groceries"),
            nameKey = SealedNarrative.EncodedIndex("Groceries"),
            description = (string?)null,
            categoryGroupId,
        });
        Guid payeeId = await CreateAsync(client, "/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Starbucks"),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        Guid transactionId = await CreateAsync(client, "/api/transactions", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = SealedNarrative.EncodedDescription("Coffee"),
            payeeId,
            categoryId,
        });

        return new FurnishedIds(accountId, categoryGroupId, categoryId, payeeId, transactionId);
    }

    /// <summary>
    /// Posts <paramref name="body" /> and returns the id of the row it created, failing loudly on any
    /// status other than success.
    /// </summary>
    private static async Task<Guid> CreateAsync(HttpClient client, string path, object body)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every request
    /// below authenticates from a session cookie.
    /// </summary>
    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
