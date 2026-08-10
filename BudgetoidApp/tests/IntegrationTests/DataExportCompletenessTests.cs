using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// That the document really carries what the account owns — every row of every budget-owned table,
/// every column of every row, and the nulls those columns hold — rather than an envelope with the
/// right shape and nothing inside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two rows of each collection, never one.</b> A one-row seed is satisfied by a read that returns
/// the first row as fully as by one that returns all of them, so it cannot tell completeness from a
/// lucky partial — and the shapes that break here are exactly the partial ones: a <c>First()</c>, a
/// forgotten <c>ToList</c>, a page size nobody chose. Each assertion compares the <b>id set</b> the
/// document returned against the id set the test seeded, in both directions, so a row that went
/// missing and a row that arrived from somewhere else fail the same line. A non-vacuity guard runs
/// first: two distinct seeded ids, because a seeder that quietly wrote nothing would otherwise make
/// an empty document match an empty expectation.
/// </para>
/// <para>
/// The whole set is not enough on its own, which is why every column of every row is asserted beside
/// it. A projection that returned the right ids with a null name, a zeroed balance or a dropped
/// parent id satisfies set equality exactly, and a person restoring from this file would find the
/// rows present and the data gone. <c>createdAtUtc</c> is compared against the value read straight
/// out of the table on the container superuser rather than against a window around the request:
/// the persisted instant is the only correct answer, and a timestamp regenerated at serialization
/// time would pass any window assertion.
/// </para>
/// <para>
/// Money is asserted through <c>GetValue&lt;decimal&gt;()</c> and never against rendered text.
/// <c>0.0000</c> and <c>0</c> are the same number written at two scales, and the scale a
/// <see cref="decimal" /> carries out of a <c>numeric(19,4)</c> column is an artifact of the column
/// rather than a promise to the caller — a string comparison would pin the artifact and go red on a
/// change nobody can observe. <c>type</c> is the opposite case and is asserted as text: it ships as
/// <c>"Checking"</c>, PascalCase, because the <c>JsonStringEnumConverter</c> registered at
/// <c>Api/Program.cs:114</c> carries no naming policy, and a reader of a saved file has nothing but
/// that string.
/// </para>
/// <para>
/// The money data is seeded over HTTP, so every row is one the application itself could have
/// written — same validation, same repositories, same least-privilege role — and the two reads that
/// go out of band (creation instants, payee ids) go to the container superuser because no endpoint
/// exposes either. The seeder is duplicated from <c>ErasureAtomicityTests.FurnishAccountAsync</c>
/// rather than extracted, which is the local convention in this folder and is stated as such at
/// <c>ErasureAtomicityTests.cs:618-622</c>; four files already carry their own copy.
/// </para>
/// <para>
/// Everything is read as <see cref="JsonNode" /> and never as a typed record. Deserializing into
/// <c>ExportDocument</c> would check the document against the same declarations that produced it,
/// so a property renamed on both sides at once — or one the serializer silently omitted — would
/// agree with itself. What a caller saving this file actually holds is the wire text.
/// </para>
/// <para>
/// <b>The <c>Count</c> assertion on each collection's row is half a pin, and the missing half is
/// named rather than left to be discovered.</b> The one on the user row works because it has a
/// partner: <c>DataMinimizationSchemaTests.Schema_PinsTheColumnsOfTheUserRow</c> pins the
/// <c>users</c> table to three columns while the assertion here pins the document to three
/// properties, so a column added on either side reds the test on the other and neither can be
/// silenced by editing its partner. No such schema pin exists for the five budget-owned tables —
/// <c>DataMinimizationSchemaTests</c> covers <c>users</c> alone. So each count below catches a
/// property added to the document with no column behind it, and catches <b>nothing</b> in the other
/// direction: a column added to <c>accounts</c> and never projected leaves every test in this file
/// green while somebody's saved copy quietly stops being a copy. Closing that direction is Story
/// 7.4's inventory check, which compares the document's shape against the schema itself rather than
/// against a number written here.
/// </para>
/// <para>
/// <b>Completeness is not correctness, and
/// <see cref="Export_CarriesTheUserRecordWithEveryPersistedColumn" /> is only the first of the two.</b>
/// Its address assertion re-derives the expected value exactly the way
/// <c>ApiFactory.CreateAuthenticatedClient</c> derives the <c>email</c> claim it puts on the request, so
/// it agrees with itself whichever value the export actually read — an implementation projecting that
/// claim into <c>user.email</c> and never touching the <c>users</c> row passes it, and passes every
/// other test in this file besides, because the stored address and the claim are the same string by
/// construction everywhere here.
/// <see cref="Export_ForASubjectWhoseProviderAddressChanged_CarriesTheStoredAddress" /> is the one
/// arrangement in which the two differ, and it matters most on this route: the export is the only
/// surface whose answer a person keeps as a file, so an address they cannot be reached at is wrong for
/// as long as they keep it. The completeness assertion stays exactly as it is — it proves the member is
/// present and populated, which is worth keeping, and it was never a claim about <em>which</em> value.
/// </para>
/// </remarks>
public sealed class DataExportCompletenessTests
{
    private const string ExportPath = "/api/me/export";

    private const string Subject = "export-completeness-subject";

    /// <summary>
    /// The address the account in
    /// <see cref="Export_ForASubjectWhoseProviderAddressChanged_CarriesTheStoredAddress" /> is registered
    /// under, and the only one its export may ever carry. Deliberately not the
    /// <c>{subject}@example.com</c> shape the factory falls back to, so an export that rebuilt the
    /// address out of the subject would be visible there rather than agreeing by construction.
    /// </summary>
    private const string RegisteredAddress = "registered@budgetoid.test";

    /// <summary>
    /// The address the provider reports <em>after</em> the change — carried in the claim, stored nowhere.
    /// Deliberately neither a substring nor a superstring of <see cref="RegisteredAddress" />, so the
    /// "appears nowhere in the document" half fails on an echo rather than on the correct answer.
    /// </summary>
    private const string ChangedProviderAddress = "changed-at-the-provider@budgetoid.test";

    [Test]
    public async Task Export_CarriesTheUserRecordWithEveryPersistedColumn()
    {
        // Arrange — a bare established account. The user row is provisioning's own work, so nothing
        // needs furnishing to make it real.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        (Guid userId, _) = await ResolveOwnerAsync(host, Subject);
        IReadOnlyDictionary<Guid, DateTime> created = await ReadCreationInstantsAsync(host, "users");

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonObject user = document["user"]!.AsObject();

        // Assert — the three columns the row carries, each with the value the database holds.
        await Assert.That(user["id"]!.GetValue<Guid>()).IsEqualTo(userId);

        // The address is re-derived here the way the factory derives the claim, so this line says the
        // member is present and populated and nothing at all about which value was read — an export
        // echoing the token's email claim satisfies it. Which value is
        // Export_ForASubjectWhoseProviderAddressChanged_CarriesTheStoredAddress, and it is the only test
        // in this file where the stored address and the claim are different strings.
        await Assert.That(user["email"]!.GetValue<string>()).IsEqualTo($"{Subject}@example.com");
        await Assert.That(user["createdAtUtc"]!.GetValue<DateTime>()).IsEqualTo(created[userId]);

        // And exactly three, which is the half that pairs with
        // DataMinimizationSchemaTests.Schema_PinsTheColumnsOfTheUserRow. That test pins the users
        // table to id, email and created_at_utc; this one pins the document to the same three. A
        // column added to the table and not to the document reds that one, a property added to the
        // document and not to the table reds this one, and neither can be silenced by editing the
        // other — which is the only way an export can be checked for completeness by a count at all.
        await Assert.That(user.Count).IsEqualTo(3);
    }

