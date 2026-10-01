using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Security;
using Domain.Sessions;
using Domain.Transactions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The read that lets an interrupted rotation resume — <c>GET /api/me/key-rotation</c> — driven over
/// real HTTP against a real account.
/// </summary>
/// <remarks>
/// <para>
/// <b>BEFORE YOU ADD A CASE HERE: DO NOT COPY <c>IsNotEqualTo(HttpStatusCode.NotFound)</c> FROM THE
/// THREE SIBLING FILES. ON THIS PATH IT ASSERTS NOTHING.</b> <c>POST /api/me/key-rotation</c> already
/// owns this exact pattern, so routing matches it and rejects the <em>verb</em>: an unmapped <c>GET</c>
/// here is answered <b>405</b> and <b>404 is unreachable</b> — measured at <c>d5dc131</c>. Every case
/// below therefore names <c>MethodNotAllowed</c> as the status that means "no GET is mapped". The full
/// argument, and the locked-session half of it, is four paragraphs down; it is repeated here because
/// this is the line a reader writes first and the sibling idiom is the one they will reach for.
/// </para>
/// <para>
/// <b>WITHOUT THIS ROUTE AN INTERRUPTED ROTATION IS PERMANENT DATA LOSS RATHER THAN A RECOVERABLE
/// STATE, AND THAT IS THE WHOLE OF WHY THE FIRST CASE BELOW IS FIRST.</b> The new content key lives
/// only in the tab that drew it — <c>AccountKeyCustodyService</c> persists nothing across reloads, by
/// decision — so a reload loses it, while every row a chunk already rewrote is sealed under it.
/// <c>wrapped_account_keys</c> still holds the <em>old</em> generation until the promotion, so the
/// staged seals are the only copies of the new one. <c>docs/business-logic/key-rotation.md</c> already
/// promises an interruption is resumable; this route is what makes that sentence true, and this file is
/// the commit's evidence.
/// </para>
/// <para>
/// <b>Every case reads the wire shape rather than naming a response record</b>, the idiom
/// <c>KeyRotationBeginEndpointTests</c>, <c>ResealChunkEndpointTests</c> and
/// <c>KeyRotationCompletionEndpointTests</c> keep for the same reason: nothing here depends on a type
/// the endpoint has not been written with yet, so the red is a 405 from a route the application does not
/// serve and turns into an assertion the moment one exists. It also means <b>every wire member is pinned
/// by use</b> — <c>rotation</c>, and beneath it <c>rotationId</c>, <c>stagedRotationEpoch</c>,
/// <c>stagedManifest</c>, <c>startedAtUtc</c>, <c>inventory</c>, <c>maxChunkBytes</c> and <c>seals</c>,
/// each seal carrying <c>factorId</c> and <c>encapsulatedAccountKeys</c>. A route spelling one of them
/// differently reddens the case that reads it rather than passing quietly.
/// </para>
/// <para>
/// <b>200 ALWAYS AND NEVER 404, WHICH IS THE ONE DECISION THIS FILE OWNS OUTRIGHT.</b>
/// <c>AccountKeysResponse</c> argues it for its own route and the reason transfers unchanged: the client
/// reads a failed read as "try again in a minute", which for an account that simply has nothing staged
/// never succeeds. So "nothing is staged" is <c>rotation: null</c> inside a 200 — <b>null rather than an
/// empty object</b>, because an object with an empty seal array is a staged run naming no factor, which
/// is a different and far worse thing for a client to act on. Two cases below read the member as well as
/// the status, since a status assertion alone cannot see an absent member or an empty object.
/// </para>
/// <para>
/// <b>THE UNMAPPED-ROUTE TRAP HAS BITTEN THIS ROUTE GROUP THREE TIMES, AND ON THIS ROUTE IT WEARS A
/// DIFFERENT AND SHARPER FACE. MEASURED AT <c>d5dc131</c>, BEFORE ANY GET WAS MAPPED.</b> The three
/// sibling files are written around "an unserved path answers 403 to a locked session and a bare 404 to
/// everybody else", and the second half of that sentence <b>is not true here</b>. <c>POST</c> already
/// owns this exact path, so routing matches the pattern and rejects the verb: a full session is answered
/// <b>405 Method Not Supported</b>, never 404. The consequence is that the guard those three files lead
/// with — <c>IsNotEqualTo(HttpStatusCode.NotFound)</c> — <b>is vacuous on this path</b>, because 404 is
/// an answer nothing here can produce. Every case below therefore names <b>405</b> as the status that
/// means "no GET is mapped", and none of them relies on 404 to say so.
/// </para>
/// <para>
/// <b>The locked half is worse, and it is measured rather than reasoned.</b> The 405 endpoint routing
/// synthesises carries no authorization metadata, so
/// <see cref="Microsoft.AspNetCore.Authorization.AuthorizationMiddleware" /> applies the fallback policy
/// to it exactly as it would to an unmarked one — measured on this path at <c>d5dc131</c>: a locked
/// session is answered <b>403</b>, and the body's <c>title</c> is <c>Forbidden</c> rather than
/// <see cref="FirstPartyRequestMiddleware.Title" />. <b>So both assertions the locked-session case makes
/// pass against an application in which this route was never mapped</b>, title comparison included. What
/// makes that case about a route at all is its accepting arm, and the arm has to be stronger than the
/// three siblings' because it cannot lean on 404: a <b>200 carrying the <c>rotation</c> member</b>. 405
/// is not 200, and an unmatched or verb-rejected request is written no JSON object with that member in
/// it.
/// </para>
/// <para>
/// <b>Twelve factors under three credentials, which is not a round number chosen for effect.</b> Two
/// passkeys and a card of ten recovery codes: a set of codes is ten separate secrets under a single
/// <see cref="Credential" />, so it is ten <c>wrapped_account_keys</c> rows. A route that answered "the
/// seal" — or only the passkeys' — would let a client resume a run that re-encapsulates the account keys
/// to two factors and orphans ten, which is the silent failure the seal set exists to prevent and which
/// only a many-factor account can see. Each live row carries a filler of its own and each staged seal
/// another, in two blocks that do not overlap and neither of which is
/// <see cref="RepositoryTestHost.SeededAccountKeysFiller" />, so <b>"handed back the staged value" and
/// "handed back the live value" cannot read the same for any factor</b>.
/// </para>
/// <para>
/// <b>Rotations are staged through <see cref="KeyRotation.Begin" /> and the real adapter over the
/// container superuser connection rather than through <c>POST /api/me/key-rotation</c></b>, the trade
/// <c>KeyRotationCompletionEndpointTests</c> makes and for the same reason: the begin is gated by a fresh
/// WebAuthn assertion, which costs a registration ceremony, a synthetic authenticator and a
/// re-authentication nonce per case, and this read reads nothing a begin writes except the staging row and
/// its seals. Driving it here would make every case below able to fail for the begin's reasons, which
/// that file already owns. Through the adapter and the loaded rows, because
/// <see cref="KeyRotationSeal.For" /> reads the owner off both the rotation and the loaded
/// <c>wrapped_account_keys</c> row and refuses a great deal a fabricated row would sail past.
/// </para>
/// <para>
/// <b>Every arrangement runs on <see cref="PostgresTestHost.ConnectionString" /></b> — the container
/// superuser — and only the act runs as the application. <c>key_rotations</c>, <c>key_rotation_seals</c>
/// and <c>wrapped_account_keys</c> all carry <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed
/// connection reports a row that is there exactly as it reports one that is not: arranged on the app role,
/// the two-account case could not tell "scoped correctly" from "seeded nothing".
/// </para>
/// </remarks>
public sealed class KeyRotationResumptionEndpointTests
{
    private const string StatePath = "/api/me/key-rotation";

    /// <summary>
    /// The nearest route that carries no key material, and the control for the <c>no-store</c> case.
    /// </summary>
    private const string NeighbourPath = "/api/me/credentials";

    /// <summary>How many passkeys the account holds, and how many factors a card of codes is.</summary>
    /// <remarks>
    /// Two passkeys rather than one, because a set-shaped bug that answered "the passkey" would be
    /// invisible on an account holding exactly one of everything.
    /// </remarks>
    private const int PasskeyCount = 2;

    /// <inheritdoc cref="PasskeyCount" />
    private const int RecoveryCodeSetSize = 10;

    /// <summary>The number of <c>wrapped_account_keys</c> rows a furnished account holds.</summary>
    /// <remarks>
    /// Written out rather than derived from the fixture, <c>KeyRotationCompletionEndpointTests</c>' habit
    /// with the same number: a seeder that quietly stopped writing the card would otherwise shrink the
    /// factor set back to the shape this number exists to refuse, and every sweep below would go on
    /// passing over a set of two.
    /// </remarks>
    private const int FactorCount = PasskeyCount + RecoveryCodeSetSize;

    /// <summary>The generation a staged run carries here.</summary>
    /// <remarks>
    /// <see cref="RepositoryTestHost" /> seeds an account's first manifest at
    /// <see cref="FactorManifest.MinimumRotationEpoch" />, which is where registration files it, so every
    /// account here has never rotated and the staged generation is its second.
    /// <see cref="StagedRotationEpochLiteral" /> is written out beside the expression that derives it, so
    /// the epoch a resuming client is handed is checked against a number this file states rather than only
    /// against arithmetic it performed.
    /// </remarks>
    private const int StagedRotationEpoch = FactorManifest.MinimumRotationEpoch + 1;

