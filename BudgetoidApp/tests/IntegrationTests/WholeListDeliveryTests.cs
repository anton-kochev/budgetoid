using System.Reflection;
using Application.Abstractions;
using Application.AccountKeys;
using Application.Accounts;
using Application.Accounts.GetAccounts;
using Application.Categories;
using Application.Categories.GetCategories;
using Application.CategoryGroups;
using Application.CategoryGroups.GetCategoryGroups;
using Application.Currencies;
using Application.Currencies.GetCurrencies;
using Application.Passkeys.BeginRegistration;
using Application.Payees;
using Application.Payees.GetPayees;
using Application.Transactions;
using Application.Users;
using Application.Users.ExportData;
using Application.Users.ListCredentials;
using Domain.Users;
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
/// No member is zero, so the CLR's <c>default</c> is not a disposition. Paired with
/// <see cref="ListReadRow.Delivery" /> having no default value, that is what stops a row nobody decided
/// about from compiling into existence.
/// </remarks>
public enum ListDelivery
{
    /// <summary>
    /// Held by this gate. The read offers a caller no way to ask for less than everything, and the
    /// place its list lands — a route's response, or the member carrying it — is one list and nothing
    /// else.
    /// </summary>
    DeliveredWhole = 1,

    /// <summary>
    /// Whole today, and held that way somewhere stronger than this file could hold it. Restating such
    /// a rule here would put a weaker sentence on top of a stronger one, and the weaker one is what a
    /// later reader quotes.
    /// </summary>
    HeldElsewhere = 2,

    /// <summary>Whole today, and deliberately not held. Paging it is a change the product intends.</summary>
    PageableLater = 3,
}

/// <summary>
/// The kind of argument a row makes, as a closed tag rather than as prose.
/// </summary>
/// <remarks>
/// <para>
/// <b>This member exists because the guard it replaces was worse than nothing.</b> What stood here was
/// a rule that every row's <see cref="ListReadRow.Reason" /> be distinct, which gave two rows that
/// genuinely share one argument no legal way to say so — and on its first outing it made an author
/// manufacture a difference that was not there. The prose is still per row and still has to be
/// non-trivial; what it no longer has to be is <em>unique</em>.
/// </para>
/// <para>
/// <b>What holds the paste instead is coverage.</b> The set is closed and every member of it must be
/// claimed by at least one row, so tagging the whole table with one family leaves the rest unclaimed
/// and goes red. The escape is to delete a family from this enum — a visible edit a reviewer weighs.
/// The limit: nothing machine-checkable holds a row's prose against the family it claims, because a
/// keyword rule over free prose is satisfied by pasting the keyword.
/// </para>
/// </remarks>
public enum ReasonFamily
{
    /// <summary>No server-side name order exists, because the name column is a sealed envelope.</summary>
    SealedNarrativeName = 1,

    /// <summary>A page would be stable; what it breaks is a tree relative to the whole set.</summary>
    WholeTree = 2,

    /// <summary>A bounded reference set belonging to no tenant, which nobody's data grows.</summary>
    BoundedReferenceSet = 3,

    /// <summary>A window of it silently removes a person's control of their own account.</summary>
    AccountControl = 4,

    /// <summary>Stably ordered, with nothing rendered relative to the rest of the set.</summary>
    StablyOrderable = 5,

    /// <summary>The wholeness is held by a rule outside this file, and a stronger one.</summary>
    HeldByAStrongerRule = 6,
}

/// <summary>
/// What the member takes besides its cancellation token, declared rather than derived.
/// </summary>
/// <remarks>
/// <b>Declared, because deriving it opens the hole this gate is about.</b> Were the rule merely "every
/// parameter is a <see cref="Guid" /> or a cancellation token", then <c>GetAllAsync(Guid afterId)</c> —
/// a keyset cursor, which is pagination — would satisfy it on the ambient reads that take nothing
/// today. Declaring <see cref="TakesNothing" /> on those rows is what makes that parameter red. The
/// limit, and it is real: a <see cref="Guid" /> cursor is indistinguishable by type from an owner key,
/// so a row declaring <see cref="KeyedOnAnAccount" /> accepts either, and
/// <c>ContractClassifiers_NameAPageAndCannotTellACursorFromAnOwner</c> demonstrates exactly that. What
/// the declaration buys is that the escape costs an edit to this column, beside the parameter.
/// </remarks>
public enum ListKeying
{
    /// <summary>Nothing at all, so no caller names anything to read the list.</summary>
    TakesNothing = 1,

    /// <summary>
    /// One <see cref="Guid" /> naming the account whose rows these are, put there by the server from
    /// an established identity rather than by the caller.
    /// </summary>
    KeyedOnAnAccount = 2,
}

/// <summary>
/// Where a delivered-whole read's list actually lands, as a closed pair of cases.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two cases rather than a nullable route, because a route-less gated read is real.</b>
/// <c>IPasskeyRepository.ListWebAuthnCredentialIdsForUserAsync</c> is reached over no route of its
/// own — it feeds <c>excludeCredentials</c> inside the registration ceremony — and tying "delivered
/// whole" to "has a route" would have forced whoever added it to invent a route or drop the row.
/// </para>
/// <para>
/// <b>Neither case can be satisfied by a value that is merely typed in.</b> A
/// <see cref="ServedOverARoute" /> pattern nothing answers to is reported rather than skipped, and a
/// <see cref="ReachedFromInsideTheServer" /> carrier must name a property that exists and is a list.
/// That is the difference from the reason guard this file used to carry: there the manufactured value
/// was unverifiable prose, and here there is nothing to manufacture that passes.
/// </para>
/// </remarks>
public abstract record ListSurface
{
    private ListSurface()
    {
    }

