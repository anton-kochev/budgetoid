using Infrastructure.Persistence.Provisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// That an erasure which has taken effect has no way back, read off the route table itself.
/// </summary>
/// <remarks>
/// <para>
/// An erasure takes effect at commit, and after that there is no route, handler, role or grant that
/// brings the account back. <c>ErasureRemnantSchemaTests</c> already asks the schema whether anything
/// survived; this asks the API whether anything could be <i>asked</i> to bring it back. The two are
/// complementary and neither implies the other — a schema with nothing left cannot be reversed by a
/// route, but a route promising a reversal is a claim the product must not make even when it would
/// fail, and a route table is the only place that promise is visible whole.
/// </para>
/// <para>
/// Structural rather than behavioural, for the reason <c>UserProvisioningRouteTests</c> gives: a
/// behavioural sweep would need one authenticated request per endpoint and could still only say what
/// happened, not what the surface offers. The fixture is that class': the factory runs in
/// <c>Production</c> against a connection string nothing connects to, so reading the route table needs
/// no database and Production skips the Development startup block that would migrate one. Every
/// control opens no host at all and is a millisecond test.
/// </para>
/// </remarks>
public sealed class ErasureIrreversibilityTests
{
    /// <summary>
    /// The words that name bringing back something already gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>cancel</c> is deliberately absent, and its absence is the considered part of this list.</b>
    /// The requirement this pin serves refuses a cancellation, grace period or restore path for an
    /// erasure <i>that has taken effect</i>, and its own text carves the pre-effect case out: a
    /// scheduled erasure would be cancellable before it takes effect, and nothing cancels one after.
    /// A pin refusing the word <c>cancel</c> outright would therefore claim more than the requirement
    /// it enforces — it would forbid a capability the requirements themselves describe. Worse, it
    /// would be a pin somebody has to fight in order to build that capability, and a pin that stands
    /// between an author and work they were told to do is a pin they delete, which leaves nothing at
    /// all where there used to be a check. Every word that <i>is</i> on this list names retrieval:
    /// each of them presupposes something gone that comes back. Cancelling before effect brings
    /// nothing back, because nothing left. Whoever later builds a delayed, cancellable erasure should
    /// read this and understand that they are standing inside the carve-out rather than outside the
    /// rule — and that adding a cancellation route under the erasure resource would still have to
    /// answer to <see cref="ErasureResource_MapsExactlyTheOneDestructiveRoute" />, which is where that
    /// argument belongs.
    /// </para>
    /// <para>
    /// <b><c>recover</c> and <c>recovery</c> are absent for the same shape of reason, and the reason is
    /// this product in particular.</b> In a passkey product <i>account recovery</i> is regaining access
    /// to an account that is very much alive: this repository already writes the word that way — a
    /// registered passkey <i>is</i> a recovery factor, and a recovery-code hash is named as a column a
    /// future table carries (see
    /// <c>docs/decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md</c>
    /// and <c>docs/engineering/data-isolation.md</c>). A word that cannot separate regaining access to
    /// a live account from resurrecting an erased one refuses the first in order to catch the second,
    /// which is the carve-out above restated: the pin would stand in front of work somebody was told to
    /// do, and would be deleted rather than argued with. The reversal the word would otherwise catch —
    /// a route directly under the erasure resource — is caught exhaustively by
    /// <see cref="ErasureResource_MapsExactlyTheOneDestructiveRoute" /> instead, so the carve-out costs
    /// nothing where the promise is actually made.
    /// </para>
    /// <para>
    /// <b>Every word is listed in each form it could be routed under, because the matcher does not
    /// stem.</b> One form per word is not a list, it is a sample: <c>restoration</c>,
    /// <c>reinstatement</c>, <c>reactivation</c>, <c>reversal</c> and <c>undeleted</c> each walk
    /// straight past the base form they derive from, and the pair <c>restore</c> / <c>restored</c> is
    /// the only place that is visible without writing them out. Not stemming is a virtue in one
    /// direction only, and the two directions are worth keeping apart: <c>discovery</c> failing to
    /// match <c>recover</c> is a false positive correctly avoided, while <c>restoration</c> failing to
    /// match <c>restore</c> is a true positive missed, and only the first is an argument for the
    /// matcher. The second is an argument for writing the forms out, which is what this list does.
    /// Every spelling below is <i>proved</i> to match by an <c>[Arguments]</c> case on
    /// <see cref="ReversalVocabulary_MatchesEverySpellingItLists" />, and
    /// <see cref="ReversalVocabulary_HoldsExactlyTheSpellingsTheCoverageControlProves" /> is what stops
    /// a spelling being added here without one.
    /// </para>
    /// <para>
    /// <c>reverse</c> is listed beside <c>revert</c> despite being the word an accounting product would
    /// want for undoing a posting, because this one does not: the decision log records
    /// <i>keep transactions append-only</i> as a rejected alternative, and a transaction here is
    /// deleted rather than reversed.
    /// </para>
    /// <para>
    /// Test-local, and deliberately not a sibling of <c>ErasureRemnantVocabulary</c> in the production
    /// assembly. That one lives there because it has two consumers in two test projects — one over the
    /// EF design-time model, one over the live catalog — and a test project cannot reference another
    /// test project, so a copy each would be two written-down spellings of one rule with no
    /// adjudicator when they disagree. This list has exactly one consumer, in one file, and no build
    /// gate or deploy-time verifier reads it. Promoting it would move a rule away from the only place
    /// it is argued about and buy nothing.
    /// </para>
    /// <para>
    /// Only the matching is shared: <see cref="IdentifierTokens" /> splits a name into words and asks
    /// whether a pattern appears as a contiguous run of them, which is the same question asked of
    /// column names, answered the same way. Whole tokens rather than substrings, so <c>discovery</c>
    /// matches nothing here — a route pattern is hyphen- and slash-separated and tokenizes cleanly, and
    /// a substring scan over one would fire on words that merely contain a word.
    /// </para>
    /// </remarks>
    private static readonly string[] ReversalVocabulary =
    [
        "reactivate",
        "reactivated",
        "reactivation",
        "reinstate",
        "reinstated",
        "reinstatement",
        "restore",
        "restored",
        "restoration",
        "resurrect",
        "resurrected",
        "resurrection",
        "reverse",
        "reversal",
        "revert",
        "reverted",
        "undelete",
        "undeleted",
        "undeletion",
        "undo",
        "undone",
        "unerase",
        "unerased",
    ];

