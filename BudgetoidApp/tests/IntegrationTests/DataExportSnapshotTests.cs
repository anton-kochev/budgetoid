using System.Net;
using System.Text.Json.Nodes;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That the export is assembled from one consistent read of the budget, so a write committing while
/// it runs is either wholly in the document or wholly out of it — never half.
/// </summary>
/// <remarks>
/// <para>
/// Each case commits one out-of-band change — an insert or a delete — in front of one of the export's
/// reads, using <see cref="MidReadCommitInterceptor" /> on the API's own context. A snapshot taken at
/// the export's first read answers the whole document from the state before that change; a read that
/// takes a fresh snapshot per statement shows some or all of it.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Before the transactions read</b>: a new account and a transaction naming it. Per-statement reads
/// produce a transaction whose account the document does not carry.
/// </description></item>
/// <item><description>
/// <b>Before the budgets read</b>: the same pair, one read after the export's first. A snapshot that
/// starts at the accounts read, or that leaves the users and budgets reads outside it, shows both rows.
/// </description></item>
/// <item><description>
/// <b>Before the transactions read, a delete</b>: an existing transaction goes. A snapshot keeps it;
/// per-statement reads lose it — and so does any fix that hides the first case by filtering orphans
/// out after the fact, since no filter can bring a row back.
/// </description></item>
/// <item><description>
/// <b>Before the category groups read</b>: a group, a category under it and a payee — the three
/// middle reads no other case commits to. Per-statement reads show all three rows.
/// </description></item>
/// <item><description>
/// <b>Before the accounts read, a second budget</b>: the owned set the completeness gate compares
/// grows by one. A snapshot hands the gate one budget and the export answers; an owned-budgets read
/// outside the snapshot sees two and refuses.
/// </description></item>
/// </list>
/// <para>
/// <b>The interceptor is added alongside the application's, not in place of it.</b>
/// <c>ConfigureDbContext</c> appends an options action, and <c>AddInterceptors</c> appends to the
/// list the application's own <c>AddDbContext</c> action already filled. A 200 carrying the seeded
/// rows is the proof: without <c>SessionContextInterceptor</c> no ambient budget is set and every
/// policed read fails.
/// </para>
/// </remarks>
public sealed class DataExportSnapshotTests
{
    private const string Subject = "export-snapshot-subject";

    private const string ExportPath = "/api/me/export";

    /// <summary>Minor unit of <c>USD</c>, which every seeded row uses.</summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Export_WhenAnAccountAndItsTransactionCommitMidExport_NeitherAppears()
    {
        // Arrange — a signed-in account whose budget already holds one account and one transaction,
        // and an API whose context commits a second pair just before its transactions read.
        await using PostgresTestHost host = await StartSignedInHostAsync();

        MidReadCommitInterceptor interceptor = new(host.ConnectionString);
        await using ApiFactory factory = CreateInterceptedFactory(host, interceptor);

        (HttpClient client, _, Guid budgetId) = await factory.CreateSignedInClientAsync(Subject);
        (Guid existingAccountId, Guid existingTransactionId) =
            await SeedAccountWithTransactionAsync(host, budgetId);

        interceptor.ArmInsert(budgetId);

        // Act
        ExportedDocument document = await ExportAsync(client);

        // Assert
        await AssertCommittedPairIsAbsentAsync(
            document, interceptor, existingAccountId, existingTransactionId);
    }

    [Test]
    public async Task Export_WhenAPairCommitsBeforeTheBudgetsRead_NeitherAppears()
    {
        // Arrange — the same pair, committed just before the export's budgets read. Session
        // authentication reads budgets too, before the endpoint runs, so the interceptor waits for the
        // export's users read and fires on the budgets read after it.
        await using PostgresTestHost host = await StartSignedInHostAsync();

        MidReadCommitInterceptor interceptor = new(
            host.ConnectionString, table: "budgets", afterTable: "users");
        await using ApiFactory factory = CreateInterceptedFactory(host, interceptor);

        (HttpClient client, _, Guid budgetId) = await factory.CreateSignedInClientAsync(Subject);
        (Guid existingAccountId, Guid existingTransactionId) =
            await SeedAccountWithTransactionAsync(host, budgetId);

        interceptor.ArmInsert(budgetId);

        // Act
        ExportedDocument document = await ExportAsync(client);

        // Assert
        await AssertCommittedPairIsAbsentAsync(
            document, interceptor, existingAccountId, existingTransactionId);
    }

