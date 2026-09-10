using System.Reflection;
using Application.AccountKeys;
using Application.Accounts;
using Application.Accounts.GetAccounts;
using Application.Categories;
using Application.Categories.GetCategories;
using Application.CategoryGroups;
using Application.CategoryGroups.GetCategoryGroups;
using Application.Currencies;
using Application.Currencies.GetCurrencies;
using Application.Payees;
using Application.Payees.GetPayees;
using Application.Transactions;
using Application.Users;
using Application.Users.ExportData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;

namespace IntegrationTests;

/// <summary>
/// Which of the two things a list read is: one the caller is handed <b>whole</b>, or one this product
/// may later hand back a window of.
/// </summary>
/// <remarks>
/// No member of this enum is zero, so the CLR's <c>default</c> is not a disposition. Paired with
/// <see cref="ListReadRow.Delivery" /> having no default value, that is what stops a row nobody decided
/// about from compiling into existence: the author has to type one of these three words, and
/// <c>EveryRow_CarriesItsOwnReasonAndARouteOnlyWhenGated</c> refuses an undefined value besides.
/// </remarks>
public enum ListDelivery
{
    /// <summary>
    /// Held by this gate. The read takes nothing but a cancellation token, its query asks for nothing,
    /// its response is one list, and its route binds nothing but its handler.
    /// </summary>
    DeliveredWhole = 1,

    /// <summary>
    /// Outside the gate because the read is keyed on an owner rather than on the ambient budget. Its
    /// wholeness, where it matters, is held by a stronger rule somewhere else.
    /// </summary>
    ScopedByOwner = 2,

    /// <summary>
    /// Whole today, and deliberately not held that way. The server can order this list on a column it
    /// can read, so a page of it is stable, and paging it is a change the product intends to make.
    /// </summary>
    PageableLater = 3,
}

/// <summary>
/// The route a delivered-whole read is served over, and the three types the assertions read.
/// </summary>
/// <param name="Pattern">
/// The endpoint's <c>RoutePattern.RawText</c> exactly. A group prefix plus <c>MapGet("/")</c> produces a
/// <b>trailing slash</b> — <c>/api/accounts/</c>, not <c>/api/accounts</c> — which is measured rather
/// than assumed, and a pattern nothing answers to is reported rather than skipped.
/// </param>
/// <param name="Handler">The one type the route's delegate may bind besides a cancellation token.</param>
/// <param name="Query">The query record the handler takes; it must declare no properties.</param>
/// <param name="Response">The response record the handler returns; it must be exactly one list.</param>
public sealed record GatedRoute(string Pattern, Type Handler, Type Query, Type Response);

/// <summary>
/// One list read, its disposition, and the sentence a person wrote for <b>that</b> read.
/// </summary>
/// <param name="ReadService">The interface exactly as the Application assembly declares it.</param>
/// <param name="Member">The member name; a spelling nothing answers to is reported by the census.</param>
/// <param name="Delivery">
/// What this list is. <b>No default value</b>, so the disposition cannot be inherited by silence.
/// </param>
/// <param name="Reason">
/// Why <em>this</em> list has <em>that</em> disposition, in this row's own words. The four families of
/// reason in play are genuinely different — no server-side name order exists at all; a tree's positions
/// are relative to the whole set; a bounded reference set; an orderable column that can page stably —
/// so one sentence pasted across the rows would make the table claim less than it appears to. Required
/// to be non-trivial and <b>distinct across the table</b>, which is what stops the paste.
/// </param>
/// <param name="Route">
/// The route half, present exactly when <paramref name="Delivery" /> is
/// <see cref="ListDelivery.DeliveredWhole" />. A gated row missing it would skip Layer B in silence,
/// and <c>EveryRow_CarriesItsOwnReasonAndARouteOnlyWhenGated</c> refuses that pairing in both
/// directions. What that pairing cannot see is a row <b>demoted</b> out of
/// <see cref="ListDelivery.DeliveredWhole" />: the two halves are compared to each other, so dropping
/// the route beside the disposition satisfies it, and every assertion downstream filters on
/// <see cref="ListDelivery.DeliveredWhole" /> first — the row leaves both layers together. The gated
/// set is therefore pinned by name in
/// <c>DeliveredWholeReads_AreExactlyTheSetThisFileNames</c>, which is what makes a demotion audible.
/// </param>
public sealed record ListReadRow(
    Type ReadService,
    string Member,
    ListDelivery Delivery,
    string Reason,
    GatedRoute? Route = null);

/// <summary>
/// Every discovered list read sorted by how many rows claim it, plus the rows naming nothing.
/// </summary>
/// <param name="Unclassified">
/// Reads no row claims. This is the bucket a <b>new</b> list read lands in, and it is red rather than
/// empty-by-convenience: with three dispositions in play there is nothing to derive for a read nobody
/// has argued about, and picking one for it would be guessing.
/// </param>
/// <param name="ClaimedTwice">
/// Reads two rows claim. Red for the opposite reason: two rows means two answers to "is this whole",
/// and the one nobody reads is the one that rots.
/// </param>
/// <param name="NamingNoMember">
/// Rows naming a member the assembly does not declare — a rename, a deletion, or a typo. Reported
/// rather than dropped: such a name silently shrinks the census by one, and waits to hand a disposition
/// argued about one read to whatever is next called that.
/// </param>
/// <param name="Classified">
/// Reads exactly one row claims. Carried so a caller can prove the census found real subjects rather
/// than passing over an empty sequence.
/// </param>
public sealed record ListReadCensus(
    IReadOnlyList<string> Unclassified,
    IReadOnlyList<string> ClaimedTwice,
    IReadOnlyList<string> NamingNoMember,
    IReadOnlyList<string> Classified);