    /// <summary>
    /// The words above, pre-split into the tokens a name's own tokens are matched against.
    /// </summary>
    /// <remarks>
    /// The way both name vocabularies compile theirs — <c>ErasureRemnantVocabulary.CompiledRules</c> is
    /// the same expression over the same tokenizer. Not for speed: the scan is a handful of short token
    /// runs and a hash-based structure could not answer a contiguous-run question anyway. The compiled
    /// shape is what makes it impossible for an entry to tokenize differently at two call sites — a
    /// spelling's tokens are decided once, at the list, rather than by whichever reader reached it —
    /// and this is the list a future author extends, so the entry they add inherits that property
    /// without having to know about it.
    /// </remarks>
    private static readonly string[][] CompiledReversalVocabulary =
        [.. ReversalVocabulary.Select(IdentifierTokens.Tokenize)];

    /// <summary>The path Pin B freezes: the erasure resource, and anything routed beneath it.</summary>
    /// <remarks>
    /// <para>
    /// <b>The erasure resource, not the <c>/api/me</c> group it hangs off, and the narrowing is
    /// deliberate.</b> <c>/api/me</c> is the current principal's namespace rather than erasure's —
    /// <c>AccountErasureEndpoints</c> says why it was chosen, and the reason is about naming the caller
    /// instead of colliding with <c>Domain.Accounts.Account</c>, not about what the namespace is for.
    /// Frozen to one route it refuses every ordinary thing a principal's namespace could serve: a read
    /// of the current principal, a list of its sessions, an export of its own data. That is the shape
    /// of pin the <c>cancel</c> carve-out above argues against — one standing between an author and
    /// work they were told to do, which is a pin they delete rather than argue with. Narrowed, it still
    /// catches every reversal that would naturally hang off the erasure resource —
    /// <c>/api/me/erasure/restore</c>, <c>/api/me/erasure/undo</c>,
    /// <c>/api/me/erasure/second-chance</c> — and argues with nothing else.
    /// </para>
    /// <para>
    /// Compared <b>segment by segment</b> and <b>case-insensitively</b>, and neither is decoration. A
    /// raw string prefix also selects <c>/api/members</c> and <c>/api/merchants</c>, which share its
    /// text and none of its meaning. An ordinal comparison misses a group mapped
    /// <c>MapGroup("/API/Me")</c> entirely: a route pattern keeps the casing it was written in, while
    /// ASP.NET routing matches it case-insensitively — so the one spelling that escapes the pin is a
    /// spelling that still serves requests.
    /// </para>
    /// </remarks>
    private const string ErasureResourcePrefix = "/api/me/erasure";