    /// <summary>
    /// That the address in the file is the one the account is <em>registered under</em>, and never the
    /// one on the token that asked for the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only test here where the stored address and the <c>email</c> claim are different strings,
    /// which is the only arrangement that can tell an export reading the <c>users</c> row apart from one
    /// projecting the claim off the request. Everywhere else the two are the same value by construction,
    /// so a document assembled without ever touching that row satisfies every other assertion in this
    /// file — the completeness test included, since it re-derives its expectation exactly the way the
    /// factory derives the claim.
    /// </para>
    /// <para>
    /// One subject and two clients, not two accounts. Two accounts would measure isolation, which
    /// <c>DataExportRefusalTests</c> already covers; what is measured here is that a <em>later</em> token
    /// has no authority over what registration wrote. That is the rule
    /// <c>docs/business-logic/users-and-ownership.md</c> states — the stored address is deliberately
    /// never refreshed from the provider — and this route is where it costs the most, because the export
    /// is the one surface whose answer a person keeps. A person whose Google address changed must still
    /// find, in the copy of their own data, the address their account can actually be reached at.
    /// </para>
    /// <para>
    /// This is
    /// <see cref="SignedInUserEndpointTests.Me_ForASubjectWhoseProviderAddressChanged_RespondsWithTheStoredAddress" />'s
    /// shape and its argument, deliberately rather than a second one invented for this file:
    /// <c>UnitTests.EnsureUserHandlerTests.EnsureUser_ReturningUserWhoseProviderEmailChanged_KeepsTheRegisteredEmail</c>
    /// pins the rule at the row, that one pins it at the <c>/api/me</c> wire, and this one pins it in the
    /// document — three places, one rule, and a reader who has met the argument once has met it here.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Export_ForASubjectWhoseProviderAddressChanged_CarriesTheStoredAddress()
    {
        // Arrange — the account is registered under address A, by a client carrying A in its claim. Both
        // addresses are written down rather than left to the factory's {subject}@example.com fallback: an
        // export that rebuilt the address out of the subject would otherwise agree with that fallback.
        await using PostgresTestHost host = await StartHostAsync();

        HttpClient beforeTheChange = host.Factory.CreateAuthenticatedClient(Subject, RegisteredAddress);
        await ApiFactory.EstablishAccountAsync(beforeTheChange);

        // The same person after a Google address change: the same subject — which is what the credential
        // resolves on, and therefore what makes this one account rather than two — carrying address B.
        HttpClient afterTheChange = host.Factory.CreateAuthenticatedClient(
            Subject,
            ChangedProviderAddress);

        // Not needed to reach the export, which carries no ProvisionsUser and mints nothing, and verified
        // rather than assumed: EnsureUserHandler asks ResolveUserHandler first, that resolves the existing
        // credential on (provider, subject), and the handler returns before it reaches either the insert
        // or the email uniqueness that would 409 — so this second call is a resolve on the same account.
        // It is here because it is the real sequence a returning client performs, and because it is the
        // one moment the stored address could be refreshed from the new token. Drop this line and a
        // provisioning path that had started writing the claim over the row leaves this test green.
        await ApiFactory.EstablishAccountAsync(afterTheChange);

        // The control on the line above, not ceremony: this read throws unless exactly one credential row
        // on this subject owns exactly one budget, so a second establish that had minted a second account
        // fails here rather than letting the assertions run against whichever row came back first.
        (Guid userId, _) = await ResolveOwnerAsync(host, Subject);

        // Act — as the person whose token now says B. Read as text and parsed from that text rather than
        // through GetExportAsync's stream: the negative assertion needs the payload exactly as it went
        // over the wire, and an address hidden behind an escape sequence in a re-rendered document is a
        // leak that a search over the re-rendered text would report as absent.
        HttpResponseMessage response = await afterTheChange.GetAsync(ExportPath);
        response.EnsureSuccessStatusCode();

        string payload = await response.Content.ReadAsStringAsync();
        JsonNode document = JsonNode.Parse(payload)
            ?? throw new InvalidOperationException("The export answered an empty body.");
        JsonObject user = document["user"]!.AsObject();

        // Assert — the id first, so the address below is read off the row this subject actually owns
        // rather than off a user object the handler assembled out of the request it was handed.
        await Assert.That(user["id"]!.GetValue<Guid>()).IsEqualTo(userId);

        // A is the answer. This half alone goes green against an export that shipped A beside a second
        // member echoing the claim — the three-property count above guards the user object, but nothing
        // guards a claim echoed anywhere else in the document…
        await Assert.That(user["email"]!.GetValue<string>()).IsEqualTo(RegisteredAddress);

        // …so B must appear nowhere in the body at all. This half alone is no better on its own: a
        // document answering some third account's address entirely satisfies it. Together they say the
        // file carries the registered address and no trace of the one the provider reports today.
        await Assert.That(payload).DoesNotContain(ChangedProviderAddress);
    }

    [Test]
    public async Task Export_CarriesTheOwnedBudgetWithEveryPersistedColumn()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        (Guid userId, Guid budgetId) = await ResolveOwnerAsync(host, Subject);
        IReadOnlyDictionary<Guid, DateTime> created = await ReadCreationInstantsAsync(host, "budgets");

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonObject budget = OnlyBudget(document);