/// <summary>
/// Holds that the list reads this product delivers <b>whole</b> stay whole — no page, no cursor, no
/// filter, no search term — at the Application contract and again at the route table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The argument is in <c>docs/engineering/whole-list-reads.md</c></b> — the failure this exists for
/// and why it has no symptom a person would recognise as pagination, the four families of reason, what
/// neither layer reaches, and why the table below is deliberately editable. Read that chapter before
/// promoting a row into the gate or demoting one out of it. What is kept here is what a reader of
/// <em>this file</em> needs and the chapter does not carry.
/// </para>
/// <para>
/// <b>Do not paste one sentence across the rows below.</b> The four reasons are genuinely different:
/// no server-side <em>name</em> order exists for payees or accounts, which is exactly why
/// <c>AccountReadService</c> orders by the creation instant instead; categories and category groups
/// order by a readable integer and are gated for the shape of the tree that is rendered; currencies are
/// a bounded reference set; and the transaction list is whole today and deliberately outside the gate.
/// Each row argues its own case in its own <see cref="ListReadRow.Reason" />, and
/// <c>EveryRow_CarriesItsOwnReasonAndARouteOnlyWhenGated</c> refuses two rows that share one.
/// </para>
/// <para>
/// <b>Reading a red run here.</b> Nothing in this file reaches a database: Layer A is pure reflection,
/// and Layer B boots <see cref="ApiFactory" /> in <c>Production</c> over a bogus connection string and
/// reads the route table without making a request, the way <see cref="CompositionBoundaryTests" /> does.
/// A connect timeout is therefore a finding about this file rather than the suite's usual load noise.
/// Layer B is also why both layers live in the integration project — <c>tests/UnitTests</c> holds no
/// reference to Api, and the absence is a pinned row in <c>ProjectReferenceGraphTests</c>.
/// </para>
/// <para>
/// <b>The synthetic controls, which ship permanently rather than being run once and reverted.</b> Every
/// live assertion has one beside it, because a reflection query that silently returned nothing satisfies
/// every emptiness assertion while inspecting nothing at all:
/// <c>NarrowingParameters_NamesTheParameterOfAPagedRead</c>,
/// <c>Census_ReportsADiscoveredReadInNoRow</c>, <c>Census_ReportsARowNoMemberAnswersTo</c>,
/// <c>Census_AcceptsAReadClaimedByExactlyOneRow</c>,
/// <c>EveryListRead_ClassifiedAgainstAnEmptyTable_ComesBackUnclassified</c>,
/// <c>UnexpectedBindings_NamesAPagingParameterOnARoute</c>,
/// <c>UnexpectedBindings_NamesBothMembersOfAnAsParametersStruct</c> and
/// <c>UnexpectedBindings_NamesAQueryAHeaderAndANullableOptionalParameter</c>.
/// </para>
/// </remarks>
public sealed class WholeListDeliveryTests
{
    /// <summary>
    /// Every list read the Application assembly declares, with the disposition a person gave it.
    /// </summary>
    /// <remarks>
    /// Nine rows, matching the nine members discovery finds. Five are gated; one is whole and
    /// deliberately not gated; three are keyed on an owner rather than on the ambient budget. Read each
    /// <see cref="ListReadRow.Reason" /> rather than the bucket name — that is the member that stops
    /// this table from meaning less than it says.
    /// </remarks>
    private static readonly ListReadRow[] Table =
    [
        new(
            typeof(IPayeeReadService),
            nameof(IPayeeReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            "Only the client can order this list, because no server-side name order exists at all: "
            + "payees.name is an AEAD envelope drawn under a fresh nonce, so the first differing byte "
            + "of two seals is nonce rather than text and an ordering by it reshuffles on every "
            + "unrelated save. A page therefore has no stable meaning to cut at. It is also the list a "
            + "client resolves a counterparty against before it posts a transaction, so a name missing "
            + "from the page it was handed reads as a payee that must be created, and the create "
            + "collides on IX_payees_budget_id_name_key.",
            new GatedRoute(
                "/api/payees/",
                typeof(GetPayeesHandler),
                typeof(GetPayeesQuery),
                typeof(PayeeListResponse))),
        new(
            typeof(IAccountReadService),
            nameof(IAccountReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            "The payees argument applies — accounts.name is an envelope under a fresh nonce, so "
            + "AccountReadService deleted the orderby account.Name it used to carry and orders by the "
            + "creation instant instead — and this read carries a second half payees does not: the "
            + "client sorts accounts by the OPENED name today, in the byName helper of "
            + "accounts.service.ts. So a page boundary here does not merely forbid a future screen, it "
            + "breaks one that ships: each page would be sorted independently and the merged list "
            + "would be in no order a person recognises, with nothing thrown and nothing logged.",
            new GatedRoute(
                "/api/accounts/",
                typeof(GetAccountsHandler),
                typeof(GetAccountsQuery),
                typeof(AccountListResponse))),
        new(
            typeof(ICategoryGroupReadService),
            nameof(ICategoryGroupReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            "NOT the ordering argument, and a reader who assumes it is will widen this row wrongly. "
            + "category_groups.position is a readable integer and CategoryGroupReadService does order "
            + "by it, so a page of this list would be perfectly stable. The reason is what is rendered: "
            + "the categories screen draws one tree whose group positions are relative to the ENTIRE "
            + "set, so a page boundary splits the tree and leaves the client arranging branches it "
            + "cannot see the rest of.",
            new GatedRoute(
                "/api/category-groups/",
                typeof(GetCategoryGroupsHandler),
                typeof(GetCategoryGroupsQuery),
                typeof(CategoryGroupListResponse))),
        new(
            typeof(ICategoryReadService),
            nameof(ICategoryReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            "The other half of the same tree, and stated in its own words rather than deferred to the "
            + "group row, because the two are read together and a single shared sentence is how a "
            + "later author would come to believe one argument covers both tables. Category positions "
            + "are readable integers ordered within their group, and categories.service.ts sorts by "
            + "group position then category position then id — an ordering computed across the whole "
            + "set. A page of categories arriving without the groups they hang from, or without their "
            + "siblings, is a tree the client cannot assemble.",
            new GatedRoute(
                "/api/categories/",
                typeof(GetCategoriesHandler),
                typeof(GetCategoriesQuery),
                typeof(CategoryListResponse))),
        new(
            typeof(ICurrencyReadService),
            nameof(ICurrencyReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            "A bounded reference set the client picks from in full — shared data belonging to no "
            + "tenant, SELECT only, with no row-level-security policy, ordered by the ISO 4217 code the "
            + "server can read. Nothing about a nonce or a tree applies here. It is whole because a "
            + "picker offering a window of the currencies that exist is a picker that cannot be used to "
            + "choose the one the person wants, and because the set does not grow with anybody's data.",
            new GatedRoute(
                "/api/currencies/",
                typeof(GetCurrenciesHandler),
                typeof(GetCurrenciesQuery),
                typeof(CurrencyListResponse))),
        new(
            typeof(ITransactionReadService),
            nameof(ITransactionReadService.GetAllWithPayeeAsync),
            ListDelivery.PageableLater,
            "WHOLE TODAY AND DELIBERATELY OUTSIDE THE GATE, which is why it carries no route half. "
            + "TransactionReadService orders by transaction.Date descending then CreatedAtUtc "
            + "descending — both columns this server reads in the clear — so unlike every gated row "
            + "above, a page of this list is stable and a client does not have to reorder it. "
            + "Pagination for it is planned. Adding it to the gate would cost nothing today and would "
            + "forbid a change the product intends to make, which is the failure mode a table like "
            + "this one is most likely to produce: a rule that reads as caution and is really a "
            + "veto nobody argued for.",
            Route: null),
        new(
            typeof(IAccountKeyReadService),
            nameof(IAccountKeyReadService.ListForAccountAsync),
            ListDelivery.ScopedByOwner,
            "Keyed on the account rather than on the ambient budget, so it is not one of the ambient "
            + "list reads this gate is about. Its wholeness is held far more strongly than a route "
            + "assertion could: IAccountKeyReadService says in its own remarks that paging it would "
            + "hand somebody nine of their ten ways back into their account, and the set is bounded by "
            + "how many factors one account can hold rather than by anybody's data volume.",
            Route: null),
        new(
            typeof(ICredentialReadService),
            nameof(ICredentialReadService.ListForUserAsync),
            ListDelivery.ScopedByOwner,
            "Keyed on the account. It orders by CreatedAtUtc, a readable column, and the credentials "
            + "table is exempt from row-level security precisely because it is what a request's "
            + "identity is resolved out of — so the owner argument is the only thing that scopes it, "
            + "and an owner parameter is the opposite of a caller-supplied narrowing. Nothing about "
            + "this read is the ambient-budget shape the gate holds.",
            Route: null),
        new(
            typeof(IExportReadService),
            nameof(IExportReadService.ListOwnedBudgetsAsync),
            ListDelivery.ScopedByOwner,
            "Keyed on the account, and held by a stronger rule running in the OPPOSITE direction: the "
            + "export refuses rather than truncates, throwing unless the owned set is exactly the "
            + "ambient budget, by set equality in both directions. A gate saying merely 'this comes "
            + "back whole' would be a weaker claim sitting on top of a refusal, and the weaker one is "
            + "the sentence a later reader would quote.",
            Route: null),
    ];

    [Test]
    public async Task Discovery_FindsExactlyTheListReadsTheApplicationDeclares()
    {
        // Arrange
        Assembly application = typeof(IAccountReadService).Assembly;

        // Act
        IReadOnlyList<string> discovered = WholeListDelivery.DiscoveredIn(application);

        // Assert — pinning the set is what catches a list read that changes SHAPE rather than
        // arriving. Give one an owner parameter, or return a Task<PagedResult<T>>, and it leaves
        // discovery entirely: the census below would go on passing forever having found one fewer
        // subject, while this line goes red and a person has to say what the change meant.
        string[] expected =
        [
            "IAccountKeyReadService.ListForAccountAsync",
            "IAccountReadService.GetAllAsync",
            "ICategoryGroupReadService.GetAllAsync",
            "ICategoryReadService.GetAllAsync",
            "ICredentialReadService.ListForUserAsync",
            "ICurrencyReadService.GetAllAsync",
            "IExportReadService.ListOwnedBudgetsAsync",
            "IPayeeReadService.GetAllAsync",
            "ITransactionReadService.GetAllWithPayeeAsync",
        ];

        // IsEmpty on the difference rather than the string.Join form two files nearby use: a census
        // reporting through string.Join truncates its message and names only the first offender, so a
        // set that drifted by three reads as a set that drifted by one.
        await Assert.That(discovered.Except(expected, StringComparer.Ordinal)).IsEmpty();
        await Assert.That(expected.Except(discovered, StringComparer.Ordinal)).IsEmpty();

        // Non-vacuity. A reflection query that came back empty satisfies the first line above and
        // says nothing to anybody.
        await Assert.That(discovered.Count).IsEqualTo(expected.Length);
        await Assert.That(discovered.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task EveryListRead_IsClaimedByExactlyOneRow()
    {
        // Arrange
        Assembly application = typeof(IAccountReadService).Assembly;
        IReadOnlyList<string> discovered = WholeListDelivery.DiscoveredIn(application);

        // Act
        ListReadCensus census = WholeListDelivery.Take(discovered, Table.Select(WholeListDelivery.KeyOf));

        // Assert — IsEmpty on the offender collections, so a failure names every one of them.
        await Assert.That(census.Unclassified).IsEmpty();
        await Assert.That(census.ClaimedTwice).IsEmpty();
        await Assert.That(census.NamingNoMember).IsEmpty();

        // Non-vacuity, twice over: the classifier saw every discovered read, and there was at least
        // one to see.
        await Assert.That(census.Classified.Count).IsEqualTo(discovered.Count);
        await Assert.That(census.Classified.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task EveryRow_CarriesItsOwnReasonAndARouteOnlyWhenGated()
    {
        // Act — three ways a row can be present and mean nothing.
        IReadOnlyList<string> undecided =
        [
            .. Table
                .Where(row => !Enum.IsDefined(row.Delivery))
                .Select(WholeListDelivery.KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        // A reason that is blank, or a word, satisfies a census perfectly and tells the next reader
        // nothing — the prose failure one layer in. Sixty characters is not a quality bar; it is the
        // floor beneath which "n/a" and "see above" live.
        IReadOnlyList<string> silent =
        [
            .. Table
                .Where(row => string.IsNullOrWhiteSpace(row.Reason) || row.Reason.Trim().Length < 60)
                .Select(WholeListDelivery.KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        // The paste this table exists to make expensive.
        IReadOnlyList<string> sharedReasons =
        [
            .. Table
                .GroupBy(row => row.Reason, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group.Select(WholeListDelivery.KeyOf))
                .Order(StringComparer.Ordinal),
        ];

        // A gated row with no route half would skip Layer B in silence. The other direction matters
        // too: a route hung on an ungated row is a claim nothing checks.
        //
        // This compares the two halves TO EACH OTHER, so it says nothing about a row demoted out of
        // DeliveredWhole — measured: flipping the payee row to PageableLater and dropping its route
        // together leaves this test green, and left the whole suite green before the pin below was
        // written. Demotion takes a row out of both layers at once.
        // DeliveredWholeReads_AreExactlyTheSetThisFileNames is what catches that,
        // and it is deliberately a pin rather than a prohibition: pagination for the transaction
        // list is planned and will one day need exactly this edit. The point is that widening the
        // gate costs a written reason a reviewer can weigh, not that it is impossible.
        IReadOnlyList<string> mismatchedRoutes =
        [
            .. Table
                .Where(row => (row.Delivery is ListDelivery.DeliveredWhole) != (row.Route is not null))
                .Select(WholeListDelivery.KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(undecided).IsEmpty();
        await Assert.That(silent).IsEmpty();
        await Assert.That(sharedReasons).IsEmpty();
        await Assert.That(mismatchedRoutes).IsEmpty();
        await Assert.That(Table.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task DeliveredWholeReads_AreExactlyTheSetThisFileNames()
    {
        // Arrange — the second half of the pin Discovery_FindsExactlyTheListReadsTheApplicationDeclares
        // starts. That one holds which reads EXIST; this one holds which of them this gate is about,
        // and the two are different facts. Every assertion that reads a signature, a query, a response
        // or a route filters on DeliveredWhole before it reads anything, so the size of this set is
        // the size of what those four inspect. The census and the reason assertions still cover the
        // whole table and are not what this is about.
        //
        // MEASURED, which is why it is here. Before this test existed, flipping the payee row to
        // PageableLater and dropping its route in the same edit left the suite green while real
        // pagination shipped on GetPayeesQuery and on the live route: a demotion takes a row out of
        // both layers at once, and nothing else notices. Measured again with this test in place, that
        // same mutation reddens exactly one test in this project — this one — and the failure names
        // the row it took out.
        //
        // A PIN AND NOT A PROHIBITION. Demoting a row is a change this product intends to make — the
        // transaction list is already outside the gate for exactly that reason, and a gated row could
        // follow it the day its own Reason stops holding. What this costs a person is one line here
        // and a rewritten Reason on the row, in a diff a reviewer can weigh. What it buys is that
        // neither edit can be made by accident.
        //
        // THE LIMIT, stated rather than papered over: this holds the SET and nothing machine-checkable
        // holds the row's prose against its disposition. A demoted row can keep a Reason still arguing
        // the gated case — measured, one did. Nothing cheap fixes that, because a keyword rule over
        // free prose is satisfied by pasting the keyword. What stands in for it is the red above: the
        // person clearing it edits this list and that row in one diff, with the stale sentence in it.
        string[] expected =
        [
            "IAccountReadService.GetAllAsync",
            "ICategoryGroupReadService.GetAllAsync",
            "ICategoryReadService.GetAllAsync",
            "ICurrencyReadService.GetAllAsync",
            "IPayeeReadService.GetAllAsync",
        ];

        // Act
        IReadOnlyList<string> gated =
        [
            .. Table
                .Where(row => row.Delivery is ListDelivery.DeliveredWhole)
                .Select(WholeListDelivery.KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        // Assert — the two directions are named apart, because they are two different edits and the
        // failure message should say which one happened. IsEmpty on the difference rather than a
        // joined string, for the reason the discovery pin gives: a set that drifted by three would
        // otherwise read as a set that drifted by one.
        IReadOnlyList<string> demoted = [.. expected.Except(gated, StringComparer.Ordinal)];
        IReadOnlyList<string> promoted = [.. gated.Except(expected, StringComparer.Ordinal)];

        await Assert.That(demoted).IsEmpty();
        await Assert.That(promoted).IsEmpty();

        // The count is not a restatement of the two lines above: a row DUPLICATED in the table leaves
        // both differences empty and moves this one. The last line is the emptiness control the rest
        // of this file writes, and here it is redundant — an empty gated set already reddens the
        // demoted line — so it is kept for the shape rather than for what it catches.
        await Assert.That(gated.Count).IsEqualTo(expected.Length);
        await Assert.That(gated.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task EveryDeliveredWholeRead_TakesNothingButACancellationToken()
    {
        // Arrange
        ListReadRow[] gated = [.. Table.Where(row => row.Delivery is ListDelivery.DeliveredWhole)];

        // Act — the allow-list at the contract layer. Anything a caller could pass that is not the
        // cancellation token is a way to ask for less than everything, whatever it is called.
        IReadOnlyList<string> narrowing =
        [
            .. gated
                .SelectMany(row => WholeListDelivery
                    .NarrowingParametersOf(WholeListDelivery.MemberOf(row))
                    .Select(parameter => $"{WholeListDelivery.KeyOf(row)} takes {parameter}"))
                .Order(StringComparer.Ordinal),
        ];

        IReadOnlyList<string> wrongArity =
        [
            .. gated
                .Where(row => WholeListDelivery.MemberOf(row).GetParameters() is not [_])
                .Select(WholeListDelivery.KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        // Optional, so no caller is ever forced to name anything to read the list at all.
        IReadOnlyList<string> mandatoryToken =
        [
            .. gated
                .Where(row => WholeListDelivery.MemberOf(row).GetParameters()
                    is not [{ IsOptional: true, ParameterType.IsValueType: true }])
                .Select(WholeListDelivery.KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(narrowing).IsEmpty();
        await Assert.That(wrongArity).IsEmpty();
        await Assert.That(mandatoryToken).IsEmpty();
        await Assert.That(gated.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task EveryDeliveredWholeRead_AsksForNothingAndAnswersWithOneList()
    {
        // Arrange
        ListReadRow[] gated = [.. Table.Where(row => row.Delivery is ListDelivery.DeliveredWhole)];

        // Act — a query record with a property is a query with a knob on it, whether the knob is
        // called Page, Skip or SearchTerm.
        IReadOnlyList<string> askingQueries =
        [
            .. gated
                .SelectMany(row => WholeListDelivery
                    .PublicPropertiesOf(WholeListDelivery.RouteOf(row).Query)
                    .Select(property =>
                        $"{WholeListDelivery.RouteOf(row).Query.Name} declares {property.Name}"))
                .Order(StringComparer.Ordinal),
        ];

        // Exactly one property, and it is the list. Stronger than "carries one IReadOnlyList<>",
        // deliberately: a response carrying Items AND NextCursor, or Items AND TotalCount, satisfies
        // the weaker sentence while being precisely the shape this file exists to refuse.
        IReadOnlyList<string> answeringMoreThanAList =
        [
            .. gated
                .Where(row => WholeListDelivery
                        .PublicPropertiesOf(WholeListDelivery.RouteOf(row).Response)
                    is not [{ } sole] || !WholeListDelivery.IsReadOnlyList(sole.PropertyType))
                .Select(row => WholeListDelivery.RouteOf(row).Response.Name)
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(askingQueries).IsEmpty();
        await Assert.That(answeringMoreThanAList).IsEmpty();

        // Non-vacuity: PublicPropertiesOf returning nothing for everything would satisfy the first
        // line above and hollow out the second, so one known list property is asserted to be found.
        await Assert.That(WholeListDelivery.PublicPropertiesOf(typeof(PayeeListResponse)).Count)
            .IsEqualTo(1);
        await Assert.That(gated.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task EveryDeliveredWholeRoute_BindsNothingButItsHandlerAndACancellationToken()
    {
        // Arrange — Production over a connection string nothing answers on, exactly as
        // CompositionBoundaryTests does: the route table is built at startup and no request is made,
        // so no database is reached and no container is needed. A connect timeout here would be a
        // finding about this test rather than the suite's usual load noise.
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        ListReadRow[] gated = [.. Table.Where(row => row.Delivery is ListDelivery.DeliveredWhole)];

        // Act
        List<string> unroutable = [];
        List<string> unexpected = [];
        int inspectedBindings = 0;

        foreach (ListReadRow row in gated)
        {
            GatedRoute route = WholeListDelivery.RouteOf(row);
            RouteEndpoint[] matches =
            [
                .. dataSource.Endpoints
                    .OfType<RouteEndpoint>()
                    .Where(endpoint =>
                        string.Equals(endpoint.RoutePattern.RawText, route.Pattern, StringComparison.Ordinal)
                        && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()
                            ?.HttpMethods.Contains(HttpMethods.Get) is true),
            ];

            // Exactly one, never "the first". A pattern nothing answers to means this row is checking
            // nothing, and two matches means the assertion below picked one of them arbitrarily.
            if (matches is not [RouteEndpoint endpoint])
            {
                unroutable.Add($"{route.Pattern} matched {matches.Length} GET endpoints");
                continue;
            }

            inspectedBindings += WholeListDelivery.BoundParametersOf(endpoint).Count;
            unexpected.AddRange(WholeListDelivery
                .UnexpectedBindingsOf(endpoint, route.Handler)
                .Select(parameter => $"{route.Pattern} binds {parameter}"));
        }

        // Assert — IsEmpty on the offender collections rather than a joined string, so a route table
        // that grew three paging parameters names all three.
        await Assert.That(unroutable).IsEmpty();
        await Assert.That(unexpected).IsEmpty();

        // Non-vacuity, three ways. The route table is EMPTY before the host starts — measured, 0
        // endpoints before StartAsync and the full set after — so a factory that failed to start
        // would leave every list above empty and this test would pass having inspected nothing.
        // WebApplicationFactory starts the host when Services is first read, which is what makes the
        // arrangement above sufficient; these three lines are what proves it did.
        await Assert.That(dataSource.Endpoints.Count).IsGreaterThan(0);
        await Assert.That(inspectedBindings).IsGreaterThan(0);
        await Assert.That(gated.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task NarrowingParameters_NamesTheParameterOfAPagedRead()
    {
        // Arrange — the defect, built here rather than staged in the Application assembly and
        // reverted, so the proof is permanent. A read service whose list member has grown a page.
        Type[] types = [typeof(IPagedProbeReadService)];

        // Act
        IReadOnlyList<MethodInfo> members = WholeListDelivery.MembersIn(types);
        IReadOnlyList<string> narrowing = WholeListDelivery.NarrowingParametersOf(members[0]);

        // Assert — the shape filter finds it (it does return Task<IReadOnlyList<T>>), and the
        // classifier names the parameter rather than merely counting it.
        await Assert.That(members.Count).IsEqualTo(1);
        await Assert.That(narrowing).IsEquivalentTo(new[] { "page" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Census_ReportsADiscoveredReadInNoRow()
    {
        // Arrange — the case this census exists for: a list read arrives and nobody says whether it
        // is delivered whole. Synthetic names, so the proof survives the day the real table is right.
        string[] discovered =
        [
            "IGatedReadService.GetAllAsync",
            "IExemptReadService.ListForAccountAsync",
            "IForgottenReadService.FeedAsync",
        ];

        // Act
        ListReadCensus census = WholeListDelivery.Take(
            discovered, ["IGatedReadService.GetAllAsync", "IExemptReadService.ListForAccountAsync"]);

        // Assert — named, not counted. "1 read is unclassified" sends the reader back to the
        // assembly to work out which. Note the forgotten one is called FeedAsync: a name filter would
        // never have found it, which is the whole reason discovery is structural.
        await Assert.That(census.Unclassified)
            .IsEquivalentTo(new[] { "IForgottenReadService.FeedAsync" });
        await Assert.That(census.Classified.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Census_ReportsARowNoMemberAnswersTo()
    {
        // Arrange — a rename, or a member deleted with the screen that used it. The stale row is not
        // harmless: it silently shrinks the census, and it waits to hand a disposition argued about
        // one read to whatever is next called that.
        string[] discovered = ["ILiveReadService.GetAllAsync"];

        // Act
        ListReadCensus census = WholeListDelivery.Take(
            discovered, ["ILiveReadService.GetAllAsync", "IGhostReadService.GetAllAsync"]);

        // Assert
        await Assert.That(census.NamingNoMember)
            .IsEquivalentTo(new[] { "IGhostReadService.GetAllAsync" });
        await Assert.That(census.Classified)
            .IsEquivalentTo(new[] { "ILiveReadService.GetAllAsync" });
    }

    [Test]
    public async Task Census_AcceptsAReadClaimedByExactlyOneRow()
    {
        // Arrange — without this, a classifier that reported every read in every bucket would satisfy
        // both cases above while making the live assertions fire on a table nobody has broken.
        string[] discovered = ["IGatedReadService.GetAllAsync", "IExemptReadService.FeedAsync"];

        // Act
        ListReadCensus census = WholeListDelivery.Take(discovered, discovered);

        // Assert
        await Assert.That(census.Unclassified).IsEmpty();
        await Assert.That(census.ClaimedTwice).IsEmpty();
        await Assert.That(census.NamingNoMember).IsEmpty();
        await Assert.That(census.Classified).IsEquivalentTo(discovered);
    }

    [Test]
    public async Task EveryListRead_ClassifiedAgainstAnEmptyTable_ComesBackUnclassified()
    {
        // Arrange — the control the synthetic cases cannot supply. They prove the classifier sorts
        // strings; this proves the LIVE subject reaches it, which is what the census would silently
        // stop doing if discovery ever narrowed to nothing.
        Assembly application = typeof(IAccountReadService).Assembly;
        IReadOnlyList<string> discovered = WholeListDelivery.DiscoveredIn(application);

        // Act
        ListReadCensus census = WholeListDelivery.Take(discovered, []);

        // Assert — every real list read comes back unclaimed, so the red direction is demonstrated
        // against real input rather than only against strings this file made up.
        await Assert.That(census.Unclassified).IsEquivalentTo(discovered);
        await Assert.That(census.Unclassified.Count).IsGreaterThan(0);
        await Assert.That(census.Classified).IsEmpty();
    }

    [Test]
    public async Task UnexpectedBindings_NamesAPagingParameterOnARoute()
    {
        // Arrange — a throwaway route table of this file's own, so the defect ships as a permanent
        // demonstration instead of a paragraph about an edit that was reverted. TestServer rather
        // than a real socket: nothing here makes a request, only the route table is read.
        await using WebApplication app = ThrowawayApp();
        app.MapGet(
            "/probe/paged",
            (ThrowawayHandler handler, int page, CancellationToken cancellationToken) => handler.Answer());

        // The route table is EMPTY before StartAsync — measured on a host of this shape, 0 endpoints
        // before and the full set after. Reading the data source without starting is the silent-pass
        // trap this whole control would otherwise fall into.
        await app.StartAsync();
        RouteEndpoint endpoint = SoleEndpointOf(app);

        // Act
        IReadOnlyList<string> unexpected =
            WholeListDelivery.UnexpectedBindingsOf(endpoint, typeof(ThrowawayHandler));

        // Assert
        await Assert.That(unexpected).IsEquivalentTo(new[] { "page" }, CollectionOrdering.Matching);
        await Assert.That(WholeListDelivery.BoundParametersOf(endpoint).Count).IsEqualTo(3);
        await app.StopAsync();
    }

    [Test]
    public async Task UnexpectedBindings_NamesBothMembersOfAnAsParametersStruct()
    {
        // Arrange — the measurement that makes Layer B worth building rather than folding into a
        // signature read. An [AsParameters] wrapper flattens into IParameterBindingMetadata as one
        // entry PER MEMBER and the wrapper itself does not appear, while MethodInfo.GetParameters()
        // shows the wrapper and none of its members. A signature read sees one harmless struct here;
        // binding metadata sees two paging knobs.
        await using WebApplication app = ThrowawayApp();
        app.MapGet(
            "/probe/wrapped",
            (ThrowawayHandler handler, [AsParameters] PagingWindow window, CancellationToken cancellationToken) =>
                handler.Answer());

        await app.StartAsync();
        RouteEndpoint endpoint = SoleEndpointOf(app);

        // Act
        IReadOnlyList<string> unexpected =
            WholeListDelivery.UnexpectedBindingsOf(endpoint, typeof(ThrowawayHandler));

        // Assert — BOTH members, separately. CollectionOrdering.Matching is named explicitly because
        // IsEquivalentTo defaults to CollectionOrdering.Any, and the order here is declaration order,
        // which is what proves the members arrived as distinct entries rather than as one blob.
        await Assert.That(unexpected)
            .IsEquivalentTo(new[] { "Page", "PageSize" }, CollectionOrdering.Matching);

        // The control for the sentence above: the signature shows the wrapper and not its members, so
        // a Layer B built on GetParameters would have found nothing to report. Pattern-matched rather
        // than asserted and then dereferenced — IsNotNull does not narrow for the compiler, so the line
        // beneath it would carry a null-forgiving operator and would die unnamed if a route delegate
        // ever stopped recording its MethodInfo.
        if (endpoint.Metadata.GetMetadata<MethodInfo>() is not { } signature)
        {
            throw new InvalidOperationException(
                "The throwaway route records no MethodInfo, so this control has no signature to hold "
                + "the flattened bindings against and would be comparing nothing.");
        }

        await Assert
            .That(signature.GetParameters().Select(parameter => parameter.Name ?? "<unnamed>"))
            .IsEquivalentTo(new[] { "handler", "window", "cancellationToken" }, CollectionOrdering.Matching);
        await app.StopAsync();
    }

    [Test]
    public async Task UnexpectedBindings_NamesAQueryAHeaderAndANullableOptionalParameter()
    {
        // Arrange — the three binding shapes the classifier is claimed to reach and that no other
        // control here demonstrates. The two beside this one bind a plain int and an [AsParameters]
        // struct, so on their evidence alone the classifier is only known to see required, unadorned
        // parameters. Each of these three is a way to hang a narrowing knob on a route that LOOKS
        // unlike a page:
        //
        //   int? cursor            — nullable, therefore optional; a caller may omit it entirely.
        //   [FromQuery] window     — bound from an explicitly named query source rather than by
        //                            convention, and named 'window' rather than 'page' precisely
        //                            because the classifier is an allow-list and must not care.
        //   [FromHeader] token     — not in the URL at all, so a reader of the route pattern sees
        //                            nothing. This is also the shape that would go quiet on its own
        //                            if a later .NET stopped recording header binds in
        //                            IParameterBindingMetadata, and this control is the only thing
        //                            in the suite that would notice.
        await using WebApplication app = ThrowawayApp();
        app.MapGet(
            "/probe/varied",
            (
                ThrowawayHandler handler,
                int? cursor,
                [FromQuery] string? window,
                [FromHeader] string? token,
                CancellationToken cancellationToken) => handler.Answer());

        await app.StartAsync();
        RouteEndpoint endpoint = SoleEndpointOf(app);

        // Act
        IReadOnlyList<string> unexpected =
            WholeListDelivery.UnexpectedBindingsOf(endpoint, typeof(ThrowawayHandler));

        // Assert — all three, by their PARAMETER names and in declaration order.
        // CollectionOrdering.Matching is named explicitly because IsEquivalentTo defaults to
        // CollectionOrdering.Any; here the order is what shows the three arrived as three distinct
        // entries rather than as a set that happens to contain the right words.
        await Assert.That(unexpected)
            .IsEquivalentTo(new[] { "cursor", "window", "token" }, CollectionOrdering.Matching);

        // Non-vacuity: the handler and the cancellation token are bound too and are the two the
        // classifier is supposed to drop, so five in and three out is what says it filtered rather
        // than failed to see anything.
        await Assert.That(WholeListDelivery.BoundParametersOf(endpoint).Count).IsEqualTo(5);
        await app.StopAsync();
    }

    /// <summary>
    /// A host with a route table and nothing else — no database, no socket, no application code.
    /// </summary>
    private static WebApplication ThrowawayApp()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ThrowawayHandler>();

        return builder.Build();
    }

    /// <summary>
    /// The one endpoint a throwaway app mapped, refusing anything else.
    /// </summary>
    /// <remarks>
    /// Refuses rather than taking the first, for the reason the live route lookup gives: a control
    /// that silently inspected the wrong endpoint would report an empty offender list and read as the
    /// classifier working.
    /// </remarks>
    private static RouteEndpoint SoleEndpointOf(WebApplication app)
    {
        RouteEndpoint[] endpoints =
        [
            .. app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
        ];

        return endpoints is [RouteEndpoint sole]
            ? sole
            : throw new InvalidOperationException(
                $"A throwaway app mapped one route and its data source holds {endpoints.Length}. The "
                + "route table is empty until StartAsync, so this usually means the host was never "
                + "started and the control was about to inspect nothing.");
    }

    /// <summary>
    /// A read service whose list member has grown a page, for the control that proves the contract
    /// classifier names it.
    /// </summary>
    /// <remarks>
    /// Nested and private, so it can never be mistaken for a port. Discovery over the Application
    /// assembly cannot see it; the control hands it to <see cref="WholeListDelivery.MembersIn" />
    /// directly, which is the same seam <c>RepositoryAttributionCensusTests</c> opens for its
    /// synthetic input.
    /// </remarks>
    private interface IPagedProbeReadService
    {
        Task<IReadOnlyList<string>> GetAllAsync(int page, CancellationToken cancellationToken = default);
    }

    /// <summary>Two paging knobs inside a struct, for the <c>[AsParameters]</c> control.</summary>
    public readonly record struct PagingWindow(int Page, int PageSize);

    /// <summary>Stands in for a query handler on a throwaway route.</summary>
    public sealed class ThrowawayHandler
    {
        public string Answer() => "unread";
    }

    /// <summary>
    /// Finds the list reads the Application assembly declares, sorts them against the written table,
    /// and names what a member or a route offers a caller besides everything.
    /// </summary>
    /// <remarks>
    /// <see cref="Take" /> takes the written keys as a parameter rather than reading
    /// <see cref="Table" />, and <see cref="MembersIn" /> takes types rather than reaching for the
    /// assembly itself. That is what lets the synthetic cases prove both failure directions without
    /// anybody deleting a real row to watch the suite go red — the same seam
    /// <c>RepositoryAttribution.Take</c> opens for the same reason.
    /// </remarks>
    private static class WholeListDelivery
    {
        /// <summary>The key one read is known by, in both the census and the table.</summary>
        internal static string KeyOf(ListReadRow row)
        {
            ArgumentNullException.ThrowIfNull(row);

            return $"{row.ReadService.Name}.{row.Member}";
        }

        /// <summary>
        /// The same key, taken from a discovered member rather than from a written row. Throws rather
        /// than dereferencing through <c>!</c>, so the day discovery is fed members from somewhere
        /// else this names itself instead of dying as a bare <c>NullReferenceException</c>.
        /// </summary>
        internal static string KeyOf(MethodInfo member)
        {
            ArgumentNullException.ThrowIfNull(member);

            return member.DeclaringType is { } declaring
                ? $"{declaring.Name}.{member.Name}"
                : throw new InvalidOperationException(
                    $"'{member.Name}' was discovered with no declaring type, which reflection over an "
                    + "interface's own members cannot produce; the discovery filter has changed.");
        }

        /// <summary>
        /// The route half of a gated row, refusing a gated row that has none. Measured: without this,
        /// a row gated with no route made the two assertion loops die on a bare
        /// <c>NullReferenceException</c> naming nothing, beside the one test that named the row.
        /// </summary>
        internal static GatedRoute RouteOf(ListReadRow row)
        {
            ArgumentNullException.ThrowIfNull(row);

            return row.Route
                   ?? throw new InvalidOperationException(
                       $"{KeyOf(row)} is marked {row.Delivery} and carries no route, so this assertion "
                       + "has nothing to read. EveryRow_CarriesItsOwnReasonAndARouteOnlyWhenGated is the "
                       + "test that says so in full.");
        }

        /// <summary>The member a row names, refusing a spelling the interface does not declare.</summary>
        /// <remarks>
        /// A row whose member has been renamed is already reported by the census as naming no member;
        /// this throw is what stops the parameter assertions from reading a null in the meantime.
        /// </remarks>
        internal static MethodInfo MemberOf(ListReadRow row)
        {
            ArgumentNullException.ThrowIfNull(row);

            return row.ReadService.GetMethod(row.Member)
                   ?? throw new InvalidOperationException(
                       $"{row.ReadService.Name} declares no member called '{row.Member}'.");
        }

        /// <summary>Every list read the assembly declares, ordered by key.</summary>
        internal static IReadOnlyList<string> DiscoveredIn(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            return
            [
                .. MembersIn(assembly.GetTypes())
                    .Select(KeyOf)
                    .Order(StringComparer.Ordinal),
            ];
        }

        /// <summary>
        /// The subject filter, applied to types rather than to an assembly so it can be aimed at
        /// synthetic input.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Structural, and the two halves are chosen rather than convenient.</b> The interface half
        /// is a name suffix, because <c>ReadService</c> is the word this codebase uses for the port a
        /// projection lives behind and there is no base type or attribute to key on. The member half is
        /// the <b>return shape</b> — <c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c> — and never the member
        /// name, because the transaction list is called <c>GetAllWithPayeeAsync</c> and a
        /// <c>GetAll</c> filter would already be missing it today. A list read called
        /// <c>FeedAsync</c> is found by this and by no name rule anybody would think to write.
        /// </para>
        /// <para>
        /// <b>The honest limit</b>, stated rather than fixed by widening: a list read that stops
        /// returning <c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c> — an <c>IAsyncEnumerable&lt;T&gt;</c>,
        /// a <c>Task&lt;PagedResult&lt;T&gt;&gt;</c>, or a port whose name loses the suffix — leaves
        /// discovery entirely and the census would go on passing with one fewer subject. Widening the
        /// filter only moves that boundary; what covers it instead is
        /// <c>Discovery_FindsExactlyTheListReadsTheApplicationDeclares</c>, which pins the nine keys,
        /// so a read that changes shape goes red there rather than quietly leaving.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<MethodInfo> MembersIn(IEnumerable<Type> types)
        {
            ArgumentNullException.ThrowIfNull(types);

            return
            [
                .. types
                    .Where(type => type.IsInterface)
                    .Where(type => type.Name.EndsWith("ReadService", StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods().Select(member => (Interface: type, Member: member)))
                    .Where(found => ReturnsAList(found.Member.ReturnType))
                    .OrderBy(found => found.Interface.Name, StringComparer.Ordinal)
                    .ThenBy(found => found.Member.Name, StringComparer.Ordinal)
                    .Select(found => found.Member),
            ];
        }

        /// <summary>
        /// Sorts each discovered read into unclassified, claimed twice, or classified, and reports the
        /// written keys nothing answers to.
        /// </summary>
        internal static ListReadCensus Take(IEnumerable<string> discovered, IEnumerable<string> listed)
        {
            ArgumentNullException.ThrowIfNull(discovered);
            ArgumentNullException.ThrowIfNull(listed);

            // Ordinal throughout: a member differing only by case is a different member, and a loose
            // comparison would let one row claim a read it does not name.
            string[] rows = [.. listed];
            Dictionary<string, int> claims = new(StringComparer.Ordinal);
            foreach (string key in rows)
            {
                claims[key] = claims.GetValueOrDefault(key) + 1;
            }

            List<string> unclassified = [];
            List<string> claimedTwice = [];
            List<string> classified = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (string read in discovered)
            {
                seen.Add(read);

                switch (claims.GetValueOrDefault(read))
                {
                    case 0:
                        unclassified.Add(read);
                        break;
                    case 1:
                        classified.Add(read);
                        break;
                    default:
                        claimedTwice.Add(read);
                        break;
                }
            }

            List<string> namingNoMember =
            [
                .. rows
                    .Where(key => !seen.Contains(key))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];

            return new ListReadCensus(unclassified, claimedTwice, namingNoMember, classified);
        }

        /// <summary>
        /// Everything a caller could pass this member besides the cancellation token, by name.
        /// </summary>
        /// <remarks>
        /// An <b>allow-list</b>, never a deny-list over parameter names. A deny-list holding
        /// <c>page</c>, <c>skip</c>, <c>take</c> and <c>cursor</c> is a list somebody escapes by
        /// writing <c>windowStart</c>, and it would have to be extended by the same person who is
        /// adding the parameter.
        /// </remarks>
        internal static IReadOnlyList<string> NarrowingParametersOf(MethodInfo member)
        {
            ArgumentNullException.ThrowIfNull(member);

            return
            [
                .. member.GetParameters()
                    .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
                    .Select(parameter => parameter.Name ?? "<unnamed>"),
            ];
        }

        /// <summary>Every parameter the route binds, flattened as ASP.NET Core records them.</summary>
        internal static IReadOnlyList<string> BoundParametersOf(Endpoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);

            return
            [
                .. endpoint.Metadata
                    .GetOrderedMetadata<IParameterBindingMetadata>()
                    .Select(binding => binding.Name),
            ];
        }

        /// <summary>
        /// Everything the route binds besides <paramref name="handler" /> and a cancellation token.
        /// </summary>
        /// <remarks>
        /// Reads <see cref="IParameterBindingMetadata" /> rather than the delegate's
        /// <c>MethodInfo</c>, and that is the whole reason this layer exists beside the contract one:
        /// an <c>[AsParameters]</c> struct's members appear here as separate entries while the wrapper
        /// does not, so a paging knob hidden inside a struct is named here and invisible to a
        /// signature read.
        /// </remarks>
        internal static IReadOnlyList<string> UnexpectedBindingsOf(Endpoint endpoint, Type handler)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(handler);

            return
            [
                .. endpoint.Metadata
                    .GetOrderedMetadata<IParameterBindingMetadata>()
                    .Where(binding => binding.ParameterInfo.ParameterType != handler
                                      && binding.ParameterInfo.ParameterType != typeof(CancellationToken))
                    .Select(binding => binding.Name),
            ];
        }

        /// <summary>
        /// The public instance properties a record declares — <c>EqualityContract</c> is protected and
        /// so is not one of them.
        /// </summary>
        internal static IReadOnlyList<PropertyInfo> PublicPropertiesOf(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            return
            [
                .. type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(property => property.Name, StringComparer.Ordinal),
            ];
        }

        /// <summary>Whether a type is a constructed <see cref="IReadOnlyList{T}" />.</summary>
        internal static bool IsReadOnlyList(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            return type.IsGenericType
                   && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);
        }

        /// <summary>Whether a member returns <c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c> exactly.</summary>
        private static bool ReturnsAList(Type returnType) =>
            returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            && IsReadOnlyList(returnType.GetGenericArguments()[0]);
    }
}