    [Test]
    public async Task Export_WhenAnExistingTransactionIsDeletedMidExport_ItStillAppears()
    {
        // Arrange — a seeded account and transaction, and an API whose context deletes that
        // transaction on another connection just before its transactions read.
        await using PostgresTestHost host = await StartSignedInHostAsync();

        MidReadCommitInterceptor interceptor = new(host.ConnectionString);
        await using ApiFactory factory = CreateInterceptedFactory(host, interceptor);

        (HttpClient client, _, Guid budgetId) = await factory.CreateSignedInClientAsync(Subject);
        (Guid existingAccountId, Guid existingTransactionId) =
            await SeedAccountWithTransactionAsync(host, budgetId);

        interceptor.ArmDelete(existingTransactionId);

        // Act
        ExportedDocument document = await ExportAsync(client);

        // Assert — non-vacuity first: the delete really removed the row, and the account read ran as a
        // policed, budget-scoped request.
        await Assert.That(interceptor.Deleted).IsEqualTo(1);
        await Assert.That(IdsOf(document.Accounts)).Contains(existingAccountId);

        // The transaction was there when the export's first read ran, so it is in the document.
        await Assert.That(IdsOf(document.Transactions)).Contains(existingTransactionId);
    }

    /// <remarks>
    /// The three middle reads — category groups, categories and payees — sit between the accounts and
    /// transactions reads, and the other cases commit only to accounts, transactions and budgets, so a
    /// fix that left these three outside the snapshot would pass them. This commit lands just before
    /// the category_groups read: per-statement reads show all three rows.
    /// </remarks>
    [Test]
    public async Task Export_WhenAGroupCategoryAndPayeeCommitMidExport_NoneAppears()
    {
        // Arrange — an existing group and category, so both arrays are non-empty, and an API whose
        // context commits a new group, a category under it and a payee just before the export's
        // category_groups read. Gated on the users read, as the budgets case is, so nothing read
        // during session authentication can trip it.
        await using PostgresTestHost host = await StartSignedInHostAsync();

        MidReadCommitInterceptor interceptor = new(
            host.ConnectionString, table: "category_groups", afterTable: "users");
        await using ApiFactory factory = CreateInterceptedFactory(host, interceptor);

        (HttpClient client, _, Guid budgetId) = await factory.CreateSignedInClientAsync(Subject);
        (Guid existingGroupId, Guid existingCategoryId) = await SeedGroupWithCategoryAsync(host, budgetId);

        interceptor.ArmInsertCategorySet(budgetId);

        // Act
        ExportedDocument document = await ExportAsync(client);

        // Assert — non-vacuity: three rows committed mid-export, and the seeded pair came back.
        await Assert.That(interceptor.Inserted).IsEqualTo(3);
        await Assert.That(IdsOf(document.CategoryGroups)).Contains(existingGroupId);
        await Assert.That(IdsOf(document.Categories)).Contains(existingCategoryId);

        // Every category names a group the document carries.
        HashSet<Guid> groupIds = [.. IdsOf(document.CategoryGroups)];
        List<Guid> orphanedGroupIds =
        [
            .. document.Categories
                .Select(row => row!["categoryGroupId"]!.GetValue<Guid>())
                .Where(groupId => !groupIds.Contains(groupId)),
        ];
        await Assert.That(orphanedGroupIds).IsEmpty();

        // And the commit is nowhere in the document, in any spelling.
        await Assert.That(document.Body).DoesNotContain(interceptor.CategoryGroupId.ToString());
        await Assert.That(document.Body).DoesNotContain(interceptor.CategoryId.ToString());
        await Assert.That(document.Body).DoesNotContain(interceptor.PayeeId.ToString());
    }