        // Assert — including userId, which the nesting already implies. It ships anyway so that a
        // completeness check over this document is a straight column-set comparison rather than one
        // against a list of agreed omissions.
        await Assert.That(budget["id"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(budget["userId"]!.GetValue<Guid>()).IsEqualTo(userId);
        await Assert.That(budget["createdAtUtc"]!.GetValue<DateTime>()).IsEqualTo(created[budgetId]);

        // Both nullable columns are present and null on a provisioned budget. Asserted as
        // present-and-null rather than by indexer alone: a missing property and a JSON null read the
        // same through JsonNode's indexer, and a column dropped from the projection would pass.
        await AssertJsonNullAsync(budget, "name");
        await AssertJsonNullAsync(budget, "baseCurrencyCode");

        // Two claims, deliberately not folded into one count. A budget object carries two unrelated
        // kinds of member — the columns the row persists and the collections filed under it — and a
        // single "Count == 10" would go red for either reason while naming neither. So: it carries
        // exactly these five nested collections, by name…
        string[] nested =
        [
            .. budget.Select(member => member.Key)
                .Intersect(NestedCollectionNames, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
        await Assert.That(string.Join(", ", nested))
            .IsEqualTo(string.Join(", ", NestedCollectionNames.Order(StringComparer.Ordinal)));

        // …and exactly five columns of its own beside them — id, userId, name, baseCurrencyCode,
        // createdAtUtc.
        await Assert.That(budget.Count - nested.Length).IsEqualTo(5);
    }

    [Test]
    public async Task Export_CarriesEveryAccountOfTheBudget()
    {
        // Arrange — two accounts, deliberately differing in every column a projection could confuse:
        // two types, two currencies and two opening balances, one of them zero.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        SeededBudget seeded = await FurnishTwoOfEachAsync(client);
        (_, Guid budgetId) = await ResolveOwnerAsync(host, Subject);
        IReadOnlyDictionary<Guid, DateTime> created = await ReadCreationInstantsAsync(host, "accounts");

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonArray accounts = OnlyBudget(document)["accounts"]!.AsArray();

        // Assert — the whole set first. Two distinct ids is the non-vacuity guard: without it an
        // empty document would match an empty seed and the comparison would agree with nothing.
        Guid[] expected = [seeded.CheckingAccountId, seeded.SavingsAccountId];
        await Assert.That(expected.Distinct().Count()).IsEqualTo(2);
        await Assert.That(SortedIds(accounts)).IsEqualTo(Sorted(expected));

        JsonObject checking = RowFor(accounts, seeded.CheckingAccountId);
        await Assert.That(checking["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(checking["name"]!.GetValue<string>()).IsEqualTo(CheckingName);
        await Assert.That(checking["type"]!.GetValue<string>()).IsEqualTo("Checking");
        await Assert.That(checking["openingBalance"]!.GetValue<decimal>()).IsEqualTo(0m);
        await Assert.That(checking["currencyCode"]!.GetValue<string>()).IsEqualTo("USD");
        await Assert.That(checking["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[seeded.CheckingAccountId]);

        // And exactly seven properties — id, budgetId, name, type, openingBalance, currencyCode,
        // createdAtUtc. On one row rather than both: the set equality above already says both rows came
        // out of one projection, so a second count would restate it. Half a pin, and the class remarks
        // say which half.
        await Assert.That(checking.Count).IsEqualTo(7);

        JsonObject savings = RowFor(accounts, seeded.SavingsAccountId);
        await Assert.That(savings["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(savings["name"]!.GetValue<string>()).IsEqualTo(SavingsName);
        await Assert.That(savings["type"]!.GetValue<string>()).IsEqualTo("Savings");
        await Assert.That(savings["openingBalance"]!.GetValue<decimal>()).IsEqualTo(125.50m);
        await Assert.That(savings["currencyCode"]!.GetValue<string>()).IsEqualTo("EUR");
        await Assert.That(savings["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[seeded.SavingsAccountId]);
    }

    [Test]
    public async Task Export_CarriesEveryCategoryGroupOfTheBudget()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        SeededBudget seeded = await FurnishTwoOfEachAsync(client);
        (_, Guid budgetId) = await ResolveOwnerAsync(host, Subject);
        IReadOnlyDictionary<Guid, DateTime> created =
            await ReadCreationInstantsAsync(host, "category_groups");

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonArray groups = OnlyBudget(document)["categoryGroups"]!.AsArray();

        // Assert
        Guid[] expected = [seeded.EssentialsGroupId, seeded.LifestyleGroupId];
        await Assert.That(expected.Distinct().Count()).IsEqualTo(2);
        await Assert.That(SortedIds(groups)).IsEqualTo(Sorted(expected));

        // Position is asserted as 0 and 1 rather than merely "present": the create handler appends,
        // taking the maximum position in the budget and adding one, so a fresh budget's two groups
        // are 0 and 1 in creation order. A document that shipped the column but not its value would
        // otherwise leave the ordering of somebody's budget unrecoverable from their own file.
        JsonObject essentials = RowFor(groups, seeded.EssentialsGroupId);
        await Assert.That(essentials["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(essentials["name"]!.GetValue<string>()).IsEqualTo(EssentialsGroupName);
        await Assert.That(essentials["description"]!.GetValue<string>())
            .IsEqualTo(EssentialsGroupDescription);
        await Assert.That(essentials["position"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(essentials["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[seeded.EssentialsGroupId]);

        // And exactly six — id, budgetId, name, description, position, createdAtUtc.
        await Assert.That(essentials.Count).IsEqualTo(6);

        JsonObject lifestyle = RowFor(groups, seeded.LifestyleGroupId);
        await Assert.That(lifestyle["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(lifestyle["name"]!.GetValue<string>()).IsEqualTo(LifestyleGroupName);
        await Assert.That(lifestyle["description"]!.GetValue<string>())
            .IsEqualTo(LifestyleGroupDescription);
        await Assert.That(lifestyle["position"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(lifestyle["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[seeded.LifestyleGroupId]);
    }

    [Test]
    public async Task Export_CarriesEveryCategoryOfTheBudget()
    {
        // Arrange — three rows, and the shape of the three is the claim. Two sit under the same group
        // so their positions are 0 and 1 rather than 0 and 0; the third sits under the other group so
        // categoryGroupId is not the same value on every row. Two rows carrying the same value for a
        // column say nothing about whether the column was read per row or filled in once, and with
        // only the first two seeded a projection taking categoryGroupId off the first row would pass.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        SeededBudget seeded = await FurnishTwoOfEachAsync(client);
        (_, Guid budgetId) = await ResolveOwnerAsync(host, Subject);
        IReadOnlyDictionary<Guid, DateTime> created =
            await ReadCreationInstantsAsync(host, "categories");

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonArray categories = OnlyBudget(document)["categories"]!.AsArray();

        // Assert
        Guid[] expected =
        [
            seeded.GroceriesCategoryId,
            seeded.TransportCategoryId,
            seeded.LeisureCategoryId,
        ];
        await Assert.That(expected.Distinct().Count()).IsEqualTo(3);
        await Assert.That(SortedIds(categories)).IsEqualTo(Sorted(expected));

        JsonObject groceries = RowFor(categories, seeded.GroceriesCategoryId);
        await Assert.That(groceries["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(groceries["categoryGroupId"]!.GetValue<Guid>())
            .IsEqualTo(seeded.EssentialsGroupId);
        await Assert.That(groceries["name"]!.GetValue<string>()).IsEqualTo(GroceriesCategoryName);
        await Assert.That(groceries["description"]!.GetValue<string>())
            .IsEqualTo(GroceriesCategoryDescription);
        await Assert.That(groceries["position"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(groceries["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[seeded.GroceriesCategoryId]);

        // And exactly seven — id, budgetId, categoryGroupId, name, description, position,
        // createdAtUtc.
        await Assert.That(groceries.Count).IsEqualTo(7);

        JsonObject transport = RowFor(categories, seeded.TransportCategoryId);
        await Assert.That(transport["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(transport["categoryGroupId"]!.GetValue<Guid>())
            .IsEqualTo(seeded.EssentialsGroupId);
        await Assert.That(transport["name"]!.GetValue<string>()).IsEqualTo(TransportCategoryName);
        await Assert.That(transport["description"]!.GetValue<string>())
            .IsEqualTo(TransportCategoryDescription);
        await Assert.That(transport["position"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(transport["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[seeded.TransportCategoryId]);

        // The third row is the one that makes categoryGroupId a per-row read: it names the other group,
        // so a projection that took the parent id once and stamped it on every row fails here. Its
        // position is 0 rather than 2 because the create handler appends within the group it is given,
        // and that is the second half of the same claim — position is read per row too.
        JsonObject leisure = RowFor(categories, seeded.LeisureCategoryId);
        await Assert.That(leisure["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(leisure["categoryGroupId"]!.GetValue<Guid>())
            .IsEqualTo(seeded.LifestyleGroupId);
        await Assert.That(leisure["name"]!.GetValue<string>()).IsEqualTo(LeisureCategoryName);
        await Assert.That(leisure["description"]!.GetValue<string>())
            .IsEqualTo(LeisureCategoryDescription);
        await Assert.That(leisure["position"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(leisure["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[seeded.LeisureCategoryId]);
    }

    [Test]
    public async Task Export_CarriesEveryPayeeOfTheBudget()
    {
        // Arrange — two payees means two transactions naming different payees, because nothing else
        // writes that table: there is no payee endpoint that creates one, and a payee exists only
        // because a transaction named it.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        SeededBudget seeded = await FurnishTwoOfEachAsync(client);
        (_, Guid budgetId) = await ResolveOwnerAsync(host, Subject);
        IReadOnlyDictionary<string, Guid> payeeIds = await ReadPayeeIdsAsync(host);
        IReadOnlyDictionary<Guid, DateTime> created = await ReadCreationInstantsAsync(host, "payees");

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonArray payees = OnlyBudget(document)["payees"]!.AsArray();

        // Assert — the ids come from the database rather than from a response, because no endpoint
        // returns a payee id and the transaction that minted the row names the payee by name only.
        Guid[] expected = [payeeIds[CoffeeShopPayeeName], payeeIds[TransitPayeeName]];
        await Assert.That(expected.Distinct().Count()).IsEqualTo(2);
        await Assert.That(SortedIds(payees)).IsEqualTo(Sorted(expected));

        JsonObject coffeeShop = RowFor(payees, payeeIds[CoffeeShopPayeeName]);
        await Assert.That(coffeeShop["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(coffeeShop["name"]!.GetValue<string>()).IsEqualTo(CoffeeShopPayeeName);
        await Assert.That(coffeeShop["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[payeeIds[CoffeeShopPayeeName]]);

        // And exactly four — id, budgetId, name, createdAtUtc.
        await Assert.That(coffeeShop.Count).IsEqualTo(4);

        JsonObject transit = RowFor(payees, payeeIds[TransitPayeeName]);
        await Assert.That(transit["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(transit["name"]!.GetValue<string>()).IsEqualTo(TransitPayeeName);
        await Assert.That(transit["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[payeeIds[TransitPayeeName]]);

        // Non-vacuity for the seeding itself: two transactions really named two different payees, so
        // the set above was compared against two rows the product wrote rather than two the test
        // invented. A furnishing step that stopped naming a payee would otherwise leave both sides of
        // the comparison empty.
        await Assert.That(payeeIds.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Export_CarriesEveryTransactionOfTheBudget()
    {
        // Arrange — the two transactions differ in account, category, payee, amount, date and
        // description, so no column can be right for both by accident.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        SeededBudget seeded = await FurnishTwoOfEachAsync(client);
        (_, Guid budgetId) = await ResolveOwnerAsync(host, Subject);
        IReadOnlyDictionary<string, Guid> payeeIds = await ReadPayeeIdsAsync(host);
        IReadOnlyDictionary<Guid, DateTime> created =
            await ReadCreationInstantsAsync(host, "transactions");

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonArray transactions = OnlyBudget(document)["transactions"]!.AsArray();

        // Assert
        Guid[] expected = [seeded.CoffeeTransactionId, seeded.BusPassTransactionId];
        await Assert.That(expected.Distinct().Count()).IsEqualTo(2);
        await Assert.That(SortedIds(transactions)).IsEqualTo(Sorted(expected));

        // Amounts through GetValue<decimal>() and dates as the ISO calendar date DateOnly renders.
        // The trailing zeros a numeric(19,4) column produces are a scale artifact rather than a
        // contract, so nothing here compares rendered money text.
        JsonObject coffee = RowFor(transactions, seeded.CoffeeTransactionId);
        await Assert.That(coffee["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(coffee["accountId"]!.GetValue<Guid>()).IsEqualTo(seeded.CheckingAccountId);
        await Assert.That(coffee["amount"]!.GetValue<decimal>()).IsEqualTo(-10.25m);
        await Assert.That(coffee["date"]!.GetValue<string>()).IsEqualTo(CoffeeDate);
        await Assert.That(coffee["description"]!.GetValue<string>()).IsEqualTo(CoffeeDescription);
        await Assert.That(coffee["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeIds[CoffeeShopPayeeName]);
        await Assert.That(coffee["categoryId"]!.GetValue<Guid>())
            .IsEqualTo(seeded.GroceriesCategoryId);
        await Assert.That(coffee["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[seeded.CoffeeTransactionId]);

        // And exactly nine — id, budgetId, accountId, amount, date, description, payeeId, categoryId,
        // createdAtUtc.
        await Assert.That(coffee.Count).IsEqualTo(9);

        JsonObject busPass = RowFor(transactions, seeded.BusPassTransactionId);
        await Assert.That(busPass["budgetId"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await Assert.That(busPass["accountId"]!.GetValue<Guid>()).IsEqualTo(seeded.SavingsAccountId);
        await Assert.That(busPass["amount"]!.GetValue<decimal>()).IsEqualTo(-25m);
        await Assert.That(busPass["date"]!.GetValue<string>()).IsEqualTo(BusPassDate);
        await Assert.That(busPass["description"]!.GetValue<string>()).IsEqualTo(BusPassDescription);
        await Assert.That(busPass["payeeId"]!.GetValue<Guid>()).IsEqualTo(payeeIds[TransitPayeeName]);
        await Assert.That(busPass["categoryId"]!.GetValue<Guid>())
            .IsEqualTo(seeded.TransportCategoryId);
        await Assert.That(busPass["createdAtUtc"]!.GetValue<DateTime>())
            .IsEqualTo(created[seeded.BusPassTransactionId]);
    }

    /// <summary>
    /// That a column holding no value ships as JSON <c>null</c> — not as the empty string, and not by
    /// being left out of the object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test that reds against a document assembled from the display DTOs</b>, and that
    /// is the whole reason it exists. <c>Application/Transactions/TransactionDto.cs:34</c> reads
    /// <c>transaction.Description ?? string.Empty</c>: a screen has to render something, so the DTO
    /// substitutes the empty string for a description the person never wrote. That substitution is
    /// correct for a screen and wrong for a copy of somebody's data — restoring from a file in which
    /// every unwritten note has become <c>""</c> loses the distinction between "no description" and
    /// "a description that is blank", and no later reader can tell which rows were which. An export
    /// built on <c>TransactionDto</c> would pass every other test in this file and fail this one.
    /// </para>
    /// <para>
    /// Three nullable columns on one transaction and one on each of a category group and a category,
    /// because the coercion is not confined to descriptions: an optional foreign key rendered as
    /// <see cref="Guid.Empty" /> is the same defect wearing a different type, and it points at a row
    /// that does not exist rather than at nothing.
    /// </para>
    /// <para>
    /// Asserted as present-and-null rather than through the indexer alone. <see cref="JsonNode" />
    /// returns <see langword="null" /> for a property that is JSON <c>null</c> and for one that is
    /// absent, so an indexer check alone would pass over a serializer configured to omit nulls — and
    /// a caller reading the file could not tell a column that was empty from a column that was never
    /// exported.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Export_PreservesTheNullsAPersistedRowCarries()
    {
        // Arrange — one of each, every optional column deliberately left empty. The transaction names
        // no payee, so nothing writes the payees table at all.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        Guid accountId = await CreateAsync(client, "/api/accounts", new
        {
            name = CheckingName,
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        Guid groupId = await CreateAsync(client, "/api/category-groups", new
        {
            name = EssentialsGroupName,
            description = (string?)null,
        });
        Guid categoryId = await CreateAsync(client, "/api/categories", new
        {
            name = GroceriesCategoryName,
            description = (string?)null,
            categoryGroupId = groupId,
        });
        Guid transactionId = await CreateAsync(client, "/api/transactions", new
        {
            amount = -10.25m,
            date = CoffeeDate,
            accountId,
            description = (string?)null,
            payeeName = (string?)null,
            categoryId = (Guid?)null,
        });

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonObject budget = OnlyBudget(document);

        // Assert — the rows are there first, which is what stops the null assertions below from being
        // satisfied by an absent row. A collection that came back empty carries no nulls to get wrong.
        JsonArray groups = budget["categoryGroups"]!.AsArray();
        JsonArray categories = budget["categories"]!.AsArray();
        JsonArray transactions = budget["transactions"]!.AsArray();
        await Assert.That(SortedIds(groups)).IsEqualTo(Sorted([groupId]));
        await Assert.That(SortedIds(categories)).IsEqualTo(Sorted([categoryId]));
        await Assert.That(SortedIds(transactions)).IsEqualTo(Sorted([transactionId]));

        await AssertJsonNullAsync(RowFor(groups, groupId), "description");
        await AssertJsonNullAsync(RowFor(categories, categoryId), "description");

        JsonObject transaction = RowFor(transactions, transactionId);
        await AssertJsonNullAsync(transaction, "description");
        await AssertJsonNullAsync(transaction, "payeeId");
        await AssertJsonNullAsync(transaction, "categoryId");

        // The columns that do carry values are checked beside them, so a projection that had nulled
        // the whole row would fail here rather than pass the three assertions above for the wrong
        // reason.
        await Assert.That(transaction["accountId"]!.GetValue<Guid>()).IsEqualTo(accountId);
        await Assert.That(transaction["amount"]!.GetValue<decimal>()).IsEqualTo(-10.25m);
    }

    /// <summary>
    /// That the budget provisioning creates ships with a <c>name</c> of <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Export_PreservesTheNullsAPersistedRowCarries" /> because it is the
    /// <b>ordinary</b> state rather than a contrived one. <c>Budget.CreateDefault</c> is the only path
    /// that has ever produced a budget in this product, and it never sets a name, so the nameless
    /// budget is what every real account holds and what every real export must carry. A reader
    /// scanning this file has to be able to see that without first working out that the seeded edge
    /// case above happens to cover it — and the day a rename endpoint ships, the seeded case would
    /// still pass while this one would be the first to notice that a named budget had become the
    /// normal one.
    /// </remarks>
    [Test]
    public async Task Export_CarriesTheUnnamedBudgetWithANullName()
    {
        // Arrange — nothing but the account provisioning mints, which is precisely the point.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        (_, Guid budgetId) = await ResolveOwnerAsync(host, Subject);

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonObject budget = OnlyBudget(document);

        // Assert — the id first, so the null below is read off the budget this account actually owns
        // rather than off whatever happened to be first in an empty or foreign array.
        await Assert.That(budget["id"]!.GetValue<Guid>()).IsEqualTo(budgetId);
        await AssertJsonNullAsync(budget, "name");
    }

    /// <summary>
    /// That a budget holding more rows than any page size an implementer would reach for comes back
    /// whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>101, and the number is argued rather than picked.</b> The plausible defaults a paged read
    /// arrives at are 20, 25, 50 and 100 — the four that appear as the out-of-the-box page size across
    /// the API tooling anyone would copy from. 101 is the smallest count strictly greater than all
    /// four, so every one of them truncates this seed, and a <c>Take</c> introduced with any of them
    /// goes red here. One over rather than five hundred because the extra four hundred rows prove
    /// nothing this one does not and cost the suite real seconds.
    /// </para>
    /// <para>
    /// <b>The bound this test cannot cross, stated rather than left to be assumed: it cannot refute a
    /// cap above 101.</b> An export that silently stopped at 500 rows passes here, and no volume test
    /// can refute a cap above its own seed — that is a limit of the shape rather than a gap in this
    /// instance, and the answer to it is not a bigger number but a different kind of test. What this
    /// one buys is the whole class of caps somebody would arrive at by default rather than by
    /// decision.
    /// </para>
    /// <para>
    /// <b>The id set, never the count.</b> <c>Count == 101</c> is satisfied by a document carrying one
    /// row a hundred and one times, by a hundred and one rows from a different budget, and by a page
    /// that happened to be the right size — three defects the requirement is precisely about. The
    /// comparison runs in both directions over the ids the seeding wrote, with a non-vacuity guard on
    /// the seed first: a furnishing loop that quietly created nothing would otherwise let an empty
    /// document match an empty expectation.
    /// </para>
    /// <para>
    /// <b>Volume on <c>transactions</c> alone, deliberately.</b> The other four collections are proved
    /// complete at two rows each by the tests above, and a page size does not know which table it is
    /// truncating — the defect is one class, and one collection at volume is enough to catch it.
    /// Seeding 101 payees, 101 accounts and 101 categories beside them would multiply the slowest part
    /// of this file several times over to make the same claim again.
    /// </para>
    /// <para>
    /// Seeded over HTTP like every other test in this file, and it is affordable: measured against the
    /// cheapest test here, the 101 posts add about half a second on top of the host boot both of them
    /// pay for. The file's convention is that every money row is one the application itself could have
    /// written, and half a second is not a reason to break it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Export_ForABudgetOfOneHundredAndOneTransactions_ReturnsAllOfThem()
    {
        // Arrange — one account, then one transaction per row. Nothing distinguishes the rows from one
        // another because nothing needs to: the claim is about how many came back, and which.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        Guid accountId = await CreateAsync(client, "/api/accounts", new
        {
            name = CheckingName,
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });

        List<Guid> seeded = [];
        for (int index = 0; index < TransactionsAbovePlausiblePageSizes; index++)
        {
            seeded.Add(await CreateAsync(client, "/api/transactions", new
            {
                amount = -1.25m,
                date = CoffeeDate,
                accountId,
                description = (string?)null,
                payeeName = (string?)null,
                categoryId = (Guid?)null,
            }));
        }

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonArray transactions = OnlyBudget(document)["transactions"]!.AsArray();

        // Assert — the seed is real first, then the two directions of the comparison in one line.
        await Assert.That(seeded.Distinct().Count()).IsEqualTo(TransactionsAbovePlausiblePageSizes);
        await Assert.That(SortedIds(transactions)).IsEqualTo(Sorted(seeded));
    }

    /// <summary>
    /// That every collection in the document is ordered by when its rows were created, and not by the
    /// order they happened to be written in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The seeding is this test's control, and it is the whole reason the test is shaped the way it
    /// is.</b> A row written over HTTP takes its <c>created_at_utc</c> from the clock at the moment of
    /// the request and its id from <c>Guid.CreateVersion7</c>, whose leading 48 bits are that same
    /// clock. So rows seeded the ordinary way arrive with insertion order, id order and creation order
    /// all pointing the same way, and a document returned in any of the three satisfies an assertion
    /// written about any other. Every ordering test seeded that way is a decoration: it passes against
    /// an implementation with no <c>OrderBy</c> at all, because PostgreSQL will usually hand back a
    /// freshly-filled heap in insertion order anyway.
    /// </para>
    /// <para>
    /// So the rows go in out of band with their creation instants <b>inverted</b> against the order
    /// they are inserted in — the first row written is stamped latest, the last row written is stamped
    /// earliest — and the assertion is that the document comes back in the reverse of the order the
    /// rows were written. Now insertion order and creation order disagree, and an implementation that
    /// returned rows in the order it found them fails on every collection. This is also why the
    /// seeding cannot go through the endpoints: nothing in the product lets a caller choose the
    /// instant a row claims to have been created at, and it must not.
    /// </para>
    /// <para>
    /// <b>All five collections in one test rather than five, and that is what covers the id case.</b>
    /// The ids are minted inside the domain factories, so this test cannot choose them, and
    /// <c>Guid.CreateVersion7</c> is not monotonic within a millisecond — several rows created in one
    /// loop may carry ids in any order. An implementation ordering by id alone would therefore have to
    /// land the reversed order by chance, and asserting five collections at once means it would have to
    /// do so five times over. One collection alone would let that pass at odds worth worrying about.
    /// </para>
    /// <para>
    /// The rows are built through the domain factories on the container superuser rather than as raw
    /// <c>INSERT</c>s, the way <c>DataExportRefusalTests.SeedSecondBudgetAsync</c> does, so each one
    /// satisfies every rule the application would have applied. Superuser because <c>budget_isolation</c>
    /// is <c>FOR ALL</c> and this connection carries no ambient budget, and because the application role
    /// is not granted a way to write a row with an instant of the caller's choosing in the first place.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Export_OrdersEachCollectionByCreationRatherThanByInsertionOrder()
    {
        // Arrange — an established account, then three rows in every collection whose creation instants
        // run backwards against the order they are written in.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        (_, Guid budgetId) = await ResolveOwnerAsync(host, Subject);
        InvertedSeed seeded = await SeedWithCreationOrderInvertedAsync(host, budgetId);

        // Act
        JsonNode document = await GetExportAsync(client);
        JsonObject budget = OnlyBudget(document);

        // Assert — non-vacuity for every collection first. Three distinct ids each means the seeding
        // really wrote fifteen rows, so the sequence comparisons below run over something.
        foreach (IReadOnlyList<Guid> written in seeded.EveryCollection)
        {
            await Assert.That(written.Distinct().Count()).IsEqualTo(RowsPerCollection);
        }

        // Reversed, because the instants were. Compared as an ordered sequence rather than as a set:
        // membership is a different claim with its own tests above, and this line is only about order.
        await Assert.That(IdsInOrder(budget["accounts"]!.AsArray()))
            .IsEqualTo(InOrder(seeded.Accounts.Reverse()));
        await Assert.That(IdsInOrder(budget["categoryGroups"]!.AsArray()))
            .IsEqualTo(InOrder(seeded.CategoryGroups.Reverse()));
        await Assert.That(IdsInOrder(budget["categories"]!.AsArray()))
            .IsEqualTo(InOrder(seeded.Categories.Reverse()));
        await Assert.That(IdsInOrder(budget["payees"]!.AsArray()))
            .IsEqualTo(InOrder(seeded.Payees.Reverse()));
        await Assert.That(IdsInOrder(budget["transactions"]!.AsArray()))
            .IsEqualTo(InOrder(seeded.Transactions.Reverse()));
    }

    /// <summary>
    /// The smallest number of transactions that exceeds every page size an implementer would arrive at
    /// without choosing one — 20, 25, 50 and 100.
    /// </summary>
    private const int TransactionsAbovePlausiblePageSizes = 101;

    /// <summary>
    /// How many rows the ordering test writes into each collection. Three rather than two: two rows in
    /// the wrong order and two rows in the right order are the only two arrangements there are, so a
    /// two-row sequence says nothing about an implementation that reverses.
    /// </summary>
    private const int RowsPerCollection = 3;

    /// <summary>
    /// Minor unit of every currency the seeding uses. The domain reads it off the account's currency to
    /// bound the precision of a money value, and <c>USD</c> carries two.
    /// </summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// The instant the <b>first</b> row of each collection is stamped with. Every later row is stamped
    /// earlier, which is the inversion the ordering test rests on.
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeKind.Utc" /> is load-bearing rather than tidy: PostgreSQL's <c>timestamptz</c>
    /// rejects a <see cref="DateTime" /> of any other kind outright. Minutes apart rather than
    /// milliseconds so the ordering is legible in a failure message and cannot be confused with clock
    /// resolution.
    /// </remarks>
    private static readonly DateTime InversionBaseInstant =
        new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static readonly DateOnly InversionTransactionDate = new(2026, 6, 26);

    /// <summary>
    /// The ids of the rows the ordering test wrote, each list in <b>insertion</b> order — which is the
    /// order the document must not come back in.
    /// </summary>
    private sealed record InvertedSeed(
        IReadOnlyList<Guid> Accounts,
        IReadOnlyList<Guid> CategoryGroups,
        IReadOnlyList<Guid> Categories,
        IReadOnlyList<Guid> Payees,
        IReadOnlyList<Guid> Transactions)
    {
        public IEnumerable<IReadOnlyList<Guid>> EveryCollection =>
            [Accounts, CategoryGroups, Categories, Payees, Transactions];
    }

    /// <summary>
    /// Writes <see cref="RowsPerCollection" /> rows into every budget-owned table with their creation
    /// instants running backwards against the order they are inserted in, and returns the ids in
    /// insertion order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two saves rather than one because categories name a group and transactions name an account, and
    /// the model carries no navigation properties for EF to infer an insert order from — the principals
    /// are committed before the rows that point at them, rather than relying on the graph walk to work
    /// that out.
    /// </para>
    /// <para>
    /// Every name is unique within the budget on purpose: accounts, category groups, categories and
    /// payees each sit under a <c>UNIQUE (budget_id, name)</c> index, and a collision here would fail
    /// this test on the index rather than on the export.
    /// </para>
    /// </remarks>
    private static async Task<InvertedSeed> SeedWithCreationOrderInvertedAsync(
        PostgresTestHost host,
        Guid budgetId)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);

        // The inversion itself: the row written first claims the latest instant.
        static DateTime StampedAt(int insertionIndex) =>
            InversionBaseInstant.AddMinutes(-insertionIndex);

        List<Guid> accounts = [];
        List<Guid> categoryGroups = [];
        List<Guid> payees = [];

        for (int index = 0; index < RowsPerCollection; index++)
        {
            Account account = Account.Create(
                budgetId,
                $"Ordered account {index}",
                AccountType.Checking,
                0m,
                "USD",
                UsdMinorUnit,
                StampedAt(index));
            db.Accounts.Add(account);
            accounts.Add(account.Id);

            CategoryGroup categoryGroup = CategoryGroup.Create(
                budgetId,
                $"Ordered group {index}",
                description: null,
                position: index,
                StampedAt(index));
            db.CategoryGroups.Add(categoryGroup);
            categoryGroups.Add(categoryGroup.Id);

            Payee payee = Payee.Create(budgetId, $"Ordered payee {index}", StampedAt(index));
            db.Payees.Add(payee);
            payees.Add(payee.Id);
        }

        await db.SaveChangesAsync();

        List<Guid> categories = [];
        List<Guid> transactions = [];

        for (int index = 0; index < RowsPerCollection; index++)
        {
            Category category = Category.Create(
                budgetId,
                categoryGroups[0],
                $"Ordered category {index}",
                description: null,
                position: index,
                StampedAt(index));
            db.Categories.Add(category);
            categories.Add(category.Id);

            Transaction transaction = Transaction.Create(
                budgetId,
                accounts[0],
                -1.25m,
                UsdMinorUnit,
                InversionTransactionDate,
                description: null,
                StampedAt(index));
            db.Transactions.Add(transaction);
            transactions.Add(transaction.Id);
        }

        await db.SaveChangesAsync();

        return new InvertedSeed(accounts, categoryGroups, categories, payees, transactions);
    }

    /// <summary>
    /// The ids a collection returned, in the order it returned them.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> <see cref="SortedIds" />, which exists for the membership claims above
    /// and would erase the very thing this reads. Joined into one string so a failure names both
    /// sequences rather than reporting that two collections differ.
    /// </remarks>
    private static string IdsInOrder(JsonArray rows) =>
        string.Join(", ", rows.Select(row => row!["id"]!.GetValue<Guid>()));

    private static string InOrder(IEnumerable<Guid> ids) => string.Join(", ", ids);

    /// <summary>
    /// The ids of the two rows seeded into each budget-owned table, so every assertion keys on the
    /// same values the seeding wrote.
    /// </summary>
    /// <remarks>
    /// The payees are absent deliberately: no endpoint returns a payee id, the rows exist only
    /// because two transactions named them, and reading them back belongs with the other out-of-band
    /// read rather than in a record the HTTP seeding fills.
    /// </remarks>
    private sealed record SeededBudget(
        Guid CheckingAccountId,
        Guid SavingsAccountId,
        Guid EssentialsGroupId,
        Guid LifestyleGroupId,
        Guid GroceriesCategoryId,
        Guid TransportCategoryId,
        Guid LeisureCategoryId,
        Guid CoffeeTransactionId,
        Guid BusPassTransactionId);

    /// <summary>
    /// The wire names of the five collections a budget carries.
    /// </summary>
    /// <remarks>
    /// Written down here rather than read from a constant in <c>Application</c>, deliberately. These
    /// are camelCase because <c>ConfigureHttpJsonOptions</c> in <c>Api</c> says so, and a wire-name
    /// array living in <c>Application</c> would be a claim about the wire made in a layer that does not
    /// own it — and one this test could then no longer disagree with.
    /// </remarks>
    private static readonly string[] NestedCollectionNames =
        ["accounts", "categoryGroups", "categories", "payees", "transactions"];

    private const string CheckingName = "Checking";
    private const string SavingsName = "Savings";
    private const string EssentialsGroupName = "Essentials";
    private const string EssentialsGroupDescription = "Rent, food and the rest of the floor";
    private const string LifestyleGroupName = "Lifestyle";
    private const string LifestyleGroupDescription = "Everything that is a choice";
    private const string GroceriesCategoryName = "Groceries";
    private const string GroceriesCategoryDescription = "The weekly shop";
    private const string TransportCategoryName = "Transport";
    private const string TransportCategoryDescription = "Buses, trains and fuel";
    private const string LeisureCategoryName = "Leisure";
    private const string LeisureCategoryDescription = "Concerts and the cinema";
    private const string CoffeeShopPayeeName = "Kaffeine";
    private const string TransitPayeeName = "City Transit";
    private const string CoffeeDescription = "Flat white";
    private const string BusPassDescription = "Monthly bus pass";
    private const string CoffeeDate = "2026-06-26";
    private const string BusPassDate = "2026-07-01";

    /// <summary>
    /// Writes <b>two</b> rows into every budget-owned table over HTTP and returns the ids the
    /// assertions key on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rather than one everywhere, and that is the shape the whole file rests on: a single row is
    /// returned correctly by a read that takes the first row as well as by one that takes all of
    /// them, so a one-row seed cannot distinguish a complete export from a partial one.
    /// </para>
    /// <para>
    /// The money data goes in through the real endpoints, so every row is one the application itself
    /// could have written — same validation, same repositories, same least-privilege role. Both
    /// transactions name a payee, because naming one is the only thing in the product that writes that
    /// table.
    /// </para>
    /// <para>
    /// <b>Categories are the one collection seeded three deep rather than two</b>, and the third row is
    /// not a spare. Two categories under one group differ in position but carry the same
    /// <c>categoryGroupId</c>, and a projection that read the parent id once and stamped it on every
    /// row would satisfy both — the same argument this file makes about position, applied to the column
    /// beside it. The third sits under the other group, so the two claims hold at once: positions 0 and
    /// 1 under the first group, and a group id that is not constant down the collection.
    /// </para>
    /// <para>
    /// Duplicated from <c>ErasureAtomicityTests.FurnishAccountAsync</c> rather than extracted, which
    /// is the local convention stated at <c>ErasureAtomicityTests.cs:618-622</c>: several files
    /// already carry their own copy, and a drive-by extraction across them is a change to those files
    /// rather than to this one.
    /// </para>
    /// </remarks>
    private static async Task<SeededBudget> FurnishTwoOfEachAsync(HttpClient client)
    {
        Guid checkingAccountId = await CreateAsync(client, "/api/accounts", new
        {
            name = CheckingName,
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        Guid savingsAccountId = await CreateAsync(client, "/api/accounts", new
        {
            name = SavingsName,
            type = "Savings",
            openingBalance = 125.50m,
            currencyCode = "EUR",
        });

        Guid essentialsGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            name = EssentialsGroupName,
            description = EssentialsGroupDescription,
        });
        Guid lifestyleGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            name = LifestyleGroupName,
            description = LifestyleGroupDescription,
        });

        Guid groceriesCategoryId = await CreateAsync(client, "/api/categories", new
        {
            name = GroceriesCategoryName,
            description = GroceriesCategoryDescription,
            categoryGroupId = essentialsGroupId,
        });
        Guid transportCategoryId = await CreateAsync(client, "/api/categories", new
        {
            name = TransportCategoryName,
            description = TransportCategoryDescription,
            categoryGroupId = essentialsGroupId,
        });
        Guid leisureCategoryId = await CreateAsync(client, "/api/categories", new
        {
            name = LeisureCategoryName,
            description = LeisureCategoryDescription,
            categoryGroupId = lifestyleGroupId,
        });

        Guid coffeeTransactionId = await CreateAsync(client, "/api/transactions", new
        {
            amount = -10.25m,
            date = CoffeeDate,
            accountId = checkingAccountId,
            description = CoffeeDescription,
            payeeName = CoffeeShopPayeeName,
            categoryId = groceriesCategoryId,
        });
        Guid busPassTransactionId = await CreateAsync(client, "/api/transactions", new
        {
            amount = -25m,
            date = BusPassDate,
            accountId = savingsAccountId,
            description = BusPassDescription,
            payeeName = TransitPayeeName,
            categoryId = transportCategoryId,
        });

        return new SeededBudget(
            checkingAccountId,
            savingsAccountId,
            essentialsGroupId,
            lifestyleGroupId,
            groceriesCategoryId,
            transportCategoryId,
            leisureCategoryId,
            coffeeTransactionId,
            busPassTransactionId);
    }

    /// <summary>
    /// Asks for the export and hands back the parsed body, refusing anything but a success.
    /// </summary>
    /// <remarks>
    /// Parsed as <see cref="JsonNode" /> and never deserialized into the production records: a typed
    /// read would check the document against the same declarations that wrote it, and a property
    /// renamed on both sides at once would agree with itself while every saved file in the world
    /// disagreed.
    /// </remarks>
    private static async Task<JsonNode> GetExportAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync(ExportPath);
        response.EnsureSuccessStatusCode();

        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))
            ?? throw new InvalidOperationException("The export answered an empty body.");
    }

    /// <summary>
    /// The one budget an account owns today, refusing a document that carries any other number of
    /// them — a test reading <c>[0]</c> out of an empty array would fail on an index rather than on
    /// the claim it makes.
    /// </summary>
    private static JsonObject OnlyBudget(JsonNode document)
    {
        JsonArray budgets = document["budgets"]!.AsArray();

        return budgets.Count == 1
            ? budgets[0]!.AsObject()
            : throw new InvalidOperationException(
                $"Expected exactly one budget in the document, got {budgets.Count}.");
    }

    /// <summary>
    /// The row carrying <paramref name="id" />, refusing anything but exactly one.
    /// </summary>
    private static JsonObject RowFor(JsonArray rows, Guid id) =>
        rows.Select(row => row!.AsObject())
            .SingleOrDefault(row => row["id"]!.GetValue<Guid>() == id)
        ?? throw new InvalidOperationException($"The document carries no row with id '{id}'.");

    /// <summary>
    /// The ids a collection returned, ordered and joined into one string.
    /// </summary>
    /// <remarks>
    /// Joined rather than compared as sets so a failure names the ids on both sides instead of
    /// reporting that two sets differ. Ordered because the comparison here is about membership and
    /// nothing else — the ordering contract is a separate claim with a separate test, and folding it
    /// in would make one failure mean two things. Duplicates are kept rather than de-duplicated: a
    /// row returned twice is a defect, and a set comparison would hide it.
    /// </remarks>
    private static string SortedIds(JsonArray rows) =>
        Sorted([.. rows.Select(row => row!["id"]!.GetValue<Guid>())]);

    private static string Sorted(IReadOnlyCollection<Guid> ids) =>
        string.Join(", ", ids.Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal));

    /// <summary>
    /// That <paramref name="property" /> is on <paramref name="owner" /> and holds JSON <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Both halves, because <see cref="JsonNode" />'s indexer answers <see langword="null" /> for an
    /// absent property exactly as it does for one whose value is null. Without the containment check
    /// a column dropped from the projection entirely would satisfy every null assertion in this file.
    /// </remarks>
    private static async Task AssertJsonNullAsync(JsonObject owner, string property)
    {
        await Assert.That(owner.ContainsKey(property)).IsTrue();
        await Assert.That(owner[property]).IsNull();
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

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"Subject '{subject}' owns more than one budget; this lookup assumes exactly one.");
        }

        return (userId, budgetId);
    }

    /// <summary>
    /// The creation instant every row of <paramref name="table" /> carries, straight out of the table
    /// on the container superuser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the database rather than asserted against a window around the request, because the
    /// persisted instant is the only correct answer and a timestamp regenerated at serialization time
    /// would sit inside any window a test could draw.
    /// </para>
    /// <para>
    /// Superuser rather than the application role: <c>budget_isolation</c> is <c>FOR ALL</c> and this
    /// connection carries no ambient budget, so a policed connection would report an empty table and
    /// every lookup below it would fail as a missing key rather than as a wrong value. The table name
    /// is interpolated because an identifier cannot be a parameter, and every argument is a literal
    /// written in this file.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<Guid, DateTime>> ReadCreationInstantsAsync(
        PostgresTestHost host,
        string table)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new($"select id, created_at_utc from {table}", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Dictionary<Guid, DateTime> instants = [];

        while (await reader.ReadAsync())
        {
            instants.Add(reader.GetGuid(0), reader.GetDateTime(1));
        }

        return instants;
    }

    /// <summary>
    /// The payee rows the seeded transactions minted, keyed by name.
    /// </summary>
    /// <remarks>
    /// Out of band because no endpoint returns a payee id: the row exists only because a transaction
    /// named a payee, and reading it through <c>GET /api/payees</c> would couple this file to that
    /// endpoint's wire shape for a value it uses as an opaque identifier.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, Guid>> ReadPayeeIdsAsync(
        PostgresTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select name, id from payees", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Dictionary<string, Guid> payees = new(StringComparer.Ordinal);

        while (await reader.ReadAsync())
        {
            payees.Add(reader.GetString(0), reader.GetGuid(1));
        }

        return payees;
    }

    /// <summary>
    /// Posts <paramref name="body" /> and returns the id of the row it created, failing loudly on any
    /// status other than success. A seeding step that quietly did nothing would otherwise reach the
    /// assertions as an empty <see cref="Guid" /> nothing matches, one assertion too late to say why.
    /// </summary>
    private static async Task<Guid> CreateAsync(HttpClient client, string path, object body)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(path, body);
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