    /// <summary>
    /// The single route that may live under <see cref="ErasureResourcePrefix" />.
    /// </summary>
    /// <remarks>
    /// <b>Read this before editing the literal.</b> Exactly one route may ever join this set, and only
    /// under the carve-out described on <see cref="ReversalVocabulary" />: a route that cancels a
    /// <i>scheduled</i> erasure before it takes effect. Anything else added here — a read of what is
    /// about to be erased, a download, a second-chance page, a route restoring anything — is a route
    /// this requirement refuses, and the argument for it belongs in
    /// <c>docs/business-logic/erasure.md</c> before it belongs in this string.
    /// </remarks>
    private const string ErasureResourceSurface = "POST /api/me/erasure";

    /// <summary>
    /// No route in the whole table names a reversal, in its pattern, its display name or its endpoint
    /// name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A word list over the whole table rather than over one group, because the promise this refuses
    /// is not confined to the erasure group. A <c>POST /api/accounts/{id}/undelete</c> would reverse
    /// less than an account erasure and would still be the product saying deletes come back.
    /// </para>
    /// <para>
    /// Three names per endpoint, and the third is the load-bearing one.
    /// <c>WithName("RestoreAccount")</c> writes <see cref="IEndpointNameMetadata" /> and leaves
    /// <see cref="Endpoint.DisplayName" /> at the framework's <c>"HTTP: {verb} {pattern}"</c> —
    /// verified against this framework version rather than assumed, because scanning only the display
    /// name would have read the pattern back a second time and reported nothing. The display name is
    /// scanned because <c>WithDisplayName</c> is what sets it, and that is reason enough on its own: it
    /// is a second place the word can appear, and reading it costs a tokenization.
    /// </para>
    /// <para>
    /// <b>Both of those sources need a control of their own, because the live table cannot supply
    /// one.</b> Neither <c>WithName</c> nor <c>WithDisplayName</c> is called anywhere in <c>Api/</c>,
    /// so on the table this pin reads the endpoint name is empty for every endpoint and the display
    /// name is the pattern tokenized a second time. Deleting either line from <see cref="NamesOf" />
    /// leaves this pin green — measured, not inferred — so two of its three sources are proved by
    /// <see cref="RouteTable_WhenADisplayNameWouldReverseOne_ReportsARouteThatWouldReverseOne" /> and
    /// <see cref="RouteTable_WhenAnEndpointNameWouldReverseOne_ReportsARouteThatWouldReverseOne" />
    /// and by nothing else.
    /// </para>
    /// <para>
    /// Its limit is the vocabulary: this catches the route somebody names honestly, and a reversal
    /// route named <c>/api/me/erasure/second-chance</c> walks past it. That is the gap
    /// <see cref="ErasureResource_MapsExactlyTheOneDestructiveRoute" /> closes within its own scope,
    /// and the two are worth having together for exactly that reason.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RouteTable_OffersNoPathThatReversesAnErasure()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = [.. dataSource.Endpoints.OfType<RouteEndpoint>()];
        string[] offenders = ReversalRoutesIn(endpoints);