    /// <summary>Served over one GET route.</summary>
    /// <param name="Pattern">
    /// The endpoint's <c>RoutePattern.RawText</c> exactly. A group prefix plus <c>MapGet("/")</c>
    /// produces a <b>trailing slash</b> — <c>/api/accounts/</c>, not <c>/api/accounts</c> — which is
    /// measured rather than assumed.
    /// </param>
    /// <param name="Handler">
    /// The one type the route's delegate may bind besides a cancellation token, and the type the query
    /// and the response are <b>read from</b> rather than typed beside.
    /// </param>
    public sealed record ServedOverARoute(string Pattern, Type Handler) : ListSurface;

    /// <summary>Reached over no route; the list is consumed inside the server.</summary>
    /// <param name="Carrier">The type the list lands on.</param>
    /// <param name="Member">
    /// The property of <paramref name="Carrier" /> holding it, which must exist and must be an
    /// <see cref="IReadOnlyList{T}" /> — the same thing a routed row's response is held to.
    /// </param>
    public sealed record ReachedFromInsideTheServer(Type Carrier, string Member) : ListSurface;
}

/// <summary>
/// One list read, its disposition, the kind of argument that disposition rests on, what it takes, the
/// sentence a person wrote for <b>that</b> read, and where its list lands.
/// </summary>
/// <param name="ReadService">The interface exactly as its assembly declares it.</param>
/// <param name="Member">The member name; a spelling nothing answers to is reported by the census.</param>
/// <param name="Delivery">
/// What this list is. <b>No default value</b>, so the disposition cannot be inherited by silence.
/// </param>
/// <param name="Family">The kind of argument, closed and checked for coverage.</param>
/// <param name="Keying">What the member takes besides a cancellation token.</param>
/// <param name="Reason">
/// Why <em>this</em> list has <em>that</em> disposition, in this row's own words. Required to be
/// non-trivial and <b>not</b> required to be unique — see <see cref="ReasonFamily" /> for why the
/// uniqueness rule was removed.
/// </param>
/// <param name="Surface">
/// Present exactly when <paramref name="Delivery" /> is <see cref="ListDelivery.DeliveredWhole" />, in
/// both directions. That pairing compares the two halves to each other, so it cannot see a row
/// <b>demoted</b> out of the gate with its surface dropped in the same edit;
/// <c>DeliveredWholeReads_AreExactlyTheSetThisFileNames</c> is what catches that, and it pins the
/// surface beside the key so a routed row quietly becoming a route-less one moves it too.
/// </param>
public sealed record ListReadRow(
    Type ReadService,
    string Member,
    ListDelivery Delivery,
    ReasonFamily Family,
    ListKeying Keying,
    string Reason,
    ListSurface? Surface = null);

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
/// filter, no search term — at the contract that declares them and again where their list lands.
/// </summary>
/// <remarks>
/// <para>
/// <b>The argument is in <c>docs/engineering/whole-list-reads.md</c></b> — the failure this exists for
/// and why it has no symptom a person would recognise as pagination, what neither layer reaches, and
/// why the table below is deliberately editable. Read that chapter before promoting a row into the
/// gate or demoting one out of it. What is kept here is what a reader of <em>this file</em> needs and
/// the chapter does not carry.
/// </para>
/// <para>
/// <b>Which interfaces are swept, and why it is not just the <c>ReadService</c> suffix</b>, is argued
/// on <c>WholeListDelivery.ReadSurface</c>.
/// </para>
/// <para>
/// <b>Reading a red run here.</b> Nothing in this file reaches a database: the contract layer is pure
/// reflection, and the route layer boots <see cref="ApiFactory" /> in <c>Production</c> over a bogus
/// connection string and reads the route table without making a request, the way
/// <see cref="CompositionBoundaryTests" /> does. A connect timeout is therefore a finding about this
/// file rather than the suite's usual load noise. That layer is also why both live in the integration
/// project — <c>tests/UnitTests</c> holds no reference to Api, and the absence is a pinned row in
/// <c>ProjectReferenceGraphTests</c>.
/// </para>
/// <para>
/// <b>The synthetic controls, which ship permanently rather than being run once and reverted.</b> A
/// reflection query that silently returned nothing satisfies every emptiness assertion while inspecting
/// nothing at all, so the classifiers are demonstrated against input built here:
/// <c>ContractClassifiers_NameAPageAndCannotTellACursorFromAnOwner</c>,
/// <c>Census_ReportsADiscoveredReadInNoRow</c>, <c>Census_ReportsARowNoMemberAnswersTo</c>,
/// <c>UnexpectedBindings_NamesBothMembersOfAnAsParametersStruct</c> and
/// <c>UnexpectedBindings_NamesAQueryAHeaderAndANullableOptionalParameter</c>.
/// </para>
/// </remarks>
public sealed class WholeListDeliveryTests
{
    /// <summary>
    /// Every list read the swept ports declare, with the disposition a person gave it.
    /// </summary>
    /// <remarks>
    /// Ten rows, matching the ten members discovery finds. Seven are gated; one is whole and
    /// deliberately not gated; two are held by a stronger rule elsewhere. Read each
    /// <see cref="ListReadRow.Reason" /> rather than the bucket name — that is the member that stops
    /// this table from meaning less than it says.
    /// </remarks>
    private static readonly ListReadRow[] Table =
    [
        new(
            typeof(IPayeeReadService),
            nameof(IPayeeReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            ReasonFamily.SealedNarrativeName,
            ListKeying.TakesNothing,
            "Only the client can order this list, because no server-side name order exists at all: "
            + "payees.name is an AEAD envelope drawn under a fresh nonce, so the first differing byte "
            + "of two seals is nonce rather than text and an ordering by it reshuffles on every "
            + "unrelated save. A page therefore has no stable meaning to cut at. It is also the list a "
            + "client resolves a counterparty against before it posts a transaction, so a name missing "
            + "from the page it was handed reads as a payee that must be created, and the create "
            + "collides on IX_payees_budget_id_name_key.",
            new ListSurface.ServedOverARoute("/api/payees/", typeof(GetPayeesHandler))),
        new(
            typeof(IAccountReadService),
            nameof(IAccountReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            ReasonFamily.SealedNarrativeName,
            ListKeying.TakesNothing,
            "The payees argument, sharing its family because it is the same argument and not a second "
            + "one: accounts.name is an envelope under a fresh nonce, so AccountReadService deleted "
            + "the orderby account.Name it used to carry and orders by CreatedAtUtc then Id instead. "
            + "The client sorts the opened names itself, and accounts and payees sort through the "
            + "SAME CODE — accounts.service.ts and transactions.service.ts each hand their rows to "
            + "sortByNarrativeName in +shared/, one function rather than a copy apiece, and that "
            + "function is the one place the product calls compareNarrative. Two lists ordered by "
            + "one body is why this row shares a family instead of arguing the case twice. So a page "
            + "boundary hands each page to a sort that cannot see the others, and the merged list "
            + "comes out in no order a person recognises, with nothing thrown and nothing logged.",
            new ListSurface.ServedOverARoute("/api/accounts/", typeof(GetAccountsHandler))),
        new(
            typeof(ICategoryGroupReadService),
            nameof(ICategoryGroupReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            ReasonFamily.WholeTree,
            ListKeying.TakesNothing,
            "NOT the ordering argument, and a reader who assumes it is will widen this row wrongly. "
            + "category_groups.position is a readable integer and CategoryGroupReadService does order "
            + "by it, so a page of this list would be perfectly stable. The reason is what is rendered: "
            + "the categories screen draws one tree whose group positions are relative to the ENTIRE "
            + "set, so a page boundary splits the tree and leaves the client arranging branches it "
            + "cannot see the rest of.",
            new ListSurface.ServedOverARoute(
                "/api/category-groups/",
                typeof(GetCategoryGroupsHandler))),
        new(
            typeof(ICategoryReadService),
            nameof(ICategoryReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            ReasonFamily.WholeTree,
            ListKeying.TakesNothing,
            "The other half of the same tree, and stated in its own words rather than deferred to the "
            + "group row, because the two are read together. Category positions are readable integers "
            + "ordered within their group, and categories.service.ts sorts by group position then "
            + "category position then id — an ordering computed across the whole set. A page of "
            + "categories arriving without the groups they hang from, or without their siblings, is a "
            + "tree the client cannot assemble.",
            new ListSurface.ServedOverARoute("/api/categories/", typeof(GetCategoriesHandler))),
        new(
            typeof(ICurrencyReadService),
            nameof(ICurrencyReadService.GetAllAsync),
            ListDelivery.DeliveredWhole,
            ReasonFamily.BoundedReferenceSet,
            ListKeying.TakesNothing,
            "A bounded reference set the client picks from in full — shared data belonging to no "
            + "tenant, SELECT only, with no row-level-security policy, ordered by the ISO 4217 code the "
            + "server can read. Nothing about a nonce or a tree applies here. It is whole because a "
            + "picker offering a window of the currencies that exist is a picker that cannot be used to "
            + "choose the one the person wants, and because the set does not grow with anybody's data.",
            new ListSurface.ServedOverARoute("/api/currencies/", typeof(GetCurrenciesHandler))),
        new(
            typeof(ICredentialReadService),
            nameof(ICredentialReadService.ListForUserAsync),
            ListDelivery.DeliveredWhole,
            ReasonFamily.AccountControl,
            ListKeying.KeyedOnAnAccount,
            "GET /api/me/credentials backs the Ways to sign in list on /app/settings, which draws one "
            + "row per credential and hangs a Revoke control on the rows that carry one. A window of "
            + "this list is therefore a credential a person can neither see nor act on, and an "
            + "account's own inventory of authenticators coming back short is read as 'that one is "
            + "not registered here'. It is bounded by how many authenticators a person enrols, so "
            + "there is nothing for a page to relieve. Its owner argument is load-bearing — "
            + "credentials is exempt from row-level security, so nothing beneath the application "
            + "narrows the read — but scoping and pageability are different questions, and the owner "
            + "argument answers only the first.",
            new ListSurface.ServedOverARoute("/api/me/credentials", typeof(ListCredentialsHandler))),
        new(
            typeof(IPasskeyRepository),
            nameof(IPasskeyRepository.ListWebAuthnCredentialIdsForUserAsync),
            ListDelivery.DeliveredWhole,
            ReasonFamily.AccountControl,
            ListKeying.KeyedOnAnAccount,
            "These handles feed excludeCredentials in the registration ceremony, so a window of this "
            + "list stops excluding: an authenticator already enrolled and left off the page it was "
            + "handed is offered the ceremony again and registers a second credential for the same "
            + "key, with nothing thrown and nothing logged. Nobody sees that happen: it is a fact "
            + "about what an authenticator did, not about a screen, which is what makes it easy to "
            + "miss afterwards. It is reached over no route of its own, so its surface names the "
            + "member the list lands on instead.",
            new ListSurface.ReachedFromInsideTheServer(
                typeof(PasskeyCreationOptions),
                nameof(PasskeyCreationOptions.ExcludeCredentials))),
        new(
            typeof(ITransactionReadService),
            nameof(ITransactionReadService.GetAllWithPayeeAsync),
            ListDelivery.PageableLater,
            ReasonFamily.StablyOrderable,
            ListKeying.TakesNothing,
            "WHOLE TODAY AND DELIBERATELY OUTSIDE THE GATE, which is why it carries no surface. "
            + "TransactionReadService orders by transaction.Date descending then CreatedAtUtc "
            + "descending, both columns this server reads in the clear, so a page of this list is "
            + "stable and a client does not have to reorder it. Stability alone is not what separates "
            + "it from the gated rows — the category-group row says plainly that a page of its list "
            + "would hold still too — it is that nothing renders a transaction relative to the set. "
            + "Pagination for it is planned. Adding it to the gate would cost nothing today and would "
            + "forbid a change the product intends to make, which is the failure mode a table like "
            + "this one is most likely to produce: a rule that reads as caution and is really a veto "
            + "nobody argued for.",
            Surface: null),
        new(
            typeof(IAccountKeyReadService),
            nameof(IAccountKeyReadService.ListForAccountAsync),
            ListDelivery.HeldElsewhere,
            ReasonFamily.HeldByAStrongerRule,
            ListKeying.KeyedOnAnAccount,
            "Held far more strongly than a route assertion could hold it: IAccountKeyReadService says "
            + "in its own remarks that paging it would hand somebody nine of their ten ways back into "
            + "their account, and the set is bounded by how many factors one account can hold rather "
            + "than by anybody's data volume. Repeating that here as a shape assertion would put a "
            + "weaker sentence on top of the stronger one, and the weaker one is what a later reader "
            + "quotes.",
            Surface: null),
        new(
            typeof(IExportReadService),
            nameof(IExportReadService.ListOwnedBudgetsAsync),
            ListDelivery.HeldElsewhere,
            ReasonFamily.HeldByAStrongerRule,
            ListKeying.KeyedOnAnAccount,
            "Held by a stronger rule running in the OPPOSITE direction: the export refuses rather than "
            + "truncates, throwing unless the owned set is exactly the ambient budget, by set equality "
            + "in both directions. A gate saying merely 'this comes back whole' would be a weaker "
            + "claim sitting on top of a refusal, and the weaker one is the sentence a later reader "
            + "would quote.",
            Surface: null),
    ];

    [Test]
    public async Task Discovery_FindsExactlyTheListReadsTheSweptPortsDeclare()
    {
        // Act
        IReadOnlyList<string> discovered = WholeListDelivery.Discovered();

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
            "IPasskeyRepository.ListWebAuthnCredentialIdsForUserAsync",
            "IPayeeReadService.GetAllAsync",
            "ITransactionReadService.GetAllWithPayeeAsync",
        ];

        // IsEmpty on the difference rather than the string.Join form two files nearby use: a census
        // reporting through string.Join truncates its message and names only the first offender, so a
        // set that drifted by three reads as a set that drifted by one.
        await Assert.That(discovered.Except(expected, StringComparer.Ordinal)).IsEmpty();
        await Assert.That(expected.Except(discovered, StringComparer.Ordinal)).IsEmpty();

        // Not a restatement of the two lines above, and the reason it is the last size assertion
        // standing after the vacuity decorations came out: it pins the COUNT, so a sweep returning
        // the same ten keys twice moves it while both differences stay empty.
        await Assert.That(discovered.Count).IsEqualTo(expected.Length);
    }

    [Test]
    public async Task EveryListRead_IsClaimedByExactlyOneRow()
    {
        // Arrange
        IReadOnlyList<string> discovered = WholeListDelivery.Discovered();

        // Act
        ListReadCensus census = WholeListDelivery.Take(discovered, Table.Select(WholeListDelivery.KeyOf));

        // Assert — IsEmpty on the offender collections, so a failure names every one of them.
        await Assert.That(census.Unclassified).IsEmpty();
        await Assert.That(census.ClaimedTwice).IsEmpty();
        await Assert.That(census.NamingNoMember).IsEmpty();

        // The classifier saw every discovered read. That the set is not empty is pinned by the
        // discovery test, which is why no size floor is repeated here.
        await Assert.That(census.Classified.Count).IsEqualTo(discovered.Count);
    }

    [Test]
    public async Task EveryRow_CarriesADecidedDispositionFamilyKeyingReasonAndSurface()
    {
        // Act — the ways a row can be present and mean nothing.
        IReadOnlyList<string> undecided =
        [
            .. Table
                .Where(row => !Enum.IsDefined(row.Delivery)
                              || !Enum.IsDefined(row.Family)
                              || !Enum.IsDefined(row.Keying))
                .Select(WholeListDelivery.KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        // A reason that is blank, or a word, satisfies a census perfectly and tells the next reader
        // nothing — the prose failure one layer in. Sixty characters is not a quality bar; it is the
        // floor beneath which "n/a" and "see above" live. There is deliberately no uniqueness rule
        // beside it: see ReasonFamily for the guard that replaced one.
        IReadOnlyList<string> silent =
        [
            .. Table
                .Where(row => string.IsNullOrWhiteSpace(row.Reason) || row.Reason.Trim().Length < 60)
                .Select(WholeListDelivery.KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        // The paste this table exists to make expensive, caught by coverage rather than by
        // uniqueness: one family pasted across every row leaves the rest of the set unclaimed.
        IReadOnlyList<string> unclaimedFamilies =
        [
            .. Enum.GetValues<ReasonFamily>()
                .Where(family => Array.TrueForAll(Table, row => row.Family != family))
                .Select(family => family.ToString())
                .Order(StringComparer.Ordinal),
        ];

        // A gated row with no surface would skip every shape assertion in silence, and a surface
        // hung on an ungated row is a claim nothing checks. This compares the two halves TO EACH
        // OTHER, so it says nothing about a row demoted out of DeliveredWhole with its surface
        // dropped in the same edit — measured, that left this test green — and
        // DeliveredWholeReads_AreExactlyTheSetThisFileNames is what catches it.
        IReadOnlyList<string> mismatchedSurfaces =
        [
            .. Table
                .Where(row => (row.Delivery is ListDelivery.DeliveredWhole) != (row.Surface is not null))
                .Select(WholeListDelivery.KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(undecided).IsEmpty();
        await Assert.That(silent).IsEmpty();
        await Assert.That(unclaimedFamilies).IsEmpty();
        await Assert.That(mismatchedSurfaces).IsEmpty();
    }

    [Test]
    public async Task DeliveredWholeReads_AreExactlyTheSetThisFileNames()
    {
        // Arrange — the second half of the pin the discovery test starts. That one holds which reads
        // EXIST; this one holds which of them this gate is about, and the two are different facts.
        //
        // MEASURED, which is why it is here. Before this test existed, flipping the payee row to
        // PageableLater and dropping its route in the same edit left the suite green while real
        // pagination shipped on the query and on the live route: a demotion takes a row out of both
        // halves at once, and nothing else notices. Measured again with this test in place: that
        // mutation reddens exactly one test in the whole suite — this one — and the failure names
        // the row. Measured a third time: swapping a row's surface between the two cases keeps its
        // key and moves this line, which is what catches a routed row rewritten as a route-less
        // one — an edit that would otherwise take it out of the route layer in silence.
        //
        // A PIN AND NOT A PROHIBITION. Demoting a row is a change this product intends to make — the
        // transaction list is already outside the gate for exactly that reason. What it costs is one
        // line here and a rewritten Reason on the row, in a diff a reviewer can weigh.
        //
        // THE LIMIT, stated rather than papered over: this holds the SET, and nothing machine-checkable
        // holds a row's prose against its disposition. A demoted row can keep a Reason still arguing
        // the gated case — measured, one did. What stands in for it is the red above: the person
        // clearing it edits this list and that row in one diff, with the stale sentence in it.
        string[] expected =
        [
            "IAccountReadService.GetAllAsync at /api/accounts/",
            "ICategoryGroupReadService.GetAllAsync at /api/category-groups/",
            "ICategoryReadService.GetAllAsync at /api/categories/",
            "ICredentialReadService.ListForUserAsync at /api/me/credentials",
            "ICurrencyReadService.GetAllAsync at /api/currencies/",
            "IPasskeyRepository.ListWebAuthnCredentialIdsForUserAsync into "
            + "PasskeyCreationOptions.ExcludeCredentials",
            "IPayeeReadService.GetAllAsync at /api/payees/",
        ];

        // Act
        IReadOnlyList<string> gated =
        [
            .. WholeListDelivery.Gated(Table)
                .Select(WholeListDelivery.DescribeGated)
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
        // both differences empty and moves this one.
        await Assert.That(gated.Count).IsEqualTo(expected.Length);
    }

    [Test]
    public async Task EveryListRead_TakesOnlyWhatItsKeyingDeclares()
    {
        // Act — the allow-list at the contract layer, and it runs over the WHOLE table rather than
        // over the gated rows. Every row today declares one of two shapes, so a parameter added to
        // any of them reddens here, including the transaction read this file has argued is pageable
        // later: paging it means editing its keying column, which is the visible cost, not a
        // silently accepted parameter. Every owner-keyed read takes its Guid from
        // IUserContext.UserId, never from the caller — measured on all four.
        IReadOnlyList<string> misKeyed =
        [
            .. Table
                .Select(WholeListDelivery.MisKeyingOf)
                .OfType<string>()
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(misKeyed).IsEmpty();
    }

    [Test]
    public async Task EveryDeliveredWholeRead_AsksForNothingAndAnswersWithOneList()
    {
        // Arrange
        ListReadRow[] gated = [.. WholeListDelivery.Gated(Table)];

        // Act — a query record with a property is a query with a knob on it, whether the knob is
        // called Page, Skip or SearchTerm. The query and the response are read from the handler's own
        // IQueryHandler<,> arguments, never typed beside it: two columns naming types nothing uses
        // would inspect nothing while passing.
        IReadOnlyList<string> askingQueries =
        [
            .. gated
                .SelectMany(WholeListDelivery.AskedForBy)
                .Order(StringComparer.Ordinal),
        ];

        // Exactly one list and nothing beside it, deliberately: a response carrying Items AND
        // NextCursor, or Items AND TotalCount, satisfies "carries a list" while being precisely the
        // shape this file exists to refuse. A route-less row is held to the same thing on the member
        // its list lands on.
        IReadOnlyList<string> answeringMoreThanAList =
        [
            .. gated
                .SelectMany(WholeListDelivery.AnsweredNotAsOneListBy)
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(askingQueries).IsEmpty();
        await Assert.That(answeringMoreThanAList).IsEmpty();

        // Non-vacuity: PublicPropertiesOf returning nothing for everything would satisfy the first
        // line above, so one known list property is asserted to be found.
        await Assert.That(WholeListDelivery.PublicPropertiesOf(typeof(PayeeListResponse)).Count)
            .IsEqualTo(1);
    }

    [Test]
    public async Task EveryRoutedWholeRead_BindsNothingButItsHandlerAndACancellationToken()
    {
        // Arrange — Production over a connection string nothing answers on, exactly as
        // CompositionBoundaryTests does: the route table is built at startup and no request is made,
        // so no database is reached and no container is needed. A connect timeout here would be a
        // finding about this test rather than the suite's usual load noise.
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // The route-less row has nothing here to inspect, stated rather than hidden: what stands in
        // for this layer on it is the carrier assertion above and the pinned surface.
        (ListReadRow Row, ListSurface.ServedOverARoute Route)[] routed =
        [
            .. WholeListDelivery.Gated(Table)
                .Select(row => (Row: row, Route: row.Surface as ListSurface.ServedOverARoute))
                .Where(pair => pair.Route is not null)
                .Select(pair => (pair.Row, Route: pair.Route!)),
        ];

        // Act
        List<string> unroutable = [];
        List<string> unexpected = [];
        int inspectedBindings = 0;

        foreach ((ListReadRow row, ListSurface.ServedOverARoute route) in routed)
        {
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
                unroutable.Add(
                    $"{WholeListDelivery.KeyOf(row)} at {route.Pattern} matched {matches.Length} GET "
                    + "endpoints");
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

        // Non-vacuity, and these two are kept where the rest came out because neither size is pinned
        // anywhere. The route table is EMPTY before the host starts — measured, 0 endpoints before
        // StartAsync and the full set after — so a factory that failed to start would leave every
        // list above empty and this test would pass having inspected nothing. WebApplicationFactory
        // starts the host when Services is first read; these two lines are what prove it did.
        await Assert.That(dataSource.Endpoints.Count).IsGreaterThan(0);
        await Assert.That(inspectedBindings).IsGreaterThan(0);
    }

    [Test]
    public async Task ContractClassifiers_NameAPageAndCannotTellACursorFromAnOwner()
    {
        // Arrange — the defects, built here rather than staged in a real assembly and reverted, so
        // the proof is permanent. One read service with a page on one member and a Guid cursor on
        // another.
        Type[] types = [typeof(IProbeReadService)];
        ListReadRow paged = ProbeRow(nameof(IProbeReadService.PagedAsync), ListKeying.TakesNothing);
        ListReadRow cursored = ProbeRow(nameof(IProbeReadService.CursorAsync), ListKeying.TakesNothing);

        // Act
        IReadOnlyList<MethodInfo> members = WholeListDelivery.MembersIn(types);

        // Assert — the shape filter finds both (each returns Task<IReadOnlyList<T>>), and the
        // classifier names the parameter rather than merely counting it.
        await Assert.That(members.Select(WholeListDelivery.KeyOf))
            .IsEquivalentTo(
                new[] { "IProbeReadService.CursorAsync", "IProbeReadService.PagedAsync" },
                CollectionOrdering.Matching);
        await Assert.That(WholeListDelivery.NarrowingParametersOf(members[1]))
            .IsEquivalentTo(new[] { "page" }, CollectionOrdering.Matching);

        // A page and a cursor are both refused on a row declaring TakesNothing, which is the whole
        // reason that column is declared rather than derived: a Guid cursor is a parameter no
        // type-shaped rule would object to.
        await Assert.That(WholeListDelivery.MisKeyingOf(paged)).IsNotNull();
        await Assert.That(WholeListDelivery.MisKeyingOf(cursored)).IsNotNull();

        // THE LIMIT, demonstrated rather than admitted in prose: the same Guid cursor passes on a row
        // that declares KeyedOnAnAccount, because an owner key and a cursor are the same type. The
        // declaration does not make that impossible; it makes it an edit to the row, next to the
        // parameter, in one diff.
        await Assert
            .That(WholeListDelivery.MisKeyingOf(cursored with { Keying = ListKeying.KeyedOnAnAccount }))
            .IsNull();
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
    public async Task UnexpectedBindings_NamesBothMembersOfAnAsParametersStruct()
    {
        // Arrange — the measurement that makes the route layer worth building rather than folding
        // into a signature read. An [AsParameters] wrapper flattens into IParameterBindingMetadata as
        // one entry PER MEMBER and the wrapper itself does not appear, while MethodInfo.GetParameters()
        // shows the wrapper and none of its members. A signature read sees one harmless struct here;
        // binding metadata sees two paging knobs.
        await using WebApplication app = ThrowawayApp();
        app.MapGet(
            "/probe/wrapped",
            (ThrowawayHandler handler, [AsParameters] PagingWindow window, CancellationToken cancellationToken) =>
                handler.Answer());

        // The route table is EMPTY before StartAsync — measured on a host of this shape, 0 endpoints
        // before and the full set after. Reading the data source without starting is the silent-pass
        // trap this control would otherwise fall into.
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
        // a route layer built on GetParameters would have found nothing to report. Pattern-matched
        // rather than asserted and then dereferenced — IsNotNull does not narrow for the compiler, so
        // the line beneath it would carry a null-forgiving operator and would die unnamed if a route
        // delegate ever stopped recording its MethodInfo.
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
        // Arrange — three binding shapes no other control here reaches. The one beside this binds an
        // [AsParameters] struct, so on its evidence alone the classifier is only known to see
        // required, unadorned parameters. Each of these is a narrowing knob that LOOKS unlike a page:
        // a nullable and therefore optional parameter; one bound from an explicitly named query
        // source and called 'window' rather than 'page', because the classifier is an allow-list and
        // must not care; and a header bind, which is not in the URL at all and would go quiet on its
        // own if a later .NET stopped recording header binds in IParameterBindingMetadata.
        //
        // It also carries the in/out filter: five parameters bind and three come back.
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

    /// <summary>A table row over the probe interface, for the contract controls.</summary>
    private static ListReadRow ProbeRow(string member, ListKeying keying) => new(
        typeof(IProbeReadService),
        member,
        ListDelivery.DeliveredWhole,
        ReasonFamily.SealedNarrativeName,
        keying,
        "Synthetic, and never in the table: the census reaches real assemblies and this interface is "
        + "private and nested, so nothing discovers it.",
        new ListSurface.ReachedFromInsideTheServer(typeof(PayeeListResponse), nameof(PayeeListResponse.Items)));

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
    /// A read service whose list members have grown a page and a cursor, for the controls that prove
    /// the contract classifiers name them.
    /// </summary>
    /// <remarks>
    /// Nested and private, so it can never be mistaken for a port. Discovery over the real assemblies
    /// cannot see it; the controls hand it to <see cref="WholeListDelivery.MembersIn" /> directly,
    /// which is the same seam <c>RepositoryAttributionCensusTests</c> opens for its synthetic input.
    /// </remarks>
    private interface IProbeReadService
    {
        Task<IReadOnlyList<string>> CursorAsync(Guid afterId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> PagedAsync(int page, CancellationToken cancellationToken = default);
    }

    /// <summary>Two paging knobs inside a struct, for the <c>[AsParameters]</c> control.</summary>
    public readonly record struct PagingWindow(int Page, int PageSize);

    /// <summary>Stands in for a query handler on a throwaway route.</summary>
    public sealed class ThrowawayHandler
    {
        public string Answer() => "unread";
    }

    /// <summary>
    /// Finds the list reads the swept ports declare, sorts them against the written table, and names
    /// what a member, a query, a response or a route offers a caller besides everything.
    /// </summary>
    /// <remarks>
    /// <see cref="Take" /> takes the written keys as a parameter rather than reading
    /// <see cref="Table" />, and <see cref="MembersIn" /> takes types rather than reaching for an
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

        /// <summary>The gated rows, which the query, response and route assertions filter to first.</summary>
        internal static IEnumerable<ListReadRow> Gated(IEnumerable<ListReadRow> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);

            return rows.Where(row => row.Delivery is ListDelivery.DeliveredWhole);
        }

        /// <summary>
        /// The surface half of a gated row, refusing a gated row that has none. Measured: without
        /// this, a row gated with no route made the assertion loops die on a bare
        /// <c>NullReferenceException</c> naming nothing, beside the one test that named the row.
        /// </summary>
        internal static ListSurface SurfaceOf(ListReadRow row)
        {
            ArgumentNullException.ThrowIfNull(row);

            return row.Surface
                   ?? throw new InvalidOperationException(
                       $"{KeyOf(row)} is marked {row.Delivery} and carries no surface, so this "
                       + "assertion has nothing to read. "
                       + "EveryRow_CarriesADecidedDispositionFamilyKeyingReasonAndSurface is the test "
                       + "that says so in full.");
        }

        /// <summary>One line naming a gated row and where its list lands, for the set pin.</summary>
        internal static string DescribeGated(ListReadRow row) => SurfaceOf(row) switch
        {
            ListSurface.ServedOverARoute route => $"{KeyOf(row)} at {route.Pattern}",
            ListSurface.ReachedFromInsideTheServer inside =>
                $"{KeyOf(row)} into {inside.Carrier.Name}.{inside.Member}",
            _ => throw new InvalidOperationException(
                $"{KeyOf(row)} carries a surface this file has no description for. ListSurface is a "
                + "closed pair of cases, so a third one was added without this switch."),
        };

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

        /// <summary>The interfaces this file sweeps for list reads.</summary>
        /// <remarks>
        /// <para>
        /// <b>Two naming sweeps and one named port, the shape <see cref="CompositionBoundaryTests" />
        /// uses against the identical weakness.</b> <c>ReadService</c> is what this codebase calls a
        /// projection's port and <c>Repository</c> what it calls an aggregate's, and both halves hold
        /// list reads: <c>IPasskeyRepository.ListWebAuthnCredentialIdsForUserAsync</c> lives in
        /// <c>Domain</c>, would be invisible to a <c>ReadService</c> sweep alone, and is the read
        /// whose truncation costs the most — a duplicate credential enrolment, not an odd screen.
        /// </para>
        /// <para>
        /// <b>Both halves are still conventions, and the residual hole is a port conforming to
        /// neither.</b> There is no base type or attribute to key on, so what covers it is listing
        /// such a port by name below and pinning the discovered set. A second one needs a line here,
        /// and that is the intended cost.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<Type> ReadSurface() =>
        [
            .. typeof(IPayeeReadService).Assembly
                .GetTypes()
                .Where(type => type.IsInterface
                               && type.Name.EndsWith("ReadService", StringComparison.Ordinal)),

            .. typeof(IPasskeyRepository).Assembly
                .GetTypes()
                .Where(type => type.IsInterface
                               && type.Name.EndsWith("Repository", StringComparison.Ordinal)),

            // Named individually because it ends in neither suffix and is a persistence port that
            // reads — the same type CompositionBoundaryTests lists for the same reason. It holds no
            // list read today; this line is what makes the day it grows one audible.
            typeof(IWebAuthnChallengeStore),
        ];

        /// <summary>Every list read the swept interfaces declare, ordered by key.</summary>
        internal static IReadOnlyList<string> Discovered() =>
        [
            .. MembersIn(ReadSurface())
                .Select(KeyOf)
                .Order(StringComparer.Ordinal),
        ];

        /// <summary>
        /// The member filter, applied to types rather than to an assembly so it can be aimed at
        /// synthetic input. Which interfaces are swept is <see cref="ReadSurface" />'s question.
        /// </summary>
        /// <remarks>
        /// <b>The filter is the return shape</b> — <c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c> — and
        /// never the member name, because the transaction list is called <c>GetAllWithPayeeAsync</c>
        /// and a <c>GetAll</c> filter would already be missing it today. A list read called
        /// <c>FeedAsync</c> is found by this and by no name rule anybody would think to write. The
        /// honest limit, stated rather than fixed by widening: a read that stops returning that shape
        /// leaves discovery entirely and the census would go on passing with one fewer subject.
        /// Widening only moves the boundary; what covers it is the pinned key set, which reddens.
        /// </remarks>
        internal static IReadOnlyList<MethodInfo> MembersIn(IEnumerable<Type> types)
        {
            ArgumentNullException.ThrowIfNull(types);

            return
            [
                .. types
                    .Where(type => type.IsInterface)
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

        /// <summary>
        /// How this member fails the keying its row declares, or <see langword="null" /> when it holds.
        /// </summary>
        internal static string? MisKeyingOf(ListReadRow row)
        {
            ArgumentNullException.ThrowIfNull(row);

            ParameterInfo[] parameters = MemberOf(row).GetParameters();

            // The token is optional on every one of these, so no caller is ever forced to name
            // anything to read the list at all.
            bool holds = row.Keying switch
            {
                ListKeying.TakesNothing =>
                    parameters is [{ IsOptional: true } only]
                    && only.ParameterType == typeof(CancellationToken),
                ListKeying.KeyedOnAnAccount =>
                    parameters is [{ IsOptional: false } owner, { IsOptional: true } token]
                    && owner.ParameterType == typeof(Guid)
                    && token.ParameterType == typeof(CancellationToken),
                _ => false,
            };

            return holds
                ? null
                : $"{KeyOf(row)} declares {row.Keying} and takes ({SignatureOf(parameters)})";
        }

        /// <summary>
        /// The properties a gated row's query declares, each as one offender line. A route-less row
        /// has no query — nothing outside the server asks for its list — so it contributes nothing
        /// here, which is one of the two things the route layer's absence costs it.
        /// </summary>
        internal static IEnumerable<string> AskedForBy(ListReadRow row) =>
            SurfaceOf(row) is ListSurface.ServedOverARoute route
                ? PublicPropertiesOf(ContractOf(route.Handler).Query)
                    .Select(property => $"{ContractOf(route.Handler).Query.Name} declares {property.Name}")
                : [];

        /// <summary>
        /// The place a gated row's list lands, named when it is anything other than one list. A
        /// routed row is held on its handler's response — either the response IS the list, or it is a
        /// record whose one public property is. A route-less row is held on the member its list lands
        /// on, which must exist and must be a list: the same claim, where the list actually arrives.
        /// </summary>
        internal static IEnumerable<string> AnsweredNotAsOneListBy(ListReadRow row)
        {
            switch (SurfaceOf(row))
            {
                case ListSurface.ServedOverARoute route:
                    {
                        Type response = ContractOf(route.Handler).Response;
                        bool oneList = IsReadOnlyList(response)
                                       || (PublicPropertiesOf(response) is [{ } sole]
                                           && IsReadOnlyList(sole.PropertyType));

                        return oneList ? [] : [response.Name];
                    }

                case ListSurface.ReachedFromInsideTheServer inside:
                    {
                        PropertyInfo? carrier = inside.Carrier.GetProperty(
                            inside.Member,
                            BindingFlags.Public | BindingFlags.Instance);

                        return carrier is not null && IsReadOnlyList(carrier.PropertyType)
                            ? []
                            : [$"{inside.Carrier.Name}.{inside.Member}"];
                    }

                default:
                    throw new InvalidOperationException(
                        $"{KeyOf(row)} carries a surface this file cannot read a list out of. "
                        + "ListSurface is a closed pair of cases, so a third one was added without "
                        + "this switch.");
            }
        }

        /// <summary>
        /// The query and the response a handler declares, read from its own
        /// <see cref="IQueryHandler{TQuery,TResult}" /> arguments.
        /// </summary>
        /// <remarks>
        /// <b>Derived rather than typed beside the handler in the table, and that is the point.</b>
        /// While the query and the response were columns of their own, nothing tied them to the
        /// handler. Measured both ways: with the row's handler declaring a query that carries a
        /// property and a response that is not a list, reading the pair off a column leaves the
        /// shape assertions inspecting the old, unused types and green, and reading it off the
        /// handler reddens them naming the property. Reflection inspecting nothing is the exact
        /// failure this file ships controls against, and that pair had no control.
        /// </remarks>
        internal static (Type Query, Type Response) ContractOf(Type handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            Type[] implemented =
            [
                .. handler.GetInterfaces()
                    .Where(contract => contract.IsGenericType
                                       && contract.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)),
            ];

            return implemented is [{ } sole]
                ? (sole.GetGenericArguments()[0], sole.GetGenericArguments()[1])
                : throw new InvalidOperationException(
                    $"{handler.Name} implements {implemented.Length} IQueryHandler<,> interfaces, so "
                    + "the query and the response cannot be read off it. Every gated route is served "
                    + "by exactly one, and a handler answering two queries needs the table to say "
                    + "which of them this row is about.");
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

        /// <summary>A parameter list as a reader would write it, for an offender line.</summary>
        private static string SignatureOf(ParameterInfo[] parameters) => string.Join(
            ", ",
            parameters.Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));

        /// <summary>Whether a member returns <c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c> exactly.</summary>
        private static bool ReturnsAList(Type returnType) =>
            returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            && IsReadOnlyList(returnType.GetGenericArguments()[0]);
    }
}