    /// <inheritdoc cref="StagedRotationEpoch" />
    private const int StagedRotationEpochLiteral = 2;

    /// <summary>
    /// The two blocks of fillers this file's payloads carry — the twelve rows as they stand, and the
    /// twelve values their seals stage.
    /// </summary>
    /// <remarks>
    /// Neither block overlaps the other and neither reaches
    /// <see cref="RepositoryTestHost.SeededAccountKeysFiller" />, which is what the default seeding writes.
    /// That is what makes the first case's central claim checkable at all: a route handing back the
    /// <em>live</em> encapsulated value for each factor would be answering a 200 with twelve well-formed
    /// 158-byte values of the right version that resume nothing, and against a defaulted account it would
    /// be indistinguishable from the right answer.
    /// </remarks>
    private const byte StoredFillerBase = 0x40;

    /// <inheritdoc cref="StoredFillerBase" />
    private const byte StagedFillerBase = 0x10;

    /// <summary>The second account's two blocks, disjoint from the first's and from each other.</summary>
    /// <remarks>
    /// A fourth and a fifth block rather than reusing the first account's, because the scoping case
    /// compares <em>bytes</em>: with one block shared, a route answering the bystander's rows would hand
    /// back values identical to the subject's and the comparison would pass on a leak.
    /// </remarks>
    private const byte BystanderStoredFillerBase = 0x70;

    /// <inheritdoc cref="BystanderStoredFillerBase" />
    private const byte BystanderStagedFillerBase = 0xA0;

    /// <summary>
    /// The payload of the <b>thirteenth</b> factor — one enrolled <em>after</em> a run was staged, so it
    /// has a <c>wrapped_account_keys</c> row and no <c>key_rotation_seals</c> row at all.
    /// </summary>
    /// <remarks>
    /// A block of its own, reaching neither the live twelve nor the staged twelve nor the bystander's
    /// two, so that an answer which invented an entry for this factor is caught <b>by its bytes</b> as
    /// well as by its identifier. One value rather than a base, because there is only ever one such row.
    /// </remarks>
    private const byte LateFactorFiller = 0xD0;

    /// <summary>How many bytes a seeded WebAuthn credential id carries.</summary>
    /// <remarks>
    /// Only the width and the distinctness matter: nothing on this path verifies a signature, and the
    /// column is unique, so the bytes are drawn at random rather than filled — two passkeys on one account
    /// need two handles, and a shared host holds two accounts in the scoping case.
    /// </remarks>
    private const int WebAuthnCredentialIdLength = 16;

    /// <summary>
    /// The budget a chunk following this read may spend, written out rather than read off
    /// <c>BeginKeyRotationHandler.MaxChunkBytes</c>.
    /// </summary>
    /// <remarks>
    /// A test that read the number from the code under test would assert only that the code agrees with
    /// itself, and would stay green through a change that halved a resuming client's chunk without anybody
    /// deciding to. <c>KeyRotationBeginEndpointTests</c> makes the same argument about its own copy.
    /// </remarks>
    private const int PublishedMaxChunkBytes = 32 * 1024;

    /// <summary>The minor unit of the currency every seeded account and transaction is denominated in.</summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// Fixed UTC instant for every row this file writes. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing rather than decoration.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The instant above as a wire reader can recognise it, whatever spelling the serializer chose.
    /// </summary>
    /// <remarks>
    /// <b>A prefix rather than a parse, and the reason is the <see cref="DateTimeKind" />.</b> Npgsql reads
    /// a <c>timestamptz</c> back as <see cref="DateTimeKind.Utc" /> and <c>System.Text.Json</c> then writes
    /// a trailing <c>Z</c>, but a <see cref="DateTime" /> that lost its kind anywhere on the way out is
    /// written without one — and <c>GetValue&lt;DateTimeOffset&gt;()</c> over that spelling silently
    /// interprets it in the <em>test machine's</em> zone, which would make this assertion pass or fail by
    /// geography. Comparing the leading characters asserts the instant the run began and asserts nothing
    /// about the offset's spelling, which is the endpoint's to choose.
    /// </remarks>
    private const string SeedInstantPrefix = "2026-06-12T13:14:15";