        // Assert — the non-vacuity guard first. An empty route table carries no reversal word, so it
        // satisfies the claim below perfectly while having read nothing.
        await Assert.That(endpoints.Length).IsGreaterThan(0);

        // Joined rather than compared as a collection, so a failure names the offending route instead
        // of only reporting that a set was not empty.
        await Assert.That(string.Join(", ", offenders)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The control for <see cref="RouteTable_OffersNoPathThatReversesAnErasure" />'s first source: the
    /// same scan, over a table that does hold a route whose <i>pattern</i> names a reversal.
    /// </summary>
    /// <remarks>
    /// The same helper as the pin, not a second scan written for the control. A control exercising a
    /// separately written scan proves that <i>that</i> scan can fail and says nothing about the one
    /// that ships. Hand-built endpoints rather than a host, so this opens nothing and runs in
    /// milliseconds.
    /// </remarks>
    [Test]
    public async Task RouteTable_WhenARouteWouldReverseOne_ReportsARouteThatWouldReverseOne()
    {
        // Arrange
        RouteEndpoint[] endpoints = [Route("POST", "/api/me/erasure/restore")];

        // Act
        string[] offenders = ReversalRoutesIn(endpoints);

        // Assert
        await Assert.That(string.Join(", ", offenders)).IsEqualTo("POST /api/me/erasure/restore");
    }

    /// <summary>
    /// The control for the scan's second source: an innocent pattern under a <i>display name</i> that
    /// names a reversal.
    /// </summary>
    /// <remarks>
    /// The shape <c>WithDisplayName</c> leaves behind. Without this the display-name line in
    /// <see cref="NamesOf" /> is unproven — on the live table it only ever hands back the pattern the
    /// first line already read.
    /// </remarks>
    [Test]
    public async Task RouteTable_WhenADisplayNameWouldReverseOne_ReportsARouteThatWouldReverseOne()
    {
        // Arrange
        RouteEndpoint[] endpoints =
        [
            Route("POST", "/api/me/erasure", displayName: "Undelete the account"),
        ];

        // Act
        string[] offenders = ReversalRoutesIn(endpoints);

        // Assert
        await Assert.That(string.Join(", ", offenders)).IsEqualTo("POST /api/me/erasure");
    }

    /// <summary>
    /// The control for the scan's third source: an innocent pattern and an innocent display name under
    /// an <i>endpoint name</i> that names a reversal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The endpoint name is attached by the framework's own <c>WithName</c> convention rather than by a
    /// hand-made metadata item, so this control also pins the framework behaviour the third source
    /// exists for: <c>WithName</c> writes <see cref="IEndpointNameMetadata" /> and does <b>not</b>
    /// touch <see cref="Endpoint.DisplayName" />. Both halves are asserted below. Were it to set the
    /// display name too, the third line of <see cref="NamesOf" /> would be redundant and this control
    /// would be passing for the second line's reason.
    /// </para>
    /// <para>
    /// It is also the name a published API document carries the reversal under: an OpenAPI
    /// <c>operationId</c> is read from <see cref="IEndpointNameMetadata" /> and not from the display
    /// name, so a route named here advertises the reversal to every generated client.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RouteTable_WhenAnEndpointNameWouldReverseOne_ReportsARouteThatWouldReverseOne()
    {
        // Arrange
        RouteEndpoint endpoint = Route("POST", "/api/me/erasure", endpointName: "RestoreAccount");

        // Act
        string[] offenders = ReversalRoutesIn([endpoint]);

        // Assert
        await Assert.That(string.Join(", ", offenders)).IsEqualTo("POST /api/me/erasure");

        // The framework behaviour this control rests on, pinned rather than assumed.
        await Assert.That(endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .IsEqualTo("RestoreAccount");
        await Assert.That(endpoint.DisplayName).IsEqualTo("HTTP: POST /api/me/erasure");
    }

    /// <summary>
    /// A route mapped with no method constraint is named by its pattern alone.
    /// </summary>
    /// <remarks>
    /// <c>MapHealthChecks</c> maps exactly that way, so the live table already holds one such endpoint
    /// — <c>/health</c>, which carries no <see cref="HttpMethodMetadata" /> at all, verified against
    /// this framework version. No filter in this class selects it today, which is what makes the
    /// leading space a naive join produces harmless <i>and</i> worth refusing: the day a filter does
    /// select one, a line beginning with a space reads as a formatting defect in the test rather than
    /// as the route it is naming, and a reader who distrusts the message stops reading the verdict.
    /// </remarks>
    [Test]
    public async Task RouteTable_WhenAMethodlessRouteWouldReverseOne_NamesItByItsPatternAlone()
    {
        // Arrange
        RouteEndpoint[] endpoints = [MethodlessRoute("/api/me/erasure/restore")];

        // Act
        string[] offenders = ReversalRoutesIn(endpoints);

        // Assert
        await Assert.That(string.Join(", ", offenders)).IsEqualTo("/api/me/erasure/restore");
    }

    /// <summary>
    /// Every spelling on <see cref="ReversalVocabulary" /> really is matched by the scan that reads it.
    /// </summary>
    /// <remarks>
    /// Written out as <c>[Arguments]</c> cases rather than derived from the list, and the difference is
    /// the whole point: a control driven from the vocabulary asserts that the list matches itself, and
    /// would go on passing over an entry that tokenizes to nothing. Each case here is a claim made
    /// independently of the array, so a spelling is <i>proved</i> to match rather than merely added.
    /// </remarks>
    [Test]
    [Arguments("reactivate")]
    [Arguments("reactivated")]
    [Arguments("reactivation")]
    [Arguments("reinstate")]
    [Arguments("reinstated")]
    [Arguments("reinstatement")]
    [Arguments("restore")]
    [Arguments("restored")]
    [Arguments("restoration")]
    [Arguments("resurrect")]
    [Arguments("resurrected")]
    [Arguments("resurrection")]
    [Arguments("reverse")]
    [Arguments("reversal")]
    [Arguments("revert")]
    [Arguments("reverted")]
    [Arguments("undelete")]
    [Arguments("undeleted")]
    [Arguments("undeletion")]
    [Arguments("undo")]
    [Arguments("undone")]
    [Arguments("unerase")]
    [Arguments("unerased")]
    public async Task ReversalVocabulary_MatchesEverySpellingItLists(string spelling)
    {
        // Arrange
        RouteEndpoint[] endpoints = [Route("POST", $"/api/me/erasure/{spelling}")];

        // Act
        string[] offenders = ReversalRoutesIn(endpoints);

        // Assert
        await Assert.That(string.Join(", ", offenders))
            .IsEqualTo($"POST /api/me/erasure/{spelling}");
    }

    /// <summary>
    /// The vocabulary holds exactly the spellings the control above proves, and no others.
    /// </summary>
    /// <remarks>
    /// Without this, a spelling added to the array without an <c>[Arguments]</c> case beside it is an
    /// entry nothing has ever seen match, which is the exact state the coverage control exists to
    /// refuse. This is the assertion that makes "every spelling is proved" a check rather than a
    /// promise, and it costs an author one literal to edit twice.
    /// </remarks>
    [Test]
    public async Task ReversalVocabulary_HoldsExactlyTheSpellingsTheCoverageControlProves()
    {
        // Arrange, Act — the list as written, in the order it is written.
        string written = string.Join(", ", ReversalVocabulary);

        // Assert
        await Assert.That(written).IsEqualTo(
            "reactivate, reactivated, reactivation, reinstate, reinstated, reinstatement, restore, "
            + "restored, restoration, resurrect, resurrected, resurrection, reverse, reversal, "
            + "revert, reverted, undelete, undeleted, undeletion, undo, undone, unerase, unerased");
    }

    /// <summary>
    /// The erasure resource carries exactly one route, and it is the destructive one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the pin that carries the weight, and what makes it worth its churn is that it is
    /// <b>exhaustive within its scope</b>. No naming loophole gets through it:
    /// <c>/api/me/erasure/second-chance</c> trips it where a word list would not, because it does not
    /// ask what a route is called, it asks how many there are. Exhaustive pins usually cost more than
    /// they return — this one does not, because the scope is one resource whose whole meaning is a
    /// single destructive act, rather than a namespace that grows. See
    /// <see cref="ErasureResourcePrefix" /> for why the scope is the resource and not the
    /// <c>/api/me</c> group it hangs off.
    /// </para>
    /// <para>
    /// And it is extension by design rather than a test somebody deletes. If a cancellation route is
    /// ever added here, adding it means editing a one-line literal that sits directly under a doc
    /// comment stating which single route may join this set and why — so the diff lands in the exact
    /// place a reviewer should be confronting the requirement, at the moment they are least able to
    /// skip it. A pin that can only be satisfied by deletion teaches an author to delete it.
    /// </para>
    /// <para>
    /// <b>Its known limits, stated rather than engineered around:</b> the route table is the capability
    /// boundary only because this codebase has no background jobs and no second entry point. A
    /// reversal driven by a hosted service, a queue consumer or a deploy-time tool would have no route
    /// and would slip both this pin and the word scan above. Closing that would mean enumerating
    /// hosted services and their work, which is a different check over a surface that does not exist
    /// yet — and inventing it now would pin an empty set and read as coverage. <b>The environment is
    /// the second limit, and it is not hypothetical:</b> both pins boot the factory in
    /// <c>Production</c>, while <c>Api/Program.cs</c> maps routes inside an <c>IsDevelopment()</c>
    /// block, so a route mapped there reaches neither pin. One route lives in that block today,
    /// <c>MapOpenApi</c>, and it is the reason the block is worth naming here: what escapes is not a
    /// surface nobody has built, it is a surface that already exists.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ErasureResource_MapsExactlyTheOneDestructiveRoute()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = [.. dataSource.Endpoints.OfType<RouteEndpoint>()];
        string[] surface = ErasureResourceSurfaceOf(endpoints);

        // Assert — the non-vacuity guard first, for the reason the scan above states it: a route table
        // read before it was populated has no erasure route either.
        await Assert.That(endpoints.Length).IsGreaterThan(0);

        // Joined rather than compared as a collection, so a failure names the route that joined the
        // resource instead of reporting that two sets differ.
        await Assert.That(string.Join(", ", surface)).IsEqualTo(ErasureResourceSurface);
    }

    /// <summary>
    /// The control for <see cref="ErasureResource_MapsExactlyTheOneDestructiveRoute" />: the same
    /// filter, over a table where a second route has joined the resource.
    /// </summary>
    /// <remarks>
    /// The probe is named <c>/api/me/erasure/second-chance</c> on purpose — it carries none of
    /// <see cref="ReversalVocabulary" />'s words, so it is precisely the route the word scan cannot
    /// see. This control therefore does double duty: it proves the filter can fail, and it demonstrates
    /// the loophole the exhaustive pin exists to close.
    /// </remarks>
    [Test]
    public async Task ErasureResource_WhenARouteJoinsIt_ReportsARouteAddedToTheResource()
    {
        // Arrange
        RouteEndpoint[] endpoints =
        [
            Route("POST", "/api/me/erasure"),
            Route("GET", "/api/me/erasure/second-chance"),
        ];

        // Act
        string[] surface = ErasureResourceSurfaceOf(endpoints);

        // Assert
        await Assert.That(string.Join(", ", surface))
            .IsEqualTo("GET /api/me/erasure/second-chance, POST /api/me/erasure");
    }

    /// <summary>
    /// The width control for the narrowed scope: a neighbouring path that merely shares the prefix's
    /// text is outside it.
    /// </summary>
    /// <remarks>
    /// Both probes are real English words a resource group could plausibly be called, and both open
    /// with the seven characters of <c>/api/me</c>. Under a raw string prefix each of them joins the
    /// frozen set and fails the pin the day it is mapped — and a pin failing on a route that has
    /// nothing to do with erasure is a pin whose next reader widens or deletes it.
    /// </remarks>
    [Test]
    public async Task ErasureResource_WhenAPathMerelySharesItsText_LeavesThatPathOutside()
    {
        // Arrange
        RouteEndpoint[] endpoints =
        [
            Route("POST", "/api/me/erasure"),
            Route("GET", "/api/members"),
            Route("GET", "/api/merchants"),
        ];

        // Act
        string[] surface = ErasureResourceSurfaceOf(endpoints);

        // Assert
        await Assert.That(string.Join(", ", surface)).IsEqualTo(ErasureResourceSurface);
    }

    /// <summary>
    /// The width control for the casing: a resource mapped in another casing is still inside the scope.
    /// </summary>
    /// <remarks>
    /// <c>MapGroup("/API/Me")</c> is a legal spelling of the same group — a route pattern keeps the
    /// casing it was written in, and ASP.NET routing matches it case-insensitively — so under an
    /// ordinal comparison the whole resource, reversal routes and all, sits outside the pin while
    /// serving requests exactly as it did.
    /// </remarks>
    [Test]
    public async Task ErasureResource_WhenMappedInAnotherCasing_IsStillInsideTheScope()
    {
        // Arrange
        RouteEndpoint[] endpoints =
        [
            Route("POST", "/API/Me/Erasure"),
            Route("GET", "/API/Me/Erasure/second-chance"),
        ];

        // Act
        string[] surface = ErasureResourceSurfaceOf(endpoints);

        // Assert
        await Assert.That(string.Join(", ", surface))
            .IsEqualTo("GET /API/Me/Erasure/second-chance, POST /API/Me/Erasure");
    }

    /// <summary>
    /// The <c>"{METHOD} {pattern}"</c> line of every endpoint whose pattern, display name or endpoint
    /// name carries one of <see cref="ReversalVocabulary" />'s words.
    /// </summary>
    /// <param name="endpoints">
    /// A route table — the live one under the pin, a hand-built array under the control. Taking a list
    /// rather than the factory is what lets one helper serve both.
    /// </param>
    private static string[] ReversalRoutesIn(IEnumerable<RouteEndpoint> endpoints) =>
        endpoints
            .Where(endpoint => NamesOf(endpoint).Any(SaysReversal))
            .Select(SurfaceLineOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The <c>"{METHOD} {pattern}"</c> line of every endpoint routed at or beneath
    /// <see cref="ErasureResourcePrefix" />.
    /// </summary>
    /// <param name="endpoints">A route table, live or hand-built, for the reason stated above.</param>
    private static string[] ErasureResourceSurfaceOf(IEnumerable<RouteEndpoint> endpoints) =>
        endpoints
            .Where(endpoint => IsUnderErasureResource(endpoint.RoutePattern.RawText ?? string.Empty))
            .Select(SurfaceLineOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Whether a route pattern names the erasure resource or something routed beneath it.
    /// </summary>
    /// <remarks>
    /// Segment by segment rather than by string prefix, and case-insensitively rather than ordinally,
    /// for the two reasons written out on <see cref="ErasureResourcePrefix" />. The prefix is split
    /// here rather than held as a second constant so that the two spellings cannot drift.
    /// </remarks>
    private static bool IsUnderErasureResource(string pattern)
    {
        string[] segments = SegmentsOf(pattern);
        string[] prefix = SegmentsOf(ErasureResourcePrefix);

        return segments.Length >= prefix.Length
            && segments.Take(prefix.Length).SequenceEqual(prefix, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A route path's non-empty segments, in order.</summary>
    private static string[] SegmentsOf(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Every string about an endpoint that a reversal could be named in.</summary>
    private static IEnumerable<string> NamesOf(RouteEndpoint endpoint)
    {
        yield return endpoint.RoutePattern.RawText ?? string.Empty;
        yield return endpoint.DisplayName ?? string.Empty;
        yield return endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? string.Empty;
    }

    /// <summary>Whether one name's words hold a reversal word as a contiguous run.</summary>
    private static bool SaysReversal(string name)
    {
        string[] tokens = IdentifierTokens.Tokenize(name);

        return CompiledReversalVocabulary.Any(word => IdentifierTokens.ContainsRun(tokens, word));
    }

    /// <summary>
    /// The verbs and pattern of one endpoint, as the one line a failure message names it by.
    /// </summary>
    /// <remarks>
    /// The verbs are joined rather than taking the first, so a route mapped to several methods reads
    /// as one line that names all of them instead of silently reporting only one. An endpoint mapped
    /// with no method constraint at all is named by its pattern alone — see
    /// <see cref="RouteTable_WhenAMethodlessRouteWouldReverseOne_NamesItByItsPatternAlone" /> for the
    /// shape that reaches this and for why the leading space a naive join would carry is worth
    /// refusing.
    /// </remarks>
    private static string SurfaceLineOf(RouteEndpoint endpoint)
    {
        string[] methods = [.. endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []];
        string verbs = string.Join('|', methods.Order(StringComparer.Ordinal));
        string pattern = endpoint.RoutePattern.RawText ?? string.Empty;

        return verbs.Length == 0 ? pattern : $"{verbs} {pattern}";
    }

    /// <summary>
    /// One hand-built endpoint, in the shape a minimal-API route reaches the route table in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built through <see cref="RouteEndpointBuilder" /> — the type the framework itself builds
    /// endpoints with — rather than through the <see cref="RouteEndpoint" /> constructor, because
    /// <paramref name="endpointName" /> is then applied by the framework's own <c>WithName</c>
    /// convention instead of by a hand-made metadata item. A control that attached
    /// <see cref="IEndpointNameMetadata" /> itself would prove the scan can read metadata somebody
    /// wrote for it, and say nothing about what a real <c>WithName</c> leaves behind.
    /// </para>
    /// <para>
    /// The default display name copies the framework's own <c>"HTTP: {verb} {pattern}"</c>, verified
    /// against this framework version, so a control cannot pass on a display name shaped unlike any
    /// real one.
    /// </para>
    /// </remarks>
    private static RouteEndpoint Route(
        string method,
        string pattern,
        string? displayName = null,
        string? endpointName = null)
    {
        RouteEndpointBuilder builder = new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0)
        {
            DisplayName = displayName ?? $"HTTP: {method} {pattern}",
        };
        builder.Metadata.Add(new HttpMethodMetadata([method]));

        if (endpointName is not null)
        {
            ConventionCollector conventions = new();
            conventions.WithName(endpointName);
            foreach (Action<EndpointBuilder> convention in conventions.Conventions)
            {
                convention(builder);
            }
        }

        return (RouteEndpoint)builder.Build();
    }

    /// <summary>
    /// One hand-built endpoint with no method constraint, the shape <c>MapHealthChecks</c> reaches the
    /// route table in.
    /// </summary>
    /// <remarks>
    /// A separate helper rather than a nullable method on <see cref="Route" />: every other caller
    /// names a verb, and a nullable first parameter would let one omit it by accident. It sets no
    /// display name either, rather than inventing one — the framework has no default for an endpoint
    /// mapped this way, and <c>MapHealthChecks</c> supplies its own (<c>"Health checks"</c>) — so this
    /// also exercises the null branch <see cref="NamesOf" /> and <see cref="SurfaceLineOf" /> carry.
    /// </remarks>
    private static RouteEndpoint MethodlessRoute(string pattern) =>
        (RouteEndpoint)new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0).Build();

    /// <summary>
    /// Collects the conventions an endpoint-building extension applies, so one can be replayed against
    /// a <see cref="RouteEndpointBuilder" /> without a host.
    /// </summary>
    private sealed class ConventionCollector : IEndpointConventionBuilder
    {
        public List<Action<EndpointBuilder>> Conventions { get; } = [];

        public void Add(Action<EndpointBuilder> convention) => Conventions.Add(convention);
    }
}