    /// <remarks>
    /// The handler refuses unless the owned set is exactly the ambient budget. The second budget
    /// commits after the export's budgets read and before its accounts read, so a snapshot fixed at
    /// the first read hands the gate one budget and the export answers. An owned-budgets read that
    /// escapes the snapshot and runs after the commit sees two, and the export refuses.
    /// </remarks>
    [Test]
    public async Task Export_WhenASecondBudgetCommitsMidExport_TheRefusalDecidesOnTheSnapshot()
    {
        // Arrange — the commit is placed in front of the export's accounts read. The gate is the
        // users read rather than the budgets read because session authentication reads budgets too,
        // before the endpoint runs; only the export reads users, and it reads budgets straight after,
        // so the firing point is the same one: after the export's budgets read, before its accounts.
        await using PostgresTestHost host = await StartSignedInHostAsync();

        MidReadCommitInterceptor interceptor = new(
            host.ConnectionString, table: "accounts", afterTable: "users");
        await using ApiFactory factory = CreateInterceptedFactory(host, interceptor);

        (HttpClient client, Guid userId, Guid budgetId) = await factory.CreateSignedInClientAsync(Subject);

        interceptor.ArmInsertBudget(userId);

        // Act
        HttpResponseMessage response = await client.GetAsync(ExportPath);
        string body = await response.Content.ReadAsStringAsync();

        // Assert — non-vacuity: the second budget really committed mid-export.
        await Assert.That(interceptor.Inserted).IsEqualTo(1);

        // The gate decided on the snapshot: one owned budget, the ambient one, and an answer.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonArray budgets = (JsonNode.Parse(body)
            ?? throw new InvalidOperationException("The export answered an empty body."))["budgets"]!.AsArray();
        await Assert.That(IdsOf(budgets)).IsEquivalentTo([budgetId]);
        await Assert.That(body).DoesNotContain(interceptor.BudgetId.ToString());
    }

    /// <summary>The export's raw body and the collections the cases read.</summary>
    private sealed record ExportedDocument(
        string Body,
        JsonArray Accounts,
        JsonArray Transactions,
        JsonArray CategoryGroups,
        JsonArray Categories);

    private static ApiFactory CreateInterceptedFactory(
        PostgresTestHost host,
        MidReadCommitInterceptor interceptor) =>
        host.CreateFactory(
            defaultSubject: Subject,
            configureServices: services => services.ConfigureDbContext<BudgetoidDbContext>(
                (_, options) => options.AddInterceptors(interceptor)));

    private static async Task<ExportedDocument> ExportAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync(ExportPath);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        JsonObject budget = OnlyBudget(JsonNode.Parse(body)
            ?? throw new InvalidOperationException("The export answered an empty body."));

        return new ExportedDocument(
            body,
            budget["accounts"]!.AsArray(),
            budget["transactions"]!.AsArray(),
            budget["categoryGroups"]!.AsArray(),
            budget["categories"]!.AsArray());
    }

    /// <summary>
    /// The claims both insert cases make: the pair really committed, the seeded rows are there, no
    /// transaction names a missing account, and neither committed id appears anywhere in the body.
    /// </summary>
    /// <remarks>
    /// Both of the last two are needed. The orphan check catches a document that caught the
    /// transaction but not its account; the body search catches one that caught both — consistent
    /// with itself, and still not the state at the export's first read.
    /// </remarks>
    private static async Task AssertCommittedPairIsAbsentAsync(
        ExportedDocument document,
        MidReadCommitInterceptor interceptor,
        Guid existingAccountId,
        Guid existingTransactionId)
    {
        // Non-vacuity: two rows written means the mid-read commit really happened, and the seeded pair
        // in the document means the reads ran as a policed, budget-scoped request.
        await Assert.That(interceptor.Inserted).IsEqualTo(2);
        await Assert.That(IdsOf(document.Accounts)).Contains(existingAccountId);
        await Assert.That(IdsOf(document.Transactions)).Contains(existingTransactionId);

        // Every transaction names an account the document carries.
        HashSet<Guid> accountIds = [.. IdsOf(document.Accounts)];
        List<Guid> orphanedAccountIds =
        [
            .. document.Transactions
                .Select(row => row!["accountId"]!.GetValue<Guid>())
                .Where(accountId => !accountIds.Contains(accountId)),
        ];
        await Assert.That(orphanedAccountIds).IsEmpty();

        // And the commit is nowhere in the document, in any spelling.
        await Assert.That(document.Body).DoesNotContain(interceptor.AccountId.ToString());
        await Assert.That(document.Body).DoesNotContain(interceptor.TransactionId.ToString());
    }

    /// <summary>
    /// Writes one account and one transaction naming it, on the container superuser, and returns
    /// their ids. Written out of band so the API's intercepted context sends no command before the
    /// export's own.
    /// </summary>
    private static async Task<(Guid AccountId, Guid TransactionId)> SeedAccountWithTransactionAsync(
        PostgresTestHost host,
        Guid budgetId)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);

        DateTime createdAtUtc = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

        Account account = Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Existing account"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            createdAtUtc);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            budgetId,
            account.Id,
            -12.50m,
            UsdMinorUnit,
            new DateOnly(2026, 9, 1),
            null,
            createdAtUtc);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        return (account.Id, transaction.Id);
    }

    /// <summary>
    /// Writes one category group and one category under it, on the container superuser, and returns
    /// their ids.
    /// </summary>
    private static async Task<(Guid GroupId, Guid CategoryId)> SeedGroupWithCategoryAsync(
        PostgresTestHost host,
        Guid budgetId)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);

        DateTime createdAtUtc = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Existing group"),
            null,
            0,
            createdAtUtc);
        db.CategoryGroups.Add(group);
        await db.SaveChangesAsync();

        Category category = Category.Create(
            Guid.CreateVersion7(),
            budgetId,
            group.Id,
            SealedNarrative.Indexed("Existing category"),
            null,
            0,
            createdAtUtc);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        return (group.Id, category.Id);
    }

    private static List<Guid> IdsOf(JsonArray rows) =>
        [.. rows.Select(row => row!["id"]!.GetValue<Guid>())];

    private static JsonObject OnlyBudget(JsonNode document)
    {
        JsonArray budgets = document["budgets"]!.AsArray();

        return budgets.Count == 1
            ? budgets[0]!.AsObject()
            : throw new InvalidOperationException(
                $"Expected exactly one budget in the document, got {budgets.Count}.");
    }

    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