    /// <summary>
    /// <b>The case that makes recoverability true.</b> After an interrupted run, the read hands back the
    /// staged manifest, the epoch it is filed at, the inventory a resuming client needs as its denominator
    /// and <b>one seal per factor carrying that factor's own staged bytes</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE SEALS ARE THE WHOLE POINT AND THE STATUS IS THE LESSER HALF.</b> The client that began this
    /// run drew a content key and an index key, encapsulated the pair to every factor, and then lost both
    /// to a reload. The only copies of that generation left anywhere are the staged seals — the live
    /// <c>wrapped_account_keys</c> rows still hold the <em>superseded</em> generation until a promotion
    /// runs — so a route that answered the live values instead would be handing a resuming client twelve
    /// well-formed 158-byte values of the right version that open the wrong generation, with nothing
    /// thrown and nothing logged. The two filler blocks are what make those two answers distinguishable,
    /// and each factor is checked against <b>its own</b> staged seal rather than against "some staged
    /// bytes": a route that zipped the two sets positionally would give every factor a value only some
    /// other factor's private key can open, and neither read on this path carries an <c>ORDER BY</c>.
    /// </para>
    /// <para>
    /// <b>Twelve, and not one and not two.</b> Registration files eleven factors and this arrangement
    /// files twelve under three credentials, so an implementation that answered the factor a ceremony
    /// presented, or the account's passkeys alone, is visible here and is invisible on any account holding
    /// one of everything. The count is asserted as a premise before the act, or every sweep below would be
    /// a sweep over a set this file failed to build.
    /// </para>
    /// <para>
    /// <b>The inventory is asserted at six different numbers, and the budget is furnished to produce
    /// them.</b> Five ints in a row is the call shape where a transposed pair compiles and is discovered
    /// later as a progress bar that finishes early, so no two of them may be equal: one account, two
    /// payees, three groups, four categories, five described transactions, and zero named budgets —
    /// registration's budget carries no name, which is what makes <c>budgets</c> a real presence test here
    /// rather than a constant. Two note-less transactions are seeded and must not be counted: the
    /// population is the completeness gate's population, and a denominator including them is one the
    /// client can never reach. Without this, a route answering a defaulted <c>RotationInventory</c> — six
    /// zeroes — would pass on an account that happens to hold nothing.
    /// </para>
    /// <para>
    /// <b>The offenders are collected and asserted empty rather than compared one at a time</b>, so a
    /// failure names every factor whose seal was wrong instead of the first — and asserted on as a
    /// <em>collection</em>, because a TUnit string assertion truncates and a census reporting through
    /// <c>string.Join</c> names only its first offender.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationState_AfterAnInterruptedRun_HandsBackTheStagedManifestAndEverySeal()
    {
        // Arrange — twelve factors, a budget furnished so no two inventory counts are the same number,
        // and a staged run naming every factor.
        await using PostgresTestHost host = await StartHostAsync();
        Rotating rotating = await FurnishAccountAsync(host, "google-resume-owner", StoredFillerBase);
        await FurnishBudgetAsync(host, rotating.BudgetId);
        Staged staged = await StageRotationAsync(host, rotating, StagedFillerBase);
        SortedDictionary<Guid, string> live = await LiveFactorKeysAsync(host, rotating.UserId);

        // The premises, or everything below is vacuous: the account really holds twelve factors, and not
        // one staged value already stands in the live row it is destined for — without which "the staged
        // seal" and "the live value" read the same.
        await Assert.That(rotating.FactorIds.Count).IsEqualTo(FactorCount);
        await Assert.That(live.Count).IsEqualTo(FactorCount);
        await Assert.That(Overlaps(staged.SealsByFactor, live)).IsEmpty();

        // Act
        HttpResponseMessage response = await rotating.Client.GetAsync(StatePath);

        // Assert — the status first, so a body missing because the request was refused reads as the
        // refusal it is rather than as a staged run nobody recognises. 405 rather than 404 is what "no
        // GET is mapped" looks like on a path POST already owns — measured; see this class's remarks.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body.ContainsKey("rotation")).IsTrue();
        await Assert.That(body["rotation"]).IsNotNull();

        JsonObject rotation = body["rotation"]!.AsObject();
        await Assert.That(rotation["rotationId"]!.GetValue<Guid>()).IsEqualTo(staged.RotationId);
        await Assert.That(rotation["stagedRotationEpoch"]!.GetValue<int>())
            .IsEqualTo(StagedRotationEpochLiteral);
        await Assert.That(rotation["stagedRotationEpoch"]!.GetValue<int>()).IsEqualTo(StagedRotationEpoch);
        await Assert.That(rotation["stagedManifest"]!.GetValue<string>()).IsEqualTo(staged.Manifest);
        await Assert.That(rotation["startedAtUtc"]!.GetValue<string>()).StartsWith(SeedInstantPrefix);

        // The denominator a resuming client drives the rest of the run against, six different numbers so
        // a transposed pair cannot pass.
        await Assert.That(rotation["maxChunkBytes"]!.GetValue<int>()).IsEqualTo(PublishedMaxChunkBytes);

        JsonObject inventory = rotation["inventory"]!.AsObject();
        await Assert.That(inventory["accounts"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(inventory["payees"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(inventory["categoryGroups"]!.GetValue<int>()).IsEqualTo(3);
        await Assert.That(inventory["categories"]!.GetValue<int>()).IsEqualTo(4);
        await Assert.That(inventory["transactions"]!.GetValue<int>()).IsEqualTo(5);
        await Assert.That(inventory["budgets"]!.GetValue<int>()).IsEqualTo(0);

        // Every factor, and the bytes filed against each are that factor's OWN staged seal — not one
        // value copied twelve times, not a zipped neighbour's, and not the live row's.
        SortedDictionary<Guid, string> served = SealsIn(rotation);
        await Assert.That(served.Count).IsEqualTo(FactorCount);
        await Assert.That(Differences(staged.SealsByFactor, served)).IsEmpty();
        await Assert.That(Overlaps(served, live)).IsEmpty();
    }

    /// <summary>
    /// A factor enrolled <b>after</b> the run was staged is <b>not</b> in the answer: thirteen live
    /// factors, twelve staged seals, and the read hands back the staged twelve and only those.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THIS IS THE ONLY CASE IN THE FILE WHERE THE LIVE FACTOR SET AND THE STAGED SEAL SET DIFFER,
    /// AND WITHOUT IT A WHOLE CLASS OF WRONG IMPLEMENTATION PASSES EVERYTHING.</b> Every other
    /// arrangement here stages exactly one seal per live factor, so a route that read
    /// <c>wrapped_account_keys</c> and joined <c>key_rotation_seals</c> onto it yields the same twelve
    /// rows as one that read the seals directly, and no assertion anywhere can tell them apart. Here
    /// they answer differently: the seal-driven read answers twelve, and the factor-driven read answers
    /// thirteen.
    /// </para>
    /// <para>
    /// <b>WHAT THE ROUTE MUST NOT DO, STATED AS ACTS RATHER THAN ONLY AS A COUNT.</b> A thirteenth entry
    /// can arrive two ways and both are asserted against separately, because they fail a client
    /// differently. <b>It must not invent one</b> — filling the missing seal with the factor's
    /// <em>live</em> <c>encapsulated_account_keys</c> produces a well-formed 158-byte value of the right
    /// version carrying the <em>superseded</em> generation, and a resuming client that adopted it would
    /// believe this factor already holds the new keys when it holds the old ones. <b>And it must not null
    /// one</b> — an entry whose <c>encapsulatedAccountKeys</c> is JSON null, or absent, is a seal a client
    /// cannot use and will either crash on or skip, and skipping it is the orphaning again. The route's
    /// only correct answer is to <b>leave the factor out</b>, because the run genuinely has no seal for
    /// it: that is what <c>factor_set_moved</c> means, and this is that conflict arriving on the
    /// <em>read</em> side, where nothing below the endpoint refuses it.
    /// </para>
    /// <para>
    /// <b>Leaving it out is not the same as hiding a problem, and the division of labour is worth
    /// stating.</b> A completion of this run will be refused — <c>IKeyRotationRepository.PromoteAsync</c>
    /// raises <c>factor_set_moved</c>, and the remedy is a fresh begin carrying the corrected set — so the
    /// client's way forward is to begin again, not to resume. What this read owes is an honest account of
    /// <em>what was staged</em>, and inventing a thirteenth entry is the one answer that would let a
    /// client believe it may carry on.
    /// </para>
    /// <para>
    /// <b>The enrolment is seeded rather than driven through the registration ceremony</b>, deliberately:
    /// <c>POST /api/passkeys/registration</c> would also promote the factor manifest's generation, which
    /// would put a second, unrelated moving part between the arrangement and the assertion — and this case
    /// is about a <c>wrapped_account_keys</c> row existing with no seal beside it, which is exactly what
    /// the seeder writes.
    /// </para>
    /// <para>
    /// <b>The malformed-entry sweep runs before the keyed comparison</b>, because
    /// <see cref="SealsIn" /> reads <c>encapsulatedAccountKeys</c> as a string and a JSON null there would
    /// die as a bare <see cref="NullReferenceException" /> naming nothing — which is the least useful
    /// possible report of the second of the two acts this case exists to refuse.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationState_WithAFactorEnrolledAfterTheBegin_NamesOnlyTheStagedFactors()
    {
        // Arrange — twelve factors, a run staged over all twelve, and then a thirteenth factor filed
        // against the account with no seal beside it.
        await using PostgresTestHost host = await StartHostAsync();
        Rotating rotating = await FurnishAccountAsync(host, "google-resume-owner", StoredFillerBase);
        Staged staged = await StageRotationAsync(host, rotating, StagedFillerBase);
        Guid enrolledAfter = await EnrolOneMoreFactorAsync(host, rotating.UserId);
        SortedDictionary<Guid, string> live = await LiveFactorKeysAsync(host, rotating.UserId);

        // The premises, and this case is nothing without them: the account really holds THIRTEEN live
        // factors while the run staged TWELVE seals, the late factor really is one of the live set and
        // really is not one of the staged set, and it carries bytes nothing else on this host carries.
        await Assert.That(live.Count).IsEqualTo(FactorCount + 1);
        await Assert.That(staged.SealsByFactor.Count).IsEqualTo(FactorCount);
        await Assert.That(live.ContainsKey(enrolledAfter)).IsTrue();
        await Assert.That(staged.SealsByFactor.ContainsKey(enrolledAfter)).IsFalse();
        await Assert.That(await StagedSealCountAsync(host, rotating.UserId)).IsEqualTo(FactorCount);

        // Act
        HttpResponseMessage response = await rotating.Client.GetAsync(StatePath);

        // Assert
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject rotation = (await ReadJsonObjectAsync(response))["rotation"]!.AsObject();

        // It did not NULL one: swept before the keyed read, so an entry carrying no usable value is
        // named rather than dying as a NullReferenceException inside the comparison below.
        await Assert.That(UnusableSealsIn(rotation)).IsEmpty();

        // It did not INVENT one: the count is twelve, the late factor is absent by name, and no served
        // value is that factor's live payload — three ways of saying it, because a route that filled the
        // gap with the superseded generation satisfies a count and a null check alike.
        SortedDictionary<Guid, string> served = SealsIn(rotation);
        await Assert.That(served.Count).IsEqualTo(FactorCount);
        await Assert.That(served.ContainsKey(enrolledAfter)).IsFalse();
        await Assert.That(served.Values).DoesNotContain(live[enrolledAfter]);

        // And what it DID hand back is the staged twelve, each against its own factor — both directions,
        // so a thirteenth entry under any other identifier is reported too.
        await Assert.That(Differences(staged.SealsByFactor, served)).IsEmpty();
    }

    /// <summary>
    /// An account with nothing staged is answered <b>200</b> carrying a <b>null</b> rotation — never a
    /// 404, and never an empty object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>404 IS THE SINGLE MOST LIKELY THING A LATER READER "FIXES" THIS INTO</b>, because "nothing
    /// found → 404" is the right instinct almost everywhere else. It is wrong here for the reason
    /// <c>AccountKeysResponse</c> gives about its own route:
    /// <c>AccountKeyCustodyService</c> and every client beside it read a failed read as "try again in a
    /// minute", which for an account that has simply never begun a rotation never succeeds. A person
    /// arriving at a settings screen that cannot tell "no run in flight" from "the server is unwell" is
    /// handed the one instruction that can never work.
    /// </para>
    /// <para>
    /// <b>The member is read as well as the status, and both halves of that reading are load-bearing.</b>
    /// <c>ContainsKey</c> alone is satisfied by a JSON null, and a null check alone is satisfied by a
    /// member that is absent entirely — so the two together are what say the response carries the member
    /// and carries nothing in it. <b>Null rather than an empty object</b>, because
    /// <c>{"rotation":{"seals":[]}}</c> is a staged run naming no factor, which is a state no path
    /// produces and which a client would read as a rotation it may resume by re-encapsulating to nobody.
    /// </para>
    /// <para>
    /// <b>The account is seeded rather than furnished</b>: no factors, no staged run and no narrative
    /// rows, because what is being measured is the answer to an account that has nothing, and building one
    /// that has something would make this case able to fail for the first case's reasons.
    /// </para>
    /// <para>
    /// <b>AND <c>Cache-Control</c> IS READ HERE TOO, WHICH IS THE HALF
    /// <see cref="KeyRotationState_IsAnsweredWithNoStore" /> STRUCTURALLY CANNOT HOLD.</b> That case
    /// reads the header off a <em>populated</em> answer, so a route writing it from inside an
    /// <c>if (rotation is not null)</c> satisfies it completely while serving a cacheable 200 to every
    /// account that has nothing staged. Cacheability is a property of the <b>route</b> and not of what
    /// the route happened to find — a null body carries no key material today, and a route deciding the
    /// header per response is one member away from the day it does.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationState_WithNothingStaged_Answers200AndANullRotation()
    {
        // Arrange — a signed-in account that has never begun a rotation.
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            "google-resume-unstaged", kind: SessionKind.Full);

        // The premise: nothing is staged, read on the superuser connection because user_isolation makes a
        // policed read of a row that IS there report exactly what an absent one reports.
        await Assert.That(await StagedRotationCountAsync(host, signedIn.UserId)).IsEqualTo(0);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(StatePath);

        // Assert — 200, and never the 404 a reader will reach for. Both are named: 404 because it is the
        // status this case exists to refuse, and 405 because it is the one an unmapped GET really
        // produces on a path POST already owns, which is what makes the 404 line a claim about the
        // endpoint's decision rather than about routing.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NoContent);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The member is there and it is null: present says the shape did not change, null says there is
        // no run rather than one naming nobody.
        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body.ContainsKey("rotation")).IsTrue();
        await Assert.That(body["rotation"]).IsNull();

        // AND THE HEADER IS READ ON THIS PATH TOO, WHICH IS WHY THIS LINE IS HERE RATHER THAN ONLY IN
        // KeyRotationState_IsAnsweredWithNoStore. That case reads it off a populated answer, so a route
        // that wrote the header from inside an `if (rotation is not null)` would satisfy it completely
        // and serve a cacheable 200 on every null answer. The header belongs to the ROUTE rather than to
        // what the route happened to find: a null body carries no key material today, and a route whose
        // cacheability is decided per response is one line away from the day it does.
        await Assert.That(HeaderOf(response, "Cache-Control")).IsEqualTo("no-store");
    }

    /// <summary>
    /// The response is <c>Cache-Control: no-store</c>, and the neighbouring route beside it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THIS IS THE SECOND ROUTE IN THE PRODUCT THAT RETURNS KEY MATERIAL, AND THE FIRST ONE'S OWN
    /// REMARK SAYS IT IS THE ONLY ONE.</b> <c>AccountKeyEndpoints.NoStore</c> is <see langword="private" />
    /// and documents itself as "the one endpoint in the application that has a reason to"; a staged seal
    /// is an encapsulation of the account's next content key and index key, and a staged manifest is a
    /// sealed blob, so that sentence stops being true the moment this route ships. Hoisting the constant to
    /// one owner is the implementer's job; what this case holds is that <em>this</em> route states its own
    /// cacheability, whichever constant it reads.
    /// </para>
    /// <para>
    /// <b>Compared to the exact string rather than through
    /// <see cref="System.Net.Http.Headers.CacheControlHeaderValue" />.</b> A parsed read answers
    /// <c>NoStore</c> true for <c>no-store, no-cache, private, max-age=0</c> as readily as for
    /// <c>no-store</c> alone, and the existing endpoint's remarks refuse that spelling on the grounds that
    /// the four together read as more careful and store more — <c>no-cache</c> permits storage,
    /// <c>private</c> permits a browser cache, and <c>max-age=0</c> without <c>no-store</c> permits a
    /// stale-serving cache to keep the bytes. An exact comparison is the only one that can say so.
    /// </para>
    /// <para>
    /// <b>The neighbouring route is the control, and without it this case passes on the thing
    /// <c>SecurityHeadersMiddleware</c> refuses to be.</b> A blanket <c>Cache-Control</c> written for every
    /// response would satisfy the first assertion while settling the question in the one place that knows
    /// least about what was returned. <c>GET /api/me/credentials</c> is the nearest route carrying no key
    /// material, so it is the one that must come back with no such header at all.
    /// </para>
    /// <para>
    /// <b>The account has a staged run, so the header is read off a response that really carried key
    /// material</b> rather than off a body whose rotation is null. <b>The null path is held next door</b>
    /// — <see cref="KeyRotationState_WithNothingStaged_Answers200AndANullRotation" /> reads the same
    /// header, because this case alone would be satisfied by a route that wrote it only when it found
    /// something to write about.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationState_IsAnsweredWithNoStore()
    {
        // Arrange — a staged run, so the response under test really carries encapsulated values and a
        // sealed manifest.
        await using PostgresTestHost host = await StartHostAsync();
        Rotating rotating = await FurnishAccountAsync(host, "google-resume-owner", StoredFillerBase);
        Staged staged = await StageRotationAsync(host, rotating, StagedFillerBase);

        // The premise: there really is something to keep out of a cache.
        await Assert.That(staged.SealsByFactor.Count).IsEqualTo(FactorCount);

        // Act
        HttpResponseMessage response = await rotating.Client.GetAsync(StatePath);
        HttpResponseMessage neighbour = await rotating.Client.GetAsync(NeighbourPath);

        // Assert — the status first on both, so a header missing because the request failed reads as the
        // failure it is.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(HeaderOf(response, "Cache-Control")).IsEqualTo("no-store");

        await Assert.That(neighbour.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(HeaderOf(neighbour, "Cache-Control")).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// After a completion, the read answers a <b>null</b> rotation — even though the staging row is still
    /// standing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THIS IS THE EPOCH RULE AND NOT AN EMPTY TABLE, WHICH IS THE WHOLE REASON THE CASE EXISTS.</b>
    /// <c>IKeyRotationRepository.PromoteAsync</c> deletes nothing — the application role holds no
    /// <c>DELETE</c> on either rotation table, and a tidy-up that ran a moment early would destroy the only
    /// copies of a generation the account has just been rewritten under — so a finished run leaves its row
    /// standing carrying the identifier the client is still quoting. <b>"A row exists" is therefore not "a
    /// run is in flight"</b>, and what separates them is the epoch: a live run's
    /// <c>staged_rotation_epoch</c> is above the generation the manifest holds, and a completed one's is
    /// equal to it. A route keyed on the row's existence alone <b>passes every other case in this
    /// file</b> and tells a client that has just finished a rotation it has one to resume — which sends it
    /// back through a whole re-encryption under a content key it no longer holds.
    /// </para>
    /// <para>
    /// <b>The completion is driven over its own route and asserted as arrangement.</b> Without the 204,
    /// this case is the "nothing staged" case wearing another name, and a route answering a null rotation
    /// to everybody would satisfy it perfectly. The staging row is then asserted still present, which is
    /// what makes the null answer a verdict about the epoch rather than about an empty table.
    /// </para>
    /// <para>
    /// <b>The account carries no narrative row, and that is not laziness.</b> The completeness gate's own
    /// correctness is <c>RotationCompletenessTests</c>'; here the account holds nothing to re-seal, so the
    /// gate answers complete without a chunk having run, which is what a presence-aware read is supposed to
    /// do and is not what is being measured.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationState_AfterACompletion_Answers200AndANullRotation()
    {
        // Arrange — twelve factors and a staged run, with nothing outstanding for the completeness gate
        // to refuse.
        await using PostgresTestHost host = await StartHostAsync();
        Rotating rotating = await FurnishAccountAsync(host, "google-resume-owner", StoredFillerBase);
        Staged staged = await StageRotationAsync(host, rotating, StagedFillerBase);

        // Act — the completion first, then the read this file is about.
        HttpResponseMessage completed = await rotating.Client.PostAsJsonAsync(
            CompletionPath, new { rotationId = staged.RotationId });

        // The arrangement, asserted: the run really finished, or this is the unstaged case under another
        // name.
        await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // And the staging row really is still standing, which is what makes the answer below the epoch
        // rule rather than an empty table.
        await Assert.That(await StagedRotationCountAsync(host, rotating.UserId)).IsEqualTo(1);
        await Assert.That(await StagedSealCountAsync(host, rotating.UserId)).IsEqualTo(FactorCount);

        HttpResponseMessage response = await rotating.Client.GetAsync(StatePath);

        // Assert — 405 named rather than 404, for the reason this class's remarks measure: on a path POST
        // already owns, an unmapped GET is answered 405 and 404 is unreachable.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body.ContainsKey("rotation")).IsTrue();
        await Assert.That(body["rotation"]).IsNull();
    }

    /// <summary>
    /// One account's staged generation reaches that account and nobody else — and the account asking is
    /// answered its own, in full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE QUIET FAILURE HERE IS <em>EMPTY</em>, NOT WRONG, AND THE CASE IS SHAPED AROUND THAT.</b>
    /// <c>key_rotations</c>, <c>key_rotation_seals</c> and <c>wrapped_account_keys</c> are all policed by
    /// <c>user_isolation</c>, so a read that lost its owner predicate answers <em>nothing</em> in
    /// production rather than answering somebody else's rows — and <c>IKeyRotationRepository</c> says in as
    /// many words that empty is the dangerous answer on every member it declares. So the subject's own
    /// answer is asserted <b>whole</b>: the right identifier, the right manifest and twelve seals carrying
    /// the right bytes. A case that only checked "the bystander's values are absent" would be satisfied by
    /// a route that answers every account a null rotation.
    /// </para>
    /// <para>
    /// <b>And the leak half is swept over the raw payload rather than over the members this file happens
    /// to name.</b> The seals are encapsulated to factors this server cannot open, so handing one account's
    /// ciphertext to another steals no plaintext — but it is still one account's key material crossing into
    /// another's browser, and it is the shape a client would act on: a resuming client that adopted a
    /// foreign seal set would re-encapsulate to factors its own account does not hold. The bystander's
    /// rotation identifier, its manifest, its twelve factor identifiers and its twelve staged values are
    /// each looked for in the text of the response, so a member added later that carried one of them is
    /// covered by a sweep that names no member.
    /// </para>
    /// <para>
    /// <b>Both accounts live on one host and both have a run staged</b>, which is what makes this a
    /// scoping claim: two accounts on two hosts could not share a leak, and a bystander with nothing staged
    /// would make "none of its values appear" true of a database that has none.
    /// </para>
    /// <para>
    /// <b>The two filler blocks are disjoint from the first account's.</b> With one block shared, a route
    /// answering the bystander's rows would hand back values byte-identical to the subject's, and every
    /// comparison below would pass on the leak it exists to catch.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationState_ForOneAccount_ReturnsNoneOfAnotherAccountsStagedGeneration()
    {
        // Arrange — one host, two whole accounts, each with twelve factors and a run of its own staged.
        await using PostgresTestHost host = await StartHostAsync();
        Rotating subject = await FurnishAccountAsync(host, "google-resume-subject", StoredFillerBase);
        Staged mine = await StageRotationAsync(host, subject, StagedFillerBase);

        Rotating bystander = await FurnishAccountAsync(
            host, "google-resume-bystander", BystanderStoredFillerBase);
        Staged theirs = await StageRotationAsync(host, bystander, BystanderStagedFillerBase);

        // The premises: two different accounts, two different runs, and not one value in common — without
        // which the sweep below cannot tell a leak from a coincidence.
        await Assert.That(subject.UserId).IsNotEqualTo(bystander.UserId);
        await Assert.That(mine.RotationId).IsNotEqualTo(theirs.RotationId);
        await Assert.That(mine.Manifest).IsNotEqualTo(theirs.Manifest);
        await Assert.That(Overlaps(mine.SealsByFactor, theirs.SealsByFactor)).IsEmpty();

        // Act
        HttpResponseMessage response = await subject.Client.GetAsync(StatePath);
        string payload = await response.Content.ReadAsStringAsync();

        // Assert — the subject's own answer, whole. This is the half that catches the read which lost its
        // owner predicate and answers empty, which two empty sets would otherwise make indistinguishable
        // from correct scoping.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject rotation = (await ReadJsonObjectAsync(response))["rotation"]!.AsObject();
        await Assert.That(rotation["rotationId"]!.GetValue<Guid>()).IsEqualTo(mine.RotationId);
        await Assert.That(rotation["stagedManifest"]!.GetValue<string>()).IsEqualTo(mine.Manifest);

        SortedDictionary<Guid, string> served = SealsIn(rotation);
        await Assert.That(served.Count).IsEqualTo(FactorCount);
        await Assert.That(Differences(mine.SealsByFactor, served)).IsEmpty();

        // And nothing of the other account's is anywhere in the body — swept over the text, so a member
        // added later that carried one of these is covered by a sweep that names no member.
        string[] foreignValues =
        [
            theirs.RotationId.ToString("D"),
            theirs.Manifest,
            .. theirs.SealsByFactor.Keys.Select(factorId => factorId.ToString("D")),
            .. theirs.SealsByFactor.Values,
        ];

        string[] leaked =
        [
            .. foreignValues
                .Where(value => payload.Contains(value, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal),
        ];

        await Assert.That(leaked).IsEmpty();
    }

    /// <summary>
    /// A session opened by the account's federated credential is refused this route with <b>403</b>, and
    /// it is the locked-session gate's 403 rather than the CSRF control's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The route must declare no <see cref="AllowsLockedSessionAttribute" />, which is the whole of what
    /// this case asks about.</b> <see cref="FullSessionRequirement" /> rides the fallback policy and the
    /// opted-out set is exactly <c>POST /api/me/session/revocation</c>. This read hands back the staged
    /// generation of an account's content key and index key; a session opened by the federated credential
    /// derives no key-encryption key at all, so a caller reaching it is one that can open none of what it
    /// is being handed.
    /// </para>
    /// <para>
    /// <b>THE FULL-SESSION ARM IS NOT A CONTROL, IT IS WHAT MAKES THIS CASE ABOUT A ROUTE AT ALL — and
    /// this route group has now been bitten three times.</b> <c>AuthorizationMiddleware</c> applies the
    /// fallback policy to a request that matched <b>no endpoint</b> as readily as to one that matched an
    /// unmarked one, so a request to a path this application does not serve is answered 403 to a locked
    /// session and 404 to everybody else — which is how <c>KeyRotationBeginEndpointTests</c>' locked case
    /// passed before the begin existed, <c>ResealChunkEndpointTests</c>' foreign-row case before the chunk
    /// did, and what <c>KeyRotationCompletionEndpointTests</c> was written around.
    /// </para>
    /// <para>
    /// <b>The arm's status is a 200 <em>carrying the <c>rotation</c> member</em>, and the member is what
    /// does the work.</b> On the three <c>POST</c> routes beside this one the accepting arm could be a 401
    /// or a 400 — statuses only a handler produces. This route answers 200 to every full session by
    /// design, and a 200 is not by itself proof a handler ran: what is, is a JSON object carrying a member
    /// this endpoint names, because an unmatched request is written a 404 with no content type, no length
    /// and no body at all. So the body is parsed and the member asked for.
    /// </para>
    /// <para>
    /// <b>A bare signed-in account rather than a staged one as that arm</b>, deliberately: it needs no
    /// factors, no ceremony and no seal set, and it already establishes everything this case needs — the
    /// route is mapped, a full session reaches it, and a handler wrote the answer. The populated answer is
    /// the first case's to own.
    /// </para>
    /// <para>
    /// <b>Both refusals on this path are 403</b>, which is the trap <c>LockedSessionTests</c> is written
    /// around: <see cref="FirstPartyRequestMiddleware" /> answers 403 to a request without the client
    /// header, before anything looks at a cookie. So the title is compared as well as the status — a 403
    /// carrying <see cref="FirstPartyRequestMiddleware.Title" /> would leave this case green against an
    /// application with no gate in it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationState_FromALockedSession_Answers403()
    {
        // Arrange — one host, two accounts, two sessions: one opened by a federated credential, which is
        // the only kind that derives SessionKind.Locked, and one opened by a passkey.
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient locked = await host.Factory.CreateSignedInClientAsync(
            "google-resume-locked", kind: SessionKind.Locked);
        ApiFactory.SignedInClient full = await host.Factory.CreateSignedInClientAsync(
            "google-resume-full", kind: SessionKind.Full);

        // Act — the same request twice, so the only thing differing between them is the session.
        HttpResponseMessage refused = await locked.Client.GetAsync(StatePath);
        HttpResponseMessage admitted = await full.Client.GetAsync(StatePath);

        // Assert — THE ADMITTED ARM FIRST, AND IT IS THE ONLY PART OF THIS CASE THAT IS ABOUT A ROUTE.
        // Measured at d5dc131, with no GET mapped: this same request answered 405 and the locked one
        // answered 403 titled Forbidden, so both assertions at the bottom of this method passed over an
        // application that served nothing here. 405 is named because it is what "no GET is mapped" looks
        // like on a path POST already owns, and 404 is NOT named because it is unreachable here — the
        // guard the three sibling files lead with would be vacuous on this route.
        await Assert.That(admitted.StatusCode).IsNotEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(admitted.StatusCode).IsNotEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The member a handler wrote, which is what separates this 200 from anything the pipeline could
        // have produced without one.
        await Assert.That((await ReadJsonObjectAsync(admitted)).ContainsKey("rotation")).IsTrue();

        // The refusal, and that it is this gate's refusal rather than the CSRF control's. Both lines are
        // satisfied by an unmapped route — see the arm above, which is what stops that mattering.
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await TitleOfAsync(refused)).IsNotEqualTo(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// An account owning a budget this request is not inside is answered <b>500</b>, and that status is
    /// the claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE REFUSAL THIS CASE EXISTS FOR WAS UNREACHABLE CODE UNTIL IT WAS WRITTEN — MEASURED: WITH
    /// THE SCOPE CHECK DELETED FROM <c>GetKeyRotationStateHandler</c> ENTIRELY, EVERY OTHER CASE IN THIS
    /// FILE STAYS GREEN.</b> It lived only in a comment and in
    /// <c>docs/business-logic/key-rotation.md</c>, which is exactly the state
    /// <c>RotationCompletenessReadService</c>'s equivalent was in before somebody wrote a test for it,
    /// and that one turned out to be hiding a real hole. <c>KeyRotationBeginEndpointTests</c>
    /// <c>.BeginKeyRotation_ForAnAccountOwningASecondBudget_Answers500</c> is the sibling and this case
    /// is deliberately its shape.
    /// </para>
    /// <para>
    /// <b>The read RECOMPUTES the inventory rather than reading it off the staged row, which is the whole
    /// of why the refusal is owed here.</b> Five of the six sets <c>CountNarrativeRowsAsync</c> walks
    /// carry the <c>BudgetIsolation</c> query filter and are scoped to the <em>ambient</em> budget, which
    /// takes no argument and cannot be re-pointed part-way through a request, while the budgets arm is
    /// scoped by owner. On an account owning a budget other than the ambient one the denominator would
    /// therefore describe part of an account — and a resuming client driven by it reaches 100% with rows
    /// still sealed under the old content key, whose very next act is the completion that destroys the
    /// last copies of that key. <b>Set equality in both directions, never <c>Count &gt; 1</c></b>: a count
    /// refuses owning a budget the request is not inside and admits being inside one the account does not
    /// own, where somebody else's rows are reported as this account's work left to do.
    /// </para>
    /// <para>
    /// <b><see cref="RotationScopeException" /> is deliberately unmapped and this case is part of what
    /// keeps it that way.</b> Its own remarks reject 404, 400 and 409 with reasons — the account exists,
    /// the request carries no field a caller could correct, and there is no conflicting state a client can
    /// resolve, because no endpoint creates, deletes or selects a budget — so the answer is a fault. The
    /// three are named below as statuses this refusal must never become, which is the sibling's habit: a
    /// <em>named</em> 5xx mapping is the seam a later reader softens into "resume the budget we can see and
    /// warn", and that is the truncation the refusal exists to prevent.
    /// </para>
    /// <para>
    /// <b>The title is read and the message is not, and both halves of that are decisions.</b> The title
    /// says the 500 came from an <em>unclaimed</em> exception reaching <c>GlobalExceptionHandler</c>'s
    /// catch-all rather than from a host that failed to boot or a pipeline that fell over before the
    /// endpoint — a broken seeder produces a 500 on its own, so the status alone is weak evidence. The
    /// message is left alone because it names counts and never identifiers <em>deliberately</em>, and the
    /// Development branch of that handler echoes it into the body: asserting the wording would turn a
    /// security constraint into a phrasing test, which is the one change that would make somebody "fix"
    /// the constraint to keep the test green. <c>KeyRotationBeginEndpointTests</c> and
    /// <c>RotationCompletenessTests</c> both say the same beside their own.
    /// </para>
    /// <para>
    /// <b>The run is staged and asserted staged, or this is the unstaged case wearing another name.</b>
    /// A route that answered every account <c>rotation: null</c> would satisfy a status assertion over an
    /// arrangement that quietly staged nothing, and the refusal sits <em>after</em> the two cheap null
    /// answers — so an account with nothing staged never reaches it at all. Twelve seals stand on the
    /// database and the epoch is the account's second, which is precisely the state the first case in this
    /// file has the route report in full.
    /// </para>
    /// <para>
    /// <b>The second budget is seeded rather than created through a route</b>, because no route creates
    /// one: registration writes exactly one budget and the product offers no second. That is also why the
    /// arrangement is asserted before the act — the seeded id really differs from the ambient one and the
    /// account really owns two — or a seeder that silently wrote nothing would leave this an ordinary
    /// happy path answering 200 and the case would go red for a reason that is not its own.
    /// </para>
    /// <para>
    /// <b>And the refusal hands back none of the run, swept over the raw payload.</b> A 500 whose body
    /// carried the staged manifest, the rotation identifier or one of the twelve staged values would be
    /// the partial answer this refusal exists to withhold, arriving under an error status — and the sweep
    /// names no member, so a body shape added later that carried one of them is covered.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationState_ForAnAccountOwningASecondBudget_Answers500()
    {
        // Arrange — twelve factors, a run staged over all twelve, and a second budget filed against the
        // same owner: a read that is correct in every other respect and whose denominator could only
        // describe part of the account.
        await using PostgresTestHost host = await StartHostAsync();
        Rotating rotating = await FurnishAccountAsync(host, "google-resume-two-budgets", StoredFillerBase);
        Staged staged = await StageRotationAsync(host, rotating, StagedFillerBase);
        Guid secondBudgetId = await RepositoryTestHost.SeedAdditionalBudgetOnAsync(
            host.ConnectionString, rotating.UserId, "Cabin");

        // The arrangement, asserted. Without the first two lines this is an ordinary read answering 200
        // for want of a second budget the seeder quietly failed to add; without the last three it is the
        // unstaged case under another name, because the refusal sits after the two cheap null answers and
        // an account with nothing in flight never reaches it.
        await Assert.That(secondBudgetId).IsNotEqualTo(rotating.BudgetId);
        await Assert.That(await OwnedBudgetCountAsync(host, rotating.UserId)).IsEqualTo(2);
        await Assert.That(await StagedRotationCountAsync(host, rotating.UserId)).IsEqualTo(1);
        await Assert.That(await StagedSealCountAsync(host, rotating.UserId)).IsEqualTo(FactorCount);
        await Assert.That(staged.SealsByFactor.Count).IsEqualTo(FactorCount);

        // Act
        HttpResponseMessage response = await rotating.Client.GetAsync(StatePath);
        string payload = await response.Content.ReadAsStringAsync();

        // Assert — the fault, and never a status a client could act on. The five beside it are
        // documentation rather than independent claims, the sibling's habit: 200 is what a handler with
        // the scope check deleted answers, 404, 400 and 409 are the three RotationScopeException's own
        // remarks reject by name, and 405 is what "no GET is mapped" looks like on a path POST already
        // owns — see this class's remarks — so its absence says the route really ran.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Conflict);

        // It reached the catch-all, which is what separates this 500 from a host that failed to boot or
        // a pipeline that fell over ahead of the endpoint. The message underneath is NOT read: it names
        // counts and never identifiers deliberately, and the Development branch of GlobalExceptionHandler
        // echoes it into the body, so pinning its wording would make a security constraint a phrasing test.
        //
        // Parsed from the text already in hand rather than through ReadJsonObjectAsync, which is what
        // DataExportRefusalTests does with its own refusal and is not a style choice here: that helper
        // reads the response stream, HttpClient hands back the SAME stream on every call once the content
        // is buffered, and a second read of it dies as a bare JsonReaderException naming no member.
        await Assert.That(response.Content.Headers.ContentType?.MediaType)
            .IsEqualTo("application/problem+json");

        JsonObject problem = JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidOperationException("The refusal answered an empty body.");
        await Assert.That(problem["title"]!.GetValue<string>()).IsEqualTo(CatchAllTitle);

        // And no rotation, whole or partial — absent or null, both of which say the run was withheld. A
        // refusal that answered it under an error status would satisfy every line above and be exactly
        // the half-answer this refusal exists to prevent.
        await Assert.That(problem["rotation"]).IsNull();

        string[] withheld =
        [
            staged.RotationId.ToString("D"),
            staged.Manifest,
            .. staged.SealsByFactor.Values,
        ];

        string[] served =
        [
            .. withheld
                .Where(value => payload.Contains(value, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal),
        ];

        await Assert.That(served).IsEmpty();
    }

    /// <summary>The route a completion is driven over, for the case that needs a finished run.</summary>
    private const string CompletionPath = "/api/me/key-rotation/completion";

    /// <summary>
    /// The title <c>GlobalExceptionHandler</c> writes for anything no earlier handler claims.
    /// </summary>
    /// <remarks>
    /// A literal, the way <c>DataExportRefusalTests</c> carries its own copy: a constant to read would
    /// first have to be added to that handler, and this file has no standing to ask for one.
    /// </remarks>
    private const string CatchAllTitle = "An unexpected error occurred.";

    /// <summary>
    /// One account as this file needs it: a signed-in client, and the twelve factors it holds.
    /// </summary>
    /// <remarks>
    /// <b>The factor identifiers are a list and the order is the order they were seeded in</b>, because
    /// that order is what <see cref="StageRotationAsync" /> assigns fillers along — so a failure names the
    /// factor whose seal it expected rather than reporting that two sets differ.
    /// </remarks>
    private sealed record Rotating(ApiFactory.SignedInClient SignedIn, IReadOnlyList<Guid> FactorIds)
    {
        public HttpClient Client => SignedIn.Client;

        public Guid UserId => SignedIn.UserId;

        public Guid BudgetId => SignedIn.BudgetId;
    }

    /// <summary>
    /// One staged run as this file arranged it: which run, the manifest it staged, and the value each
    /// factor's seal carries.
    /// </summary>
    /// <remarks>
    /// <b>The binary members are carried as base64url text rather than as <see cref="byte" /><c>[]</c>,
    /// and that is not presentation.</b> A record's generated equality over a byte array is reference
    /// equality, which would report two reads of the same bytes as different; and TUnit's
    /// <c>IsEquivalentTo</c> defaults to <c>CollectionOrdering.Any</c>, so a permuted payload would satisfy
    /// a collection comparison. One string equality can be satisfied neither way, and it is the alphabet the
    /// wire carries these values in anyway.
    /// </remarks>
    private sealed record Staged(
        Guid RotationId,
        string Manifest,
        SortedDictionary<Guid, string> SealsByFactor);

    /// <summary>
    /// The seals of one served rotation, keyed on the factor each names.
    /// </summary>
    /// <remarks>
    /// <b>Keyed rather than listed, because the claim is per factor.</b> A list comparison would be
    /// satisfied by the right twelve values against the wrong twelve factors — which is exactly what a
    /// handler zipping two unordered reads produces, and what leaves every row holding a value only some
    /// other factor's private key can open. Refuses a repeated factor rather than letting the last one win,
    /// because a response naming one factor twice is a seal set one factor short and a dictionary
    /// assignment would absorb it silently.
    /// </remarks>
    private static SortedDictionary<Guid, string> SealsIn(JsonObject rotation)
    {
        SortedDictionary<Guid, string> seals = [];

        foreach (JsonNode? seal in rotation["seals"]!.AsArray())
        {
            JsonObject entry = seal!.AsObject();
            Guid factorId = entry["factorId"]!.GetValue<Guid>();

            if (!seals.TryAdd(factorId, entry["encapsulatedAccountKeys"]!.GetValue<string>()))
            {
                throw new InvalidOperationException(
                    $"The served seal set names factor {factorId} more than once, so it carries fewer "
                    + "factors than entries and a keyed comparison would absorb the repeat.");
            }
        }

        return seals;
    }

    /// <summary>
    /// Every served seal that carries no value a client could use: the member absent, or explicitly
    /// <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Run before <see cref="SealsIn" /> rather than folded into it, because the two failures are
    /// different and the order decides which one a reader is told about.</b> That method reads
    /// <c>encapsulatedAccountKeys</c> as a string, so a JSON null there throws a bare
    /// <see cref="NullReferenceException" /> naming nothing at all — the least useful possible report of
    /// a route that padded its answer out to the live factor set with empty entries.
    /// </para>
    /// <para>
    /// <b>Absent and null are swept together and neither is tolerated.</b> They are one defect from the
    /// client's side: a seal it cannot decapsulate with, on a factor it is about to believe was sealed
    /// for. Nothing in this API omits a member to mean "no value" — the absent manifest on
    /// <c>GET /api/me/account-keys</c> is spelled <c>null</c> deliberately — so either spelling arriving
    /// here is a route inventing a row rather than leaving one out.
    /// </para>
    /// </remarks>
    private static string[] UnusableSealsIn(JsonObject rotation)
    {
        List<string> offenders = [];
        int ordinal = 0;

        foreach (JsonNode? seal in rotation["seals"]!.AsArray())
        {
            JsonObject entry = seal!.AsObject();
            string named = entry["factorId"] is { } factorId
                ? factorId.GetValue<Guid>().ToString("D")
                : $"entry {ordinal}, which names no factor";

            if (!entry.ContainsKey("encapsulatedAccountKeys"))
            {
                offenders.Add($"{named}: the seal carries no encapsulatedAccountKeys member at all");
            }
            else if (entry["encapsulatedAccountKeys"] is null)
            {
                offenders.Add($"{named}: encapsulatedAccountKeys is null, so this factor was named "
                              + "without a seal rather than left out");
            }

            ordinal++;
        }

        return [.. offenders];
    }

    /// <summary>
    /// Every way <paramref name="actual" /> departs from <paramref name="expected" />, as sentences a
    /// reader can line up against the arrangement.
    /// </summary>
    /// <remarks>
    /// <b>Offenders collected rather than asserted one at a time</b>, so a failure names every factor whose
    /// seal was wrong instead of the first — and asserted on as a <em>collection</em>, because a TUnit
    /// string assertion truncates and a census reporting through <c>string.Join</c> names only its first
    /// offender. Both directions, because a factor served that the account does not hold and a factor held
    /// that was not served are different failures.
    /// </remarks>
    private static string[] Differences(
        SortedDictionary<Guid, string> expected,
        SortedDictionary<Guid, string> actual)
    {
        List<string> offenders = [];

        foreach ((Guid factorId, string keys) in expected)
        {
            if (!actual.TryGetValue(factorId, out string? served))
            {
                offenders.Add($"{factorId}: no seal was served for this factor");
            }
            else if (!string.Equals(keys, served, StringComparison.Ordinal))
            {
                offenders.Add($"{factorId}: expected {keys}, served {served}");
            }
        }

        offenders.AddRange(actual.Keys
            .Where(factorId => !expected.ContainsKey(factorId))
            .Select(factorId => $"{factorId}: a factor this answer was not expected to name"));

        return [.. offenders];
    }

    /// <summary>
    /// Every factor for which two sets of encapsulated values carry the <b>same</b> bytes.
    /// </summary>
    /// <remarks>
    /// <b>The premise every "the staged value, not the live one" claim rests on.</b> If a factor's staged
    /// seal happened to equal the value already standing in its <c>wrapped_account_keys</c> row, then
    /// "handed back the staged generation" and "handed back the superseded one" would be the same string
    /// for that factor and the comparison would pass either way. Used in the other direction too, over two
    /// accounts, where a shared value would make a leak indistinguishable from correct scoping.
    /// </remarks>
    private static string[] Overlaps(
        SortedDictionary<Guid, string> left,
        SortedDictionary<Guid, string> right)
    {
        HashSet<string> rightValues = new(right.Values, StringComparer.Ordinal);

        return
        [
            .. left
                .Where(entry => rightValues.Contains(entry.Value))
                .Select(entry => $"{entry.Key}: {entry.Value}")
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Seeds a signed-in account and hangs twelve factors off it, under three credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two passkeys and a card of ten codes.</b> The recovery-codes credential is added here rather than
    /// through a seeder because ten factors hang off one row of <c>credentials</c> —
    /// <c>SeedPasskeyOnAsync</c> has no equivalent, and a set is the arrangement this file is about.
    /// </para>
    /// <para>
    /// <b>The session's own passkey is read back before the second one is filed.</b>
    /// <c>CreateSignedInClientAsync</c> writes a passkey to open a full session and hands back no credential
    /// id, so the lookup has to happen while the account still holds exactly one — a <c>Single</c>
    /// afterwards would match two rows and a <c>First</c> would pick one by coin flip.
    /// </para>
    /// <para>
    /// <b>The session is seeded rather than established through a ceremony</b>, because this read runs no
    /// assertion and needs none.
    /// </para>
    /// </remarks>
    private static async Task<Rotating> FurnishAccountAsync(
        PostgresTestHost host,
        string subject,
        byte storedFillerBase)
    {
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            subject, kind: SessionKind.Full);

        Guid sessionPasskeyId;
        Guid recoveryCodesId;

        await using (BudgetoidDbContext db = SuperuserDb(host))
        {
            sessionPasskeyId = (await db.Credentials.SingleAsync(credential =>
                credential.UserId == signedIn.UserId && credential.Type == CredentialType.Passkey)).Id;

            Credential recoveryCodes = Credential.CreateRecoveryCodes(signedIn.UserId, SeedInstant);
            db.Credentials.Add(recoveryCodes);
            await db.SaveChangesAsync();
            recoveryCodesId = recoveryCodes.Id;
        }

        Guid secondPasskeyId = await RepositoryTestHost.SeedPasskeyOnAsync(
            host.ConnectionString,
            signedIn.UserId,
            RandomNumberGenerator.GetBytes(WebAuthnCredentialIdLength));

        List<Guid> factorIds = [];
        await AddFactorAsync(host, sessionPasskeyId, factorIds, storedFillerBase);
        await AddFactorAsync(host, secondPasskeyId, factorIds, storedFillerBase);

        foreach (int _ in Enumerable.Range(0, RecoveryCodeSetSize))
        {
            await AddFactorAsync(host, recoveryCodesId, factorIds, storedFillerBase);
        }

        return new Rotating(signedIn, factorIds);
    }

    /// <summary>
    /// Files one more factor against <paramref name="credentialId" />, carrying a payload nothing else on
    /// this host carries.
    /// </summary>
    /// <remarks>
    /// The filler counts up with the factor's position, because the default seeding writes the same value
    /// on every row — so a read that returned row A's payload for row B would be invisible on a defaulted
    /// account and is visible here. <see cref="RepositoryTestHost.SeedWrappedAccountKeysAsync" /> says so in
    /// its own remarks.
    /// </remarks>
    private static async Task AddFactorAsync(
        PostgresTestHost host,
        Guid credentialId,
        List<Guid> factorIds,
        byte storedFillerBase)
    {
        Guid factorId = await RepositoryTestHost.SeedWrappedAccountKeysOnAsync(
            host.ConnectionString,
            credentialId,
            Guid.CreateVersion7(),
            encapsulatedAccountKeys: RepositoryTestHost.EncapsulatedAccountKeysPayload(
                (byte)(storedFillerBase + factorIds.Count)));

        factorIds.Add(factorId);
    }

    /// <summary>
    /// Files one more <c>wrapped_account_keys</c> row against the account's session passkey, <b>after</b>
    /// a run has been staged — so the account holds a factor the staged seal set does not name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Seeded rather than enrolled through <c>POST /api/passkeys/registration</c>, and the difference
    /// matters.</b> That route promotes the account's factor manifest to the next generation, which would
    /// put a second moving part between this arrangement and the assertion and would make the case able
    /// to fail for the manifest's reasons. What is wanted is exactly the row the seeder writes: a live
    /// factor with no <c>key_rotation_seals</c> row beside it.
    /// </para>
    /// <para>
    /// <b>Hung off the session's own passkey rather than a fresh credential</b>, because a factor is not
    /// a credential — <c>wrapped_account_keys</c> is keyed on <c>factor_id</c>, a set of recovery codes
    /// is ten factors under one credential, and nothing about this case needs a thirteenth credential to
    /// exist. Any passkey of the account will do; <c>First</c> rather than <c>Single</c> because the
    /// arrangement deliberately filed two.
    /// </para>
    /// <para>
    /// <b>No <c>key_rotation_seals</c> row is written and none could be</b>:
    /// <see cref="KeyRotationSeal.For" /> would take one, but a run that had sealed for this factor is
    /// precisely the state this case exists to be the opposite of.
    /// </para>
    /// </remarks>
    private static async Task<Guid> EnrolOneMoreFactorAsync(PostgresTestHost host, Guid userId)
    {
        Guid passkeyId;

        await using (BudgetoidDbContext db = SuperuserDb(host))
        {
            passkeyId = (await db.Credentials.FirstAsync(credential =>
                credential.UserId == userId && credential.Type == CredentialType.Passkey)).Id;
        }

        return await RepositoryTestHost.SeedWrappedAccountKeysOnAsync(
            host.ConnectionString,
            passkeyId,
            Guid.CreateVersion7(),
            encapsulatedAccountKeys: RepositoryTestHost.EncapsulatedAccountKeysPayload(LateFactorFiller));
    }

    /// <summary>
    /// Stages one rotation for <paramref name="rotating" />, with one seal for every factor the account
    /// holds, and returns what a resuming client has to be handed back.
    /// </summary>
    /// <remarks>
    /// <b>Through <see cref="KeyRotation.Begin" /> and the real adapter over the account's loaded rows</b>
    /// rather than an <c>insert</c>, the idiom <c>KeyRotationCompletionEndpointTests</c> keeps: the factory
    /// refuses an absent or over-wide manifest, an epoch below the floor, an empty identifier and a
    /// credential that is not a passkey, and <see cref="KeyRotationSeal.For" /> reads the owner off both the
    /// rotation and the loaded <c>wrapped_account_keys</c> row and refuses when they disagree — so a staged
    /// run here is one a begin could really have written.
    /// </remarks>
    private static async Task<Staged> StageRotationAsync(
        PostgresTestHost host,
        Rotating rotating,
        byte stagedFillerBase)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);
        KeyRotationRepository repository = new(db);

        Credential passkey = await db.Credentials.FirstAsync(credential =>
            credential.UserId == rotating.UserId && credential.Type == CredentialType.Passkey);
        IReadOnlyDictionary<Guid, WrappedAccountKeys> factors =
            await repository.ListFactorsAsync(rotating.UserId);

        byte[] manifest = ManifestFixture.Mint().Manifest;
        KeyRotation rotation = KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), manifest, StagedRotationEpoch, SeedInstant);

        List<KeyRotationSeal> seals = [];
        SortedDictionary<Guid, string> sealsByFactor = [];

        for (int index = 0; index < rotating.FactorIds.Count; index++)
        {
            Guid factorId = rotating.FactorIds[index];
            byte[] payload = RepositoryTestHost.EncapsulatedAccountKeysPayload(
                (byte)(stagedFillerBase + index));

            sealsByFactor[factorId] = Base64UrlText.Encode(payload);
            seals.Add(KeyRotationSeal.For(rotation, factors[factorId], payload));
        }

        await repository.StageAsync(rotation, seals);

        return new Staged(rotation.RotationId, Base64UrlText.Encode(manifest), sealsByFactor);
    }

    /// <summary>
    /// The encapsulated value each of the account's factors holds <b>live</b>, as the superseded generation
    /// standing in <c>wrapped_account_keys</c>.
    /// </summary>
    /// <remarks>
    /// Read on a context that wrote none of it, so every comparison built on it is a statement about
    /// PostgreSQL rather than about an identity map — and on the superuser connection, because
    /// <c>user_isolation</c> makes a policed read of a row that is there report exactly what an absent one
    /// reports.
    /// </remarks>
    private static async Task<SortedDictionary<Guid, string>> LiveFactorKeysAsync(
        PostgresTestHost host,
        Guid userId)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);

        SortedDictionary<Guid, string> factorKeys = [];

        foreach (WrappedAccountKeys keys in await db.WrappedAccountKeys
                     .Where(row => row.UserId == userId)
                     .ToListAsync())
        {
            factorKeys[keys.FactorId] = Base64UrlText.Encode(keys.EncapsulatedAccountKeys.ToArray());
        }

        return factorKeys;
    }

    /// <summary>
    /// Puts one row carrying a narrative value into five of the six counted tables, at five different
    /// counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written through the domain's own factories over the container superuser connection</b>, the way
    /// every other arrangement of narrative rows in this suite is: the product offers no route that creates
    /// a category group or a payee in bulk, and rows written by raw SQL would carry envelopes no production
    /// write path could have produced.
    /// </para>
    /// <para>
    /// <b>The two note-less transactions are the presence test.</b> They carry a narrative column that is
    /// the whole of their row's narrative and is absent, so they are rows a chunk never visits and a count
    /// must not include — a denominator that counted them is one a resuming client can never reach.
    /// </para>
    /// <para>
    /// Every label is distinct because each of these tables carries a blind-index uniqueness rule over one
    /// budget, and two rows sharing a label collide on it rather than seeding a second row.
    /// </para>
    /// </remarks>
    private static async Task FurnishBudgetAsync(PostgresTestHost host, Guid budgetId)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options,
            new TestBudgetContext(budgetId));

        Account account = Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Current"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            SeedInstant);
        db.Accounts.Add(account);

        foreach (string label in new[] { "Grocer", "Baker" })
        {
            db.Payees.Add(Payee.Create(
                Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed(label), SeedInstant));
        }

        List<CategoryGroup> groups = [];
        foreach ((string label, int position) in new[] { ("Everyday", 0), ("Bills", 1), ("Saving", 2) })
        {
            CategoryGroup group = CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed(label),
                SealedNarrative.Description($"{label} note"),
                position,
                SeedInstant);
            groups.Add(group);
            db.CategoryGroups.Add(group);
        }

        await db.SaveChangesAsync();

        // A save of its own: a category names its group by foreign key, so the group has to be on the
        // database before it can be filed under.
        foreach ((string label, int position) in new[]
                 {
                     ("Groceries", 0), ("Dining", 1), ("Electricity", 2), ("Holiday", 3),
                 })
        {
            db.Categories.Add(Category.Create(
                Guid.CreateVersion7(),
                budgetId,
                groups[position % groups.Count].Id,
                SealedNarrative.Indexed(label),
                SealedNarrative.Description($"{label} note"),
                position,
                SeedInstant));
        }

        // Five carrying a description and two carrying none. Transactions have no name and no unique index
        // but the key, so the labels repeat harmlessly and the dates only have to differ from nothing.
        for (int index = 0; index < 7; index++)
        {
            db.Transactions.Add(Transaction.Create(
                Guid.CreateVersion7(),
                budgetId,
                account.Id,
                -1m * (index + 1),
                UsdMinorUnit,
                new DateOnly(2026, 6, 1).AddDays(index),
                index < 5 ? SealedNarrative.Description($"Spend {index}") : (NarrativeField?)null,
                SeedInstant));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>How many staging rows the account holds.</summary>
    private static async Task<int> StagedRotationCountAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);

        return await db.KeyRotations.CountAsync(rotation => rotation.UserId == userId);
    }

    /// <summary>How many budgets the account owns.</summary>
    /// <remarks>
    /// On the superuser connection, because <c>budgets</c> is policed on the user by <c>user_isolation</c>
    /// and a policed read reports a row that is there exactly as it reports one that is not — so arranged
    /// on the app role, "the account owns two" could not be told from "the seeder wrote nothing". Keyed on
    /// the owner rather than counted whole, because a host is shared between accounts in other cases here.
    /// </remarks>
    private static async Task<int> OwnedBudgetCountAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);

        return await db.Budgets.CountAsync(budget => budget.UserId == userId);
    }

    /// <summary>How many staged seals the account holds — unchanged by a completion.</summary>
    private static async Task<int> StagedSealCountAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);

        return await db.KeyRotationSeals.CountAsync(seal => seal.UserId == userId);
    }

    /// <summary>
    /// A context on the container superuser, for arranging and for reading back.
    /// </summary>
    /// <remarks>
    /// No ambient budget, which is safe because nothing read through it carries the <c>BudgetIsolation</c>
    /// filter — every table this read touches is policed on the user instead.
    /// <see cref="FurnishBudgetAsync" /> builds a context of its own with a budget on it.
    /// </remarks>
    private static BudgetoidDbContext SuperuserDb(PostgresTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    /// <summary>
    /// A host serving requests under the application's own authentication, so a seeded session cookie is
    /// read by the handler that reads one in production.
    /// </summary>
    /// <remarks>
    /// No <c>repointsProviderSchemeToTestHandler</c>, because nothing here drives a registration ceremony —
    /// see this class's remarks for why a resumption suite stages its rotations rather than beginning them.
    /// </remarks>
    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();

        return host;
    }

    /// <summary>The response body as an object, so a member can be asked for by name.</summary>
    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();

    /// <summary>
    /// The <c>title</c> of a problem-details body — the only member that says which of two 403s on this
    /// path answered.
    /// </summary>
    private static async Task<string> TitleOfAsync(HttpResponseMessage response) =>
        (await ReadJsonObjectAsync(response))["title"]!.GetValue<string>();

    /// <summary>
    /// One header's value, wherever the framework filed it, or the empty string when there is none.
    /// </summary>
    /// <remarks>
    /// Both collections, because <c>Cache-Control</c> is a response header and a reader comparing only
    /// <see cref="HttpResponseMessage.Headers" /> would report the empty string for a route that really
    /// wrote one onto the content. The same shape <c>AccountKeysEndpointTests</c> reads its own with.
    /// </remarks>
    private static string HeaderOf(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? string.Join(", ", values)
            : response.Content.Headers.TryGetValues(name, out IEnumerable<string>? contentValues)
                ? string.Join(", ", contentValues)
                : string.Empty;
}
