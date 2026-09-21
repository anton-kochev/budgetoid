using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.KeyRotations.ResealRows;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Security;
using Domain.Sessions;
using Domain.Transactions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The chunk leg of a content-key rotation — <c>POST /api/me/key-rotation/chunks</c> — driven over
/// real HTTP against a real account, on the route's own terms rather than the handler's.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>ResealRowsHandler</c>, its port and its adapter all exist and are reachable by nothing.</b>
/// <c>ResealRowsHandler</c>'s own remarks say so in as many words — "no route reaches this handler
/// yet, so nothing a browser can do writes a <c>rotation_id</c>" — and this file is the commit that
/// makes the sentence false. Every case below is therefore red as a <b>404</b> until a route is
/// mapped, and every one of them turns into an assertion about behaviour the moment one is.
/// </para>
/// <para>
/// <b>Every test posts the wire shape rather than naming a request record</b>, the idiom
/// <c>KeyRotationBeginEndpointTests</c> keeps for the same reason: nothing here depends on a type the
/// endpoint has not been written with yet. It also means the <b>wire members are pinned by use</b> —
/// <c>rotationId</c>, and five arrays named <c>accounts</c>, <c>payees</c>, <c>categoryGroups</c>,
/// <c>categories</c> and <c>transactions</c>, whose entries carry <c>id</c>, <c>name</c>,
/// <c>nameKey</c> and <c>description</c> exactly as the five <c>Resealed*</c> records carry their
/// counterparts. A route spelling one of them differently binds it to <see langword="null" /> and the
/// happy path below finds a row that never moved.
/// </para>
/// <para>
/// <b><c>id</c> is a <see cref="Guid" /> and not base64url text, and that is the one place a chunk
/// parts company with the <em>create</em> bodies it otherwise resembles.</b> A create carries the row
/// identifier as text because the client minted it and it is the associated data the envelope beside
/// it was sealed against — a spelling this server cannot reproduce is a value that never opens again.
/// A chunk names a row that already exists and whose identifier this server rendered, so it matches
/// the <b>update</b> paths: the identifier selects a row and nothing is sealed against what this body
/// says about it.
/// </para>
/// <para>
/// <b>NOTHING HERE RESTATES WHAT <c>ResealRowsHandlerTests</c> AND <c>ResealChunkTests</c> ALREADY
/// HOLD, AND THE OMISSIONS ARE DELIBERATE.</b> The presence rule's two arms, "resolve every arm before
/// mutating any", "drive each arm from the command rather than from what the port answered", "the save
/// is inside the unit of work" and the rollback of an abandoned unit of work are all held one or two
/// tiers down, by instruments that can see the side of a delegate a write landed on. Repeating any of
/// them over HTTP would buy a slower copy of a sharper test. What is <em>only</em> visible from here is
/// the route: that it exists, that it is behind the full-session gate, what status each refusal reaches
/// a client as, and that a 204 really moved the columns.
/// </para>
/// <para>
/// <b>A ROUTE THAT WAS NEVER MAPPED ANSWERS TWO OF THE STATUSES THIS FILE ASSERTS, AND THE NEXT ROUTE
/// ADDED UNDER <c>/api/me/key-rotation</c> WILL MEET THE SAME TRAP. READ THIS BEFORE WRITING ITS
/// TESTS.</b> A request matching <b>no endpoint</b> is still seen by
/// <see cref="Microsoft.AspNetCore.Authorization.AuthorizationMiddleware" />, which applies the fallback
/// policy to it as readily as to one that matched an unmarked endpoint. So an unserved path answers
/// <b>403</b> to a locked session and <b>404</b> to everybody else — and this story has now been bitten
/// by both faces of that: <c>KeyRotationBeginEndpointTests</c>' locked-session case passed before the
/// begin route existed, and this file's foreign-row case passed before this one did, measured on its
/// first run as the single green among five reds.
/// <list type="bullet">
/// <item><description><b>A case expecting 403</b> needs an accepting arm on the same path whose status
/// can only come from a handler that ran — the full-session request in
/// <see cref="ResealChunk_FromALockedSession_Answers403" />.</description></item>
/// <item><description><b>A case expecting 404</b> needs the response <em>body</em>. A request that
/// matched nothing reaches the end of the middleware pipeline, which writes a 404 with <b>no body at
/// all</b> — measured: no content type, no length. <c>NotFoundExceptionHandler</c> writes problem
/// details. That difference is the discriminator, and it costs no pinned wording.</description></item>
/// </list>
/// The general rule the two cases share: <b>404 means no endpoint, 403 means a policy refused, and only
/// a status a handler produced proves the route exists.</b>
/// </para>
/// <para>
/// <b>The route must not re-enforce <c>MaxChunkBytes</c>, and no case here sends an over-sized body on
/// purpose.</b> A second ceiling at 32 KB would refuse bodies that are legal under the 64 KB Kestrel
/// cap and would split one condition across a 400 and a 413. Enforcement stays Kestrel's, and a case
/// asserting a 413 here would be a case demanding the second ceiling.
/// </para>
/// <para>
/// <b>Rotations are staged through <see cref="KeyRotation.Begin" /> on the container superuser
/// connection rather than through <c>POST /api/me/key-rotation</c>.</b> The begin is gated by a fresh
/// WebAuthn assertion, which costs a registration ceremony, a synthetic authenticator and a
/// re-authentication nonce per test — and a chunk reads nothing the begin writes except the identifier
/// on <c>key_rotations</c>. Driving it here would make every case below able to fail for the begin's
/// reasons, which <c>KeyRotationBeginEndpointTests</c> already owns.
/// </para>
/// <para>
/// <b>Every arrangement and every read-back runs on <see cref="PostgresTestHost.ConnectionString" /></b>
/// — the container superuser — and only the act runs as the application. Half of what is asserted is
/// that a column did <em>not</em> move, and a policed connection reports a row it cannot see exactly as
/// it reports one that did not change.
/// </para>
/// </remarks>
public sealed class ResealChunkEndpointTests
{
    private const string ChunkPath = "/api/me/key-rotation/chunks";

    /// <summary>Minor unit of the USD rows seeded here; precision is what no case is about.</summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// The generation every staged manifest here is filed at. Above the floor, so the row is one a
    /// begin could really have written.
    /// </summary>
    private const int StagedRotationEpoch = 4;

    /// <summary>
    /// Fixed UTC instant for every seeded row. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing rather than
    /// decoration.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The five tables a chunk rewrites, in the order <c>docs/business-logic/key-rotation.md</c> lists
    /// them — <b>minus <c>budgets</c></b>.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from the model, following <c>ResealChunkTests</c> and
    /// <c>RotationCompletenessTests</c>: a list built from the mapping agrees with whatever the mapping
    /// later decides, and the point of this one is to be the independent statement of how many arms
    /// there are. The sixth arm is refused rather than forgotten — <c>Budget.ResealName</c> is
    /// <see langword="internal" /> and <c>budgets</c> is granted <c>UPDATE</c> on <c>name</c> and
    /// nothing else.
    /// </remarks>
    private static readonly string[] ResealedTables =
        ["accounts", "payees", "category_groups", "categories", "transactions"];

    /// <summary>
    /// How many rows a whole chunk names here: five arms, <b>two of which carry two rows</b>.
    /// </summary>
    /// <remarks>
    /// <b>THE SECOND ROW ON TWO ARMS IS NOT PADDING, AND ONE ROW PER ARM IS THE WEAKEST BATCH A CHUNK
    /// CAN BE.</b> With a single entry everywhere, a mapping that took <c>FirstOrDefault()</c> or
    /// <c>Take(1)</c> of each array — the shape a reader writes while converting a list to a scalar —
    /// satisfies every case in this file, and turns a real rotation into dozens of one-row writes whose
    /// completeness gate never converges. The defect is in the mapping's shape rather than in any one
    /// arm, so two arms carry two rows and the rest carry one. <c>payees</c> and <c>transactions</c> are
    /// the two chosen because they are where a real account holds thousands.
    /// </remarks>
    private const int ResealedRowCount = 7;

    // The labels the fixture seeds with, and the labels a chunk rotates to. Every one of the fourteen is
    // distinct, which is what makes a column written from the wrong arm — or a name written into the
    // description, or a second row written with the first row's value — visible rather than a comparison
    // of two identical envelopes. SealedNarrative is deterministic in its label, so "what should be
    // there afterwards" is a value this file can state exactly rather than merely assert has changed.
    private const string SeededAccountLabel = "chunk checking";
    private const string SeededPayeeLabel = "chunk grocer";
    private const string SeededSecondPayeeLabel = "chunk baker";
    private const string SeededGroupLabel = "chunk everyday";
    private const string SeededGroupNote = "chunk group note";
    private const string SeededCategoryLabel = "chunk groceries";
    private const string SeededCategoryNote = "chunk category note";
    private const string SeededTransactionNote = "chunk transaction note";
    private const string SeededSecondTransactionNote = "chunk hardware note";

    private const string RotatedAccountLabel = "rotated checking";
    private const string RotatedPayeeLabel = "rotated grocer";
    private const string RotatedSecondPayeeLabel = "rotated baker";
    private const string RotatedGroupLabel = "rotated everyday";
    private const string RotatedGroupNote = "rotated group note";
    private const string RotatedCategoryLabel = "rotated groceries";
    private const string RotatedCategoryNote = "rotated category note";
    private const string RotatedTransactionNote = "rotated transaction note";
    private const string RotatedSecondTransactionNote = "rotated hardware note";

    /// <summary>
    /// <b>The riskiest case.</b> A chunk naming a row of another budget is answered <b>404</b>, and not
    /// one row of the caller's own budget is rewritten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The five entities carry the <c>BudgetIsolation</c> query filter, so a foreign row is
    /// <em>invisible</em> rather than forbidden</b> — the port's read simply does not answer for that
    /// identifier. 404 is therefore the honest status and 403 would be a lie about what the server
    /// knows: answering "forbidden" would confirm to a caller reaching into another budget that the
    /// identifier it guessed names a real row, which is the enumeration this API refuses to be
    /// everywhere else.
    /// </para>
    /// <para>
    /// <b>The chunk names this account's own rows beside the stranger's, and that is the half a status
    /// assertion cannot see.</b> A route that re-sealed the four legible arms and refused on the fifth
    /// would answer the same 404 while leaving this account's rows carrying a stamp the run never
    /// earned — and the stamp is the one signal the destructive completion step trusts. The whole
    /// snapshot is compared, so a failure names every arm that moved rather than only the first.
    /// </para>
    /// <para>
    /// <b>The stranger's row is checked too, which is a different claim from the one above.</b> That one
    /// is about the caller's budget; this one is about the budget the chunk reached into, where a route
    /// running without the ambient-budget filter would have written a value the owner never authorised.
    /// </para>
    /// <para>
    /// <b>The foreign payee belongs to a second account rather than to a second budget of this one.</b>
    /// An account owning two budgets is refused a rotation outright — <c>RotationScopeException</c>, at
    /// the begin and again at the completeness gate — so arranging one here would be arranging a state
    /// no rotation reaches, and the refusal under test could be read as that one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WhenARowBelongsToAnotherBudget_Answers404AndRewritesNothing()
    {
        // Arrange — two accounts, each with its own budget, and a chunk naming both of this account's
        // payees and one of the stranger's.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Furnished mine = await FurnishAccountAsync(host, "google-chunk-owner");
        Furnished theirs = await FurnishAccountAsync(host, "google-chunk-stranger");
        Guid rotationId = await StageRotationAsync(host, mine);
        IReadOnlyDictionary<string, RowState> before = await SnapshotAsync(admin, mine);

        // The stranger's row is LAST, after both of this account's own, so a route that resolved and
        // mutated arm by arm would have rewritten two legible rows before reaching the one it cannot see.
        object chunk = ChunkBodyFor(rotationId, mine, payees:
        [
            .. OwnPayeeEntries(mine),
            PayeeEntry(theirs.PayeeId, "stranger rotated"),
        ]);

        // The premise, or this is the happy path wearing another name: the two budgets really differ,
        // and the stranger's row really is in the chunk.
        await Assert.That(theirs.BudgetId).IsNotEqualTo(mine.BudgetId);
        await Assert.That(await BudgetOfAsync(admin, "payees", theirs.PayeeId)).IsEqualTo(theirs.BudgetId);

        // Act
        HttpResponseMessage response = await mine.Client.PostAsJsonAsync(ChunkPath, chunk);

        // Assert — 404 rather than the two statuses a reader reaches for. 403 would disclose that the
        // guessed identifier names a real row; 500 would be a fault for a request that is merely wrong.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // A 404 FROM A HANDLER THAT RAN, AND NOT THE ONE AN UNMAPPED PATH ANSWERS. This is the one case
        // in the file whose expected status is also the status of a route that does not exist — measured
        // on this file's first run, where it was the single green among five reds. A request matching no
        // endpoint reaches the end of the middleware pipeline, which writes 404 with NO BODY AT ALL;
        // NotFoundExceptionHandler writes problem details. The body is the only discriminator, and
        // without it this case would go on passing over an application in which nothing was ever mapped.
        // The wording is deliberately not pinned — Detail is NotFoundException's own message, copied
        // verbatim into a Production response, and asserting it here would make that a phrasing test.
        string refusal = await response.Content.ReadAsStringAsync();
        await Assert.That(refusal).IsNotEqualTo(string.Empty);

        JsonObject problem = JsonNode.Parse(refusal)!.AsObject();
        await Assert.That(problem.ContainsKey("title")).IsTrue();
        await Assert.That(problem.ContainsKey("detail")).IsTrue();

        // And this account's own rows are exactly what they were — no arm was rewritten on the way to
        // the one that could not be read.
        IReadOnlyDictionary<string, RowState> after = await SnapshotAsync(admin, mine);
        await Assert.That(after
                .Where(row => !row.Value.Equals(before[row.Key]))
                .Select(row => row.Key)
                .ToArray())
            .IsEmpty();

        // And the stranger's row is untouched, which is the claim about the budget that was reached into.
        // Compared as base64url text rather than as a byte collection: TUnit's IsEquivalentTo defaults
        // to CollectionOrdering.Any, so a re-sealed index whose bytes happened to be a permutation of
        // the seeded one would satisfy it. One string equality cannot be satisfied that way, and a
        // failure prints two values a reader can line up.
        await Assert.That(await StampOfAsync(admin, "payees", theirs.PayeeId)).IsNull();
        await Assert.That(Base64UrlText.Encode(await NameKeyOfAsync(admin, "payees", theirs.PayeeId)))
            .IsEqualTo(SealedNarrative.EncodedIndex(SeededPayeeLabel));
    }

    /// <summary>
    /// A chunk quoting a rotation that is not the account's staged one is answered <b>400</b> and
    /// rewrites nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>400 is the status <c>ResealRowsHandler</c> produces today, read off the handler rather than
    /// assumed.</b> It raises <c>Domain.Common.ValidationException</c> keyed on
    /// <c>nameof(ResealRowsCommand.RotationId)</c>, and <c>ValidationExceptionHandler</c> turns exactly
    /// that type into a 400 carrying a <c>ValidationProblemDetails</c>. It is the right status: the
    /// request is correctable by the caller, and the correction is "begin a run and quote what the begin
    /// staged".
    /// </para>
    /// <para>
    /// <b>409 was the tempting alternative and would be wrong here.</b> A conflict says the server holds
    /// a state the caller has to reconcile with; the handler deliberately collapses "no run is in
    /// flight" and "a different run is" into one refusal, precisely so that a caller whose request was
    /// already wrong is told nothing about which of the two it was. A 409 would have to distinguish them
    /// to mean anything.
    /// </para>
    /// <para>
    /// <b>The error key is asserted, and it is doing the same job the full-session arm does next door.</b>
    /// A framework 400 out of the model binder is also a 400, and so is a route-level refusal keyed on a
    /// wire member. Only <c>RotationId</c> — PascalCase, the command's own member name, because nothing
    /// configures a <c>DictionaryKeyPolicy</c> and <c>ValidationExceptionHandler</c> copies the
    /// dictionary verbatim — says the refusal came from a handler that <em>ran</em>.
    /// </para>
    /// <para>
    /// <b>The chunk is a correct client's in every other respect.</b> Every row it names exists, belongs
    /// to this budget and holds a note where the entry supplies one, so the refusal can be about the
    /// quoted run and nothing else.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_ForARotationThatIsNotTheStagedOne_Answers400AndRewritesNothing()
    {
        // Arrange — one run staged, and a chunk quoting another.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Furnished furnished = await FurnishAccountAsync(host, "google-chunk-owner");
        Guid staged = await StageRotationAsync(host, furnished);
        Guid abandoned = Guid.CreateVersion7();
        IReadOnlyDictionary<string, RowState> before = await SnapshotAsync(admin, furnished);

        // The premise: the two identifiers really differ, and the account really holds one of them.
        await Assert.That(abandoned).IsNotEqualTo(staged);
        await Assert.That(await StagedRotationIdAsync(admin, furnished.UserId)).IsEqualTo(staged);

        // Act
        HttpResponseMessage response = await furnished.Client.PostAsJsonAsync(
            ChunkPath, ChunkBodyFor(abandoned, furnished));

        // Assert — refused rather than faulted, and refused rather than unreadable.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // Keyed on the member the caller can correct, which is what says the handler ran.
        JsonObject errors = (await ReadJsonObjectAsync(response))["errors"]!.AsObject();
        await Assert.That(errors.ContainsKey(nameof(ResealRowsCommand.RotationId))).IsTrue();

        // And not one arm moved.
        IReadOnlyDictionary<string, RowState> after = await SnapshotAsync(admin, furnished);
        await Assert.That(after
                .Where(row => !row.Value.Equals(before[row.Key]))
                .Select(row => row.Key)
                .ToArray())
            .IsEmpty();
    }

    // ==================================================================================
    // THE TWO BELOW ARE ONE FAMILY: A CORRECT CALLER, A STAGED RUN, AND ONE MEMBER THIS ROUTE MUST
    // REFUSE RATHER THAN DECODE OR FAULT ON.
    //
    // Both turn on the same absent thing. NarrativeField.Sealed and IndexedName.Of both throw
    // ArgumentException — read, not assumed: NarrativeField's own remarks say the Try shape "belongs
    // at the wire edge", which is Application/Security/CiphertextEnvelopeText.TryDecode, and nothing
    // in Api maps ArgumentException, so it reaches GlobalExceptionHandler as an unexpected error. A
    // route that decoded with Base64UrlText.Decode and built the values directly therefore answers
    // 500 to a malformed member, which is a FAULT for a request that is merely wrong.
    //
    // 500 IS THEREFORE NAMED SEPARATELY IN BOTH, before the equality: a bare check against 400 would
    // report the very answer these cases exist to refuse as "not 400".
    //
    // WHAT NEITHER SAYS, STATED RATHER THAN IMPLIED: each asserts that the request was REFUSED rather
    // than stored, and asserts NOTHING about which rule refused it. No message and no error key is
    // pinned — a route that refused these chunks for some unrelated reason passes both. What keeps
    // them from being vacuous is that every other member is a correct client's: the run is the staged
    // one, every row exists in this budget, and every note is present where the row holds one. The two
    // fixtures differ in exactly one member from the body the happy path sends and gets a 204 for.
    // ==================================================================================

    /// <summary>
    /// A <c>nameKey</c> spelled in <b>padded standard base64</b> is refused, not decoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE BLIND INDEX IS THE MEMBER TO GET WRONG, AND BOTH REASONS MATTER.</b>
    /// </para>
    /// <para>
    /// <b>One — it is the member no fixture can accidentally make blind.</b> Unpadded base64url and
    /// padded standard base64 spell a payload identically when its length is a multiple of three and
    /// none of its six-bit groups lands on 62 or 63 — <c>SealedNarrative</c> writes that out at length,
    /// and names eleven labels in this suite for which its own <c>EncodedName</c> is blind. A blind
    /// index is <b>exactly 32 bytes</b>, 32 is not a multiple of 3, so the two spellings differ for
    /// <em>every</em> label, for ever. A case built on a name envelope would be green under either
    /// decoder whenever somebody picked an unlucky label; this one cannot be. The Arrange asserts it
    /// anyway, because a property nobody checks is a property that stops holding quietly.
    /// </para>
    /// <para>
    /// <b>Two — it is the member with the least behind it.</b> A narrative envelope is judged again by
    /// <c>NarrativeField.Sealed</c>, which has a version byte and a length band to catch a decode that
    /// went wrong. A blind index is judged on <b>width alone</b> — <c>IndexedName.Of</c> compares
    /// against <c>BlindIndexLength</c> and nothing else — so a lenient decoder handing over the right
    /// 32 bytes is refused by no rule anywhere in the product. There is no second line of defence to
    /// make this case pass for somebody else's reason.
    /// </para>
    /// <para>
    /// <b>Three implementations are separated by one request.</b> A strict decoder behind a
    /// <c>Try</c> shape answers 400; a lenient one accepts the value and answers 204; a strict one with
    /// no <c>Try</c> in front of it throws out of the endpoint and answers 500. All three are named.
    /// </para>
    /// <para>
    /// <b>The envelope beside it stays in the right alphabet</b>, so the entry differs from a correct
    /// one in exactly one member — and the second payee in the same arm is correct throughout, which is
    /// what makes "nothing was rewritten" a claim about a refusal that preceded the write rather than
    /// about a chunk with one entry in it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WithANameKeyInPaddedStandardBase64_Answers400AndRewritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Furnished furnished = await FurnishAccountAsync(host, "google-chunk-owner");
        Guid rotationId = await StageRotationAsync(host, furnished);
        IReadOnlyDictionary<string, RowState> before = await SnapshotAsync(admin, furnished);

        string padded = Convert.ToBase64String(
            SealedNarrative.BlindIndex(RotatedPayeeLabel).Span);

        // The two spellings really are different for this value — see the remarks for why they are
        // different for every blind index and only for some envelopes.
        await Assert.That(padded).IsNotEqualTo(SealedNarrative.EncodedIndex(RotatedPayeeLabel));

        // Act — one member in the wrong alphabet, and everything else about the request correct.
        HttpResponseMessage response = await furnished.Client.PostAsJsonAsync(
            ChunkPath,
            ChunkBodyFor(rotationId, furnished, payees:
            [
                PayeeEntryWithNameKey(furnished.PayeeId, RotatedPayeeLabel, padded),
                PayeeEntry(furnished.SecondPayeeId, RotatedSecondPayeeLabel),
            ]));

        // Assert — refused rather than faulted, and refused rather than decoded.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NoContent);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // And not one row moved — including the six the chunk named correctly.
        IReadOnlyDictionary<string, RowState> after = await SnapshotAsync(admin, furnished);
        await Assert.That(after
                .Where(row => !row.Value.Equals(before[row.Key]))
                .Select(row => row.Key)
                .ToArray())
            .IsEmpty();
    }

    /// <summary>
    /// A narrative envelope one byte <b>below the AEAD framing's floor</b> is refused — and refused as a
    /// 400 rather than escaping as a 500.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THIS IS THE HALF THE ALPHABET CASE CANNOT REACH.</b> That one sends text no decoder should
    /// accept; this one sends text every decoder accepts, carrying bytes no <em>envelope</em> rule
    /// should. They fail in different places — the first at <c>PasskeyEncoding.TryDecode</c>, this one
    /// at <c>CiphertextEnvelope.IsWellFormed</c> — and a route can hold one and not the other, because
    /// a lenient alphabet and a missing framing check are two separate omissions.
    /// </para>
    /// <para>
    /// <b>The 500 is the specific wrong answer here.</b> <c>NarrativeField.Sealed</c> refuses this value
    /// with <c>ArgumentException</c>, which its own remarks argue for — the type is shared by eight
    /// columns and owns none of them, so a <c>ValidationException</c> keyed on a property name would
    /// name a member no request has. That is correct <em>for the Domain</em> and it means the refusal
    /// only becomes a 400 if the route runs <c>CiphertextEnvelopeText.TryDecode</c> first. Nothing in
    /// <c>Api</c> maps <c>ArgumentException</c>, so a route that skipped the <c>Try</c> faults.
    /// </para>
    /// <para>
    /// <b>The width is chosen so that only one rule in the product can answer.</b> It is not empty, so
    /// the nullable column's presence rule cannot fire and <c>SealedOrAbsent</c> reads it as a value
    /// that was supplied; it is far under <c>NarrativeFieldLimits.DescriptionBytes</c>, so no ceiling
    /// can; and it leads with <c>CiphertextEnvelope.Version</c>, so no version test can. All three are
    /// asserted in the Arrange rather than assumed. What is left is
    /// <c>CiphertextEnvelope.MinimumLength</c> — a version, a nonce and a tag with no ciphertext
    /// between them.
    /// </para>
    /// <para>
    /// <b><c>transactions.description</c> is the arm, because it is the one whose whole narrative is a
    /// single nullable column.</b> There is no name and no blind index beside it to be refused first,
    /// so the framing of this one value is the only thing the route can be answering about — and it is
    /// also the arm a real account holds thousands of rows in, where a 500 on one malformed note would
    /// strand a rotation mid-run.
    /// </para>
    /// <para>
    /// <b>The value cannot come from <c>SealedNarrative</c> and that is deliberate</b>: that helper
    /// caps and frames by construction, and its own remarks say a test needing an envelope outside the
    /// band has to build it inline, in the open, where a reviewer sees it. The filler is deterministic
    /// so a failure is reproducible.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WithADescriptionBelowTheFramingFloor_Answers400AndRewritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Furnished furnished = await FurnishAccountAsync(host, "google-chunk-owner");
        Guid rotationId = await StageRotationAsync(host, furnished);
        IReadOnlyDictionary<string, RowState> before = await SnapshotAsync(admin, furnished);

        byte[] tooNarrow = EnvelopeOf(CiphertextEnvelope.MinimumLength - 1);

        // The value clears every OTHER rule in the product, so a refusal can only be the framing's
        // floor: present rather than absent, far under the column's cap, and correctly versioned.
        await Assert.That(tooNarrow.Length).IsGreaterThan(0);
        await Assert.That(tooNarrow.Length).IsLessThan(NarrativeFieldLimits.DescriptionBytes);
        await Assert.That(tooNarrow[0]).IsEqualTo(CiphertextEnvelope.Version);

        // Act — one member below the floor, in the right alphabet, and everything else correct.
        HttpResponseMessage response = await furnished.Client.PostAsJsonAsync(
            ChunkPath,
            ChunkBodyFor(rotationId, furnished, transactions:
            [
                TransactionEntry(furnished.TransactionId, Base64UrlText.Encode(tooNarrow)),
                TransactionEntry(
                    furnished.SecondTransactionId,
                    SealedNarrative.EncodedDescription(RotatedSecondTransactionNote)),
            ]));

        // Assert — a refusal and never a fault. 500 first, because it is the specific wrong answer.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NoContent);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // And not one row moved.
        IReadOnlyDictionary<string, RowState> after = await SnapshotAsync(admin, furnished);
        await Assert.That(after
                .Where(row => !row.Value.Equals(before[row.Key]))
                .Select(row => row.Key)
                .ToArray())
            .IsEmpty();
    }

    /// <summary>
    /// <paramref name="length" /> bytes leading with <see cref="CiphertextEnvelope.Version" />, filled
    /// deterministically.
    /// </summary>
    /// <remarks>
    /// <b>Built here rather than through <c>SealedNarrative</c>, which caps and frames by
    /// construction</b> — that helper would refuse the one value the case needing it is about, which its
    /// own remarks say in as many words. The filler varies by position so a buffer read into the wrong
    /// column is visible rather than a run of one repeated byte that matches anything, and it is
    /// deterministic so a failure reproduces.
    /// </remarks>
    private static byte[] EnvelopeOf(int length)
    {
        byte[] envelope = new byte[length];

        for (int position = 1; position < envelope.Length; position++)
        {
            envelope[position] = (byte)(position * 31);
        }

        // Written last, so a filler loop that walked from zero could not overwrite it.
        envelope[0] = CiphertextEnvelope.Version;

        return envelope;
    }

    /// <summary>
    /// A chunk re-sent after a timeout succeeds — the same identifier, the same envelopes, a second
    /// <b>204</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>key-rotation.md</c> states this as a MUST NOT and it is the one that reads like a bug:</b>
    /// "a reseal must not refuse a rotation identifier it has already been given". A client whose
    /// request timed out cannot know whether the chunk landed, so it re-sends the same body — and a
    /// route that refused a repeat would look like idempotence protection while breaking retries on
    /// exactly the long rotations that need chunking. An account of ten thousand transactions is dozens
    /// of chunks, so the probability of at least one retry is not small.
    /// </para>
    /// <para>
    /// <b>The second send is byte-identical to the first</b>, which is what a retry really posts.
    /// Rebuilding the chunk with fresh envelopes would be a different request and would measure
    /// something else — and there is nothing to protect against, because re-applying the client's own
    /// values to the same rows converges.
    /// </para>
    /// <para>
    /// <b>The first send is asserted as arrangement.</b> Without that, a route answering 204 to
    /// everything — including a first send that wrote nothing — satisfies this case perfectly.
    /// </para>
    /// <para>
    /// <b>What is read back afterwards is the whole snapshot at the rotated values</b>, so a second
    /// application that cleared a column, doubled a stamp into the wrong row or wrote a second
    /// generation's bytes is visible. A status pair alone would pass over a route whose retry silently
    /// undid the first send.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_ResentAfterATimeout_Answers204Again()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Furnished furnished = await FurnishAccountAsync(host, "google-chunk-owner");
        Guid rotationId = await StageRotationAsync(host, furnished);
        object chunk = ChunkBodyFor(rotationId, furnished);

        // Act — the send, then the very same body again.
        HttpResponseMessage first = await furnished.Client.PostAsJsonAsync(ChunkPath, chunk);
        HttpResponseMessage retry = await furnished.Client.PostAsJsonAsync(ChunkPath, chunk);

        // The arrangement, asserted: the first send really landed, or the retry is the only write and
        // this case is the happy path under another name.
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Assert — the retry is accepted rather than refused as a repeat.
        await Assert.That(retry.StatusCode).IsNotEqualTo(HttpStatusCode.Conflict);
        await Assert.That(retry.StatusCode).IsNotEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // And the rows hold exactly what the chunk sent, once — not a column cleared by the second
        // application and not a stamp that moved.
        await AssertRotatedAsync(admin, furnished, rotationId);
    }

    /// <summary>
    /// The happy path: every row the chunk names comes back carrying the new envelope, the recomputed
    /// blind index and the run's identifier, and the response is <b>204</b> with an empty body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>204 and never 200, asserted both ways.</b> A chunk is all-or-nothing in one save, so there is
    /// no partial-accept count to report — and a "rows remaining" member would be a second denominator,
    /// able to disagree with the one the begin already published in its inventory. The body is read and
    /// asserted empty rather than merely ignored, because a route answering 200 with a count is the
    /// shape this decision refuses and a status assertion alone would not see a body somebody added
    /// under a 204.
    /// </para>
    /// <para>
    /// <b>The stamp is asserted beside the ciphertext on every arm, and neither half stands alone.</b> A
    /// chunk that wrote the ciphertext and forgot the stamp leaves the completeness gate refusing for
    /// ever with nothing saying why. One that wrote the stamp and dropped the ciphertext is worse: the
    /// gate answers <b>complete</b>, the promotion destroys the only wrapped copies of the generation
    /// those rows are still sealed under, and the discovery is a person reloading the page to find their
    /// budget unreadable.
    /// </para>
    /// <para>
    /// <b>The blind index is read too, and on a payee it is the half that bites.</b> A rotation replaces
    /// the index key as well as the content key, so a route that carried the envelope and dropped
    /// <c>nameKey</c> — binding it to <see langword="null" /> because the member was spelled differently,
    /// say — would leave every payee keyed under the previous generation: find-or-create stops finding
    /// anything and the next transaction against each existing counterparty mints a duplicate.
    /// </para>
    /// <para>
    /// <b>Every column is compared against the exact bytes the chunk sent, not merely against "something
    /// changed".</b> <c>SealedNarrative</c> is deterministic in its label and the fourteen labels here are
    /// all distinct, so a route that wrote the account's new name into the payee's row, or a name into a
    /// description, fails on the value rather than passing a movement test.
    /// </para>
    /// <para>
    /// <b>THE CHUNK NAMES SEVEN ROWS AND NOT FIVE, AND THE TWO EXTRA ONES ARE THE POINT.</b>
    /// <c>payees</c> and <c>transactions</c> each carry two entries under different labels, so a mapping
    /// that forwarded only the first entry of an array — <c>FirstOrDefault()</c>, <c>Take(1)</c>, or a
    /// scalar where a list belongs — fails on the row it skipped and names it. With one entry per arm
    /// that mapping passed every case in this file while turning a real rotation into dozens of one-row
    /// writes whose completeness gate never converges. See <see cref="ResealedRowCount" />.
    /// </para>
    /// <para>
    /// <b>No re-authentication, and its absence is a decision this case relies on.</b> A full session
    /// already writes narrative ciphertext through the ordinary routes; a chunk writes the same columns
    /// plus the stamp, and the stamp is read only by a completion that cannot promote anything a gated
    /// begin did not stage. A prompt per chunk would also break the feature outright — dozens of
    /// authenticator taps for one rotation. So this case posts no assertion members at all, and a route
    /// that grew a gate would answer it 401.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WithEveryArm_Answers204AndStampsEveryRowItRewrites()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Furnished furnished = await FurnishAccountAsync(host, "google-chunk-owner");
        Guid rotationId = await StageRotationAsync(host, furnished);
        IReadOnlyDictionary<string, RowState> before = await SnapshotAsync(admin, furnished);

        // The arrangement, asserted before the subject is touched: all seven rows exist — five arms, two
        // of which carry two — and not one of them carries a stamp, so every claim below is about what
        // the act did. The count is written out rather than taken from the fixture: a seeder that
        // quietly stopped writing the second payee would otherwise shrink the batch back to the shape
        // this number exists to refuse.
        await Assert.That(before.Count).IsEqualTo(ResealedRowCount);
        await Assert.That(before
                .Where(row => row.Value.RotationId is not null)
                .Select(row => row.Key)
                .ToArray())
            .IsEmpty();

        // Act
        HttpResponseMessage response = await furnished.Client.PostAsJsonAsync(
            ChunkPath, ChunkBodyFor(rotationId, furnished));

        // Assert — the status first, so a body missing because the request was refused reads as the
        // refusal it is.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(string.Empty);

        // Every column of every arm, against the exact value the chunk carried.
        await AssertRotatedAsync(admin, furnished, rotationId);

        // And the values really are different from what was seeded — without this, a fixture that
        // happened to seed the rotated labels would make every comparison above vacuous.
        await Assert.That(before
                .Where(row => row.Value.Equals(SnapshotOfRotated(row.Key, rotationId)))
                .Select(row => row.Key)
                .ToArray())
            .IsEmpty();
    }

    /// <summary>
    /// A session opened by the account's federated credential is refused this route with <b>403</b>, and
    /// it is the locked-session gate's 403 rather than the CSRF control's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The route must declare no <c>AllowsLockedSessionAttribute</c>, which is the whole of what this
    /// case asks about.</b> <c>FullSessionRequirement</c> rides the fallback policy and the opted-out set
    /// is exactly <c>POST /api/me/session/revocation</c>. A chunk rewrites narrative columns under the
    /// next generation's keys, so a provider sign-in reaching it would be a caller who cannot hold the
    /// account's keys writing values that claim to be sealed under them.
    /// </para>
    /// <para>
    /// <b>THE FULL-SESSION ARM IS NOT A CONTROL, IT IS WHAT MAKES THIS CASE ABOUT A ROUTE AT ALL — and
    /// it is here because the locked half passed before the begin route existed.</b>
    /// <c>AuthorizationMiddleware</c> applies the fallback policy to a request that matched <b>no
    /// endpoint</b> as readily as to one that matched an unmarked one, so a <c>POST</c> to a path this
    /// application does not serve is answered 403 to a locked session and 404 to everybody else —
    /// measured, on <c>KeyRotationBeginEndpointTests</c>' first run. A case asserting the 403 alone is
    /// therefore green against an application in which this route was never mapped. The three statuses
    /// separate cleanly: <b>404 means no endpoint, 403 means a policy refused, and only a status from a
    /// handler that ran proves the route exists.</b>
    /// </para>
    /// <para>
    /// <b>A 400 rather than a 204 as that arm, deliberately.</b> The full session's body quotes a
    /// rotation nothing staged, which is the handler's own refusal — it needs no furnished budget, no
    /// staged run and no envelopes, and it already establishes everything this case needs: the route is
    /// mapped, a full session reaches it, and the body got as far as a handler. The 204 is the happy
    /// path's to own.
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
    public async Task ResealChunk_FromALockedSession_Answers403()
    {
        // Arrange — one host, two accounts, two sessions: one opened by a federated credential, which is
        // the only kind that derives SessionKind.Locked, and one opened by a passkey.
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient locked = await host.Factory.CreateSignedInClientAsync(
            "google-chunk-locked", kind: SessionKind.Locked);
        ApiFactory.SignedInClient full = await host.Factory.CreateSignedInClientAsync(
            "google-chunk-full", kind: SessionKind.Full);

        // Act — one well-shaped, meaningless body, so the only thing differing between the two requests
        // is the session that carried it.
        HttpResponseMessage refused = await locked.Client.PostAsJsonAsync(ChunkPath, EmptyChunkBody());
        HttpResponseMessage admitted = await full.Client.PostAsJsonAsync(ChunkPath, EmptyChunkBody());

        // Assert — the admitted arm first, so a route that is not mapped at all reads as the 404 it is
        // rather than as a locked session being refused.
        await Assert.That(admitted.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(admitted.StatusCode).IsNotEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // The refusal, and that it is this gate's refusal rather than the CSRF control's.
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await TitleOfAsync(refused)).IsNotEqualTo(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// One account as this file needs it: a signed-in client, and the row it holds in each of the five
    /// arms.
    /// </summary>
    /// <remarks>
    /// <b>Every row carries a narrative value, including the two optional notes and the transaction's.</b>
    /// A row bearing none is a row a chunk never visits, and <c>NarrativeReseal</c> refuses an entry that
    /// changes whether a nullable column holds a value — so a fixture that left a note out by accident
    /// would arrange the presence rule's case inside every test here, and the refusal would read as the
    /// route being broken.
    /// </remarks>
    private sealed record Furnished(
        ApiFactory.SignedInClient SignedIn,
        Guid AccountId,
        Guid PayeeId,
        Guid SecondPayeeId,
        Guid CategoryGroupId,
        Guid CategoryId,
        Guid TransactionId,
        Guid SecondTransactionId)
    {
        public HttpClient Client => SignedIn.Client;

        public Guid UserId => SignedIn.UserId;

        public Guid BudgetId => SignedIn.BudgetId;

        /// <summary>
        /// The identifiers of this account's rows in <paramref name="table" />, in the order the chunk
        /// names them.
        /// </summary>
        /// <remarks>
        /// <b>A list rather than one identifier, because two arms carry two rows</b> — see
        /// <see cref="ResealedRowCount" /> for why one row per arm is not a batch. The order matters:
        /// it is the order <see cref="ChunkBodyFor" /> sends and the order
        /// <see cref="SnapshotOfRotated" /> answers for, so a route that forwarded only the first entry
        /// fails on the row it skipped and names it.
        /// <para>
        /// It throws on an unknown name rather than returning an empty list, because nothing would match
        /// and the miss would be reported one layer away from its cause.
        /// </para>
        /// </remarks>
        public IReadOnlyList<Guid> RowsIn(string table) => table switch
        {
            "accounts" => [AccountId],
            "payees" => [PayeeId, SecondPayeeId],
            "category_groups" => [CategoryGroupId],
            "categories" => [CategoryId],
            "transactions" => [TransactionId, SecondTransactionId],
            _ => throw new ArgumentOutOfRangeException(
                nameof(table), table, "No arm of a chunk writes that table."),
        };
    }

    /// <summary>
    /// The columns of one row a reseal may write, as the database holds them.
    /// </summary>
    /// <remarks>
    /// A <see langword="record" /> so that "nothing moved" is one comparison per arm rather than four,
    /// and the byte arrays are compared by content through <see cref="Equals(RowState)" /> — a record's
    /// generated equality over <see cref="byte" /><c>[]</c> is reference equality, which would report two
    /// reads of an unchanged row as different and make every "rewrites nothing" assertion pass for the
    /// wrong reason.
    /// </remarks>
    private sealed record RowState(byte[]? Name, byte[]? NameKey, byte[]? Description, Guid? RotationId)
    {
        public bool Equals(RowState? other) =>
            other is not null
            && Same(Name, other.Name)
            && Same(NameKey, other.NameKey)
            && Same(Description, other.Description)
            && RotationId == other.RotationId;

        public override int GetHashCode() => RotationId?.GetHashCode() ?? 0;

        private static bool Same(byte[]? left, byte[]? right) =>
            (left, right) switch
            {
                (null, null) => true,
                (null, _) or (_, null) => false,
                _ => left.AsSpan().SequenceEqual(right),
            };
    }

    /// <summary>
    /// The whole chunk a correct client sends for <paramref name="furnished" />: every row it holds, in
    /// all five arms, re-sealed under a label that differs from the seeded one.
    /// </summary>
    /// <param name="rotationId">The run this chunk quotes.</param>
    /// <param name="furnished">The account whose rows it names.</param>
    /// <param name="payees">
    /// The payee array to send, or <see langword="null" /> for this account's own two. The cases that
    /// override it name a row of <b>another</b> budget, or spell a member in the wrong alphabet.
    /// </param>
    /// <param name="transactions">
    /// The transaction array to send, or <see langword="null" /> for this account's own two. The one case
    /// that overrides it sends an envelope below the framing floor.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Anonymous objects rather than a request record</b>, so nothing here depends on a type the
    /// endpoint has not been written with yet — and so the wire members are pinned by use. The
    /// identifiers are <see cref="Guid" />s and every sealed value is unpadded base64url, which is how
    /// binary crosses JSON everywhere in this API.
    /// </para>
    /// <para>
    /// <b><c>payees</c> and <c>transactions</c> carry two entries and the other three carry one</b> —
    /// see <see cref="ResealedRowCount" /> for why one row per arm would leave a mapping that forwards
    /// only the first entry passing every case in this file.
    /// </para>
    /// </remarks>
    private static object ChunkBodyFor(
        Guid rotationId,
        Furnished furnished,
        object[]? payees = null,
        object[]? transactions = null) => new
        {
            rotationId,
            accounts = new object[]
            {
                new
                {
                    id = furnished.AccountId,
                    name = SealedNarrative.EncodedName(RotatedAccountLabel),
                    nameKey = SealedNarrative.EncodedIndex(RotatedAccountLabel),
                },
            },
            payees = payees ?? OwnPayeeEntries(furnished),
            categoryGroups = new object[]
            {
                new
                {
                    id = furnished.CategoryGroupId,
                    name = SealedNarrative.EncodedName(RotatedGroupLabel),
                    nameKey = SealedNarrative.EncodedIndex(RotatedGroupLabel),
                    description = SealedNarrative.EncodedDescription(RotatedGroupNote),
                },
            },
            categories = new object[]
            {
                new
                {
                    id = furnished.CategoryId,
                    name = SealedNarrative.EncodedName(RotatedCategoryLabel),
                    nameKey = SealedNarrative.EncodedIndex(RotatedCategoryLabel),
                    description = SealedNarrative.EncodedDescription(RotatedCategoryNote),
                },
            },
            transactions = transactions ?? OwnTransactionEntries(furnished),
        };

    /// <summary>Both of this account's payees, in the order <c>Furnished.RowsIn</c> answers them.</summary>
    private static object[] OwnPayeeEntries(Furnished furnished) =>
    [
        PayeeEntry(furnished.PayeeId, RotatedPayeeLabel),
        PayeeEntry(furnished.SecondPayeeId, RotatedSecondPayeeLabel),
    ];

    /// <summary>
    /// Both of this account's transactions, in the order <c>Furnished.RowsIn</c> answers them.
    /// </summary>
    private static object[] OwnTransactionEntries(Furnished furnished) =>
    [
        TransactionEntry(
            furnished.TransactionId, SealedNarrative.EncodedDescription(RotatedTransactionNote)),
        TransactionEntry(
            furnished.SecondTransactionId,
            SealedNarrative.EncodedDescription(RotatedSecondTransactionNote)),
    ];

    /// <summary>One entry of the payee arm, as the wire carries it.</summary>
    private static object PayeeEntry(Guid payeeId, string label) => new
    {
        id = payeeId,
        name = SealedNarrative.EncodedName(label),
        nameKey = SealedNarrative.EncodedIndex(label),
    };

    /// <summary>
    /// One entry of the payee arm whose two halves are spelled in different alphabets.
    /// </summary>
    /// <remarks>
    /// The envelope stays in the alphabet this API carries binary in, so the only thing wrong with the
    /// entry is the index — see the case that sends it for why the index is the member to get wrong.
    /// </remarks>
    private static object PayeeEntryWithNameKey(Guid payeeId, string label, string nameKeyText) => new
    {
        id = payeeId,
        name = SealedNarrative.EncodedName(label),
        nameKey = nameKeyText,
    };

    /// <summary>One entry of the transaction arm, as the wire carries it.</summary>
    private static object TransactionEntry(Guid transactionId, string descriptionText) => new
    {
        id = transactionId,
        description = descriptionText,
    };

    /// <summary>
    /// A chunk that is well shaped and means nothing: a run nobody staged, and five arms naming no rows.
    /// </summary>
    /// <remarks>
    /// <b>Both arms of the locked-session case post this, so the only thing differing between the two
    /// requests is the session.</b> The arrays are present and empty rather than absent, so the request
    /// is one a client could really send and the 400 that answers it is the handler's refusal about the
    /// quoted run rather than anything about the arms.
    /// </remarks>
    private static object EmptyChunkBody() => new
    {
        rotationId = Guid.CreateVersion7(),
        accounts = Array.Empty<object>(),
        payees = Array.Empty<object>(),
        categoryGroups = Array.Empty<object>(),
        categories = Array.Empty<object>(),
        transactions = Array.Empty<object>(),
    };

    /// <summary>
    /// That every arm holds exactly what <see cref="ChunkBodyFor" /> sent, stamped with
    /// <paramref name="rotationId" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Arm by arm, and the offenders are collected rather than asserted one at a time</b>, so a
    /// failure names every arm that stood still and not merely the first. A chunk touching five tables
    /// has five chances to be the one that was missed.
    /// </para>
    /// <para>
    /// <b>The expected value is built from the same labels the body was built from</b> — not read back
    /// and compared against itself, and not compared against "whatever is not the old value". A route
    /// that wrote the account's new name into the payee's row moves both columns and would pass any
    /// movement test.
    /// </para>
    /// </remarks>
    private static async Task AssertRotatedAsync(
        NpgsqlConnection admin,
        Furnished furnished,
        Guid rotationId)
    {
        IReadOnlyDictionary<string, RowState> after = await SnapshotAsync(admin, furnished);

        await Assert.That(after
                .Where(row => !row.Value.Equals(SnapshotOfRotated(row.Key, rotationId)))
                .Select(row => $"{row.Key}: {Describe(row.Value)}")
                .ToArray())
            .IsEmpty();
    }

    /// <summary>
    /// What the row under one snapshot key must hold once the chunk has been applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed per ROW rather than per table</b>, because two arms carry two rows and the second of
    /// each holds a different value from the first. A per-table expectation would be satisfied by a
    /// route that wrote the first entry's value into both rows of the arm — which is the neighbouring
    /// defect to forwarding only the first entry, and equally silent.
    /// </para>
    /// <para>
    /// The absent columns are <see langword="null" /> rather than empty: <c>accounts</c> and
    /// <c>payees</c> carry no description and <c>transactions</c> carries no name, so a comparison over
    /// the record compares what each row actually holds.
    /// </para>
    /// </remarks>
    private static RowState SnapshotOfRotated(string key, Guid rotationId) => key switch
    {
        "accounts[0]" => new RowState(
            SealedNarrative.Name(RotatedAccountLabel).Envelope.ToArray(),
            SealedNarrative.BlindIndex(RotatedAccountLabel).ToArray(),
            null,
            rotationId),
        "payees[0]" => new RowState(
            SealedNarrative.Name(RotatedPayeeLabel).Envelope.ToArray(),
            SealedNarrative.BlindIndex(RotatedPayeeLabel).ToArray(),
            null,
            rotationId),
        "payees[1]" => new RowState(
            SealedNarrative.Name(RotatedSecondPayeeLabel).Envelope.ToArray(),
            SealedNarrative.BlindIndex(RotatedSecondPayeeLabel).ToArray(),
            null,
            rotationId),
        "category_groups[0]" => new RowState(
            SealedNarrative.Name(RotatedGroupLabel).Envelope.ToArray(),
            SealedNarrative.BlindIndex(RotatedGroupLabel).ToArray(),
            SealedNarrative.Description(RotatedGroupNote).Envelope.ToArray(),
            rotationId),
        "categories[0]" => new RowState(
            SealedNarrative.Name(RotatedCategoryLabel).Envelope.ToArray(),
            SealedNarrative.BlindIndex(RotatedCategoryLabel).ToArray(),
            SealedNarrative.Description(RotatedCategoryNote).Envelope.ToArray(),
            rotationId),
        "transactions[0]" => new RowState(
            null,
            null,
            SealedNarrative.Description(RotatedTransactionNote).Envelope.ToArray(),
            rotationId),
        "transactions[1]" => new RowState(
            null,
            null,
            SealedNarrative.Description(RotatedSecondTransactionNote).Envelope.ToArray(),
            rotationId),
        _ => throw new ArgumentOutOfRangeException(
            nameof(key), key, "No row of a chunk is filed under that key."),
    };

    /// <summary>One row's columns as a sentence a reader can line up against the request.</summary>
    private static string Describe(RowState row) =>
        $"name={Text(row.Name)} nameKey={Text(row.NameKey)} description={Text(row.Description)} "
        + $"stamp={row.RotationId?.ToString() ?? "none"}";

    private static string Text(byte[]? bytes) =>
        bytes is null ? "absent" : Base64UrlText.Encode(bytes);

    /// <summary>
    /// Every column a reseal may touch, on every arm, read on the container superuser connection.
    /// </summary>
    /// <remarks>
    /// <b>Read as columns rather than through entities</b>, following <c>ResealChunkTests</c>: the three
    /// nullable narrative columns round-trip through a value converter, and one that materialised an
    /// empty envelope for a <c>NULL</c> would make an entity disagree with the column a completeness
    /// gate's SQL tests. Reading raw also keeps this guard independent of the mapping the act went
    /// through.
    /// <para>
    /// <b>Keyed <c>table[ordinal]</c> rather than by table</b>, because two arms carry two rows — so a
    /// failure names the row and not merely the arm, and the two rows of one arm cannot satisfy a single
    /// expectation between them.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, RowState>> SnapshotAsync(
        NpgsqlConnection admin,
        Furnished furnished)
    {
        Dictionary<string, RowState> snapshot = [];

        foreach (string table in ResealedTables)
        {
            IReadOnlyList<Guid> rows = furnished.RowsIn(table);

            for (int ordinal = 0; ordinal < rows.Count; ordinal++)
            {
                snapshot[$"{table}[{ordinal}]"] = await RowOfAsync(admin, table, rows[ordinal]);
            }
        }

        return snapshot;
    }

    /// <summary>The four columns of one named row.</summary>
    private static async Task<RowState> RowOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        string columns = table switch
        {
            "accounts" or "payees" => "name, name_key, null::bytea, rotation_id",
            "category_groups" or "categories" => "name, name_key, description, rotation_id",
            "transactions" => "null::bytea, null::bytea, description, rotation_id",
            _ => throw new ArgumentOutOfRangeException(
                nameof(table), table, "No arm of a chunk writes that table."),
        };

        // The identifier is interpolated because an identifier cannot be a parameter, and it is never
        // caller-supplied text: every value reaching here comes from ResealedTables or from a literal in
        // this file.
        await using NpgsqlCommand command = new($"select {columns} from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No row of '{table}' is filed under {rowId}.");
        }

        return new RowState(
            Bytes(reader, 0), Bytes(reader, 1), Bytes(reader, 2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    private static byte[]? Bytes(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);

    /// <summary>Reads one row's stamp back, as the database holds it.</summary>
    private static async Task<Guid?> StampOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        await using NpgsqlCommand command = new($"select rotation_id from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);

        return await command.ExecuteScalarAsync() switch
        {
            Guid stamp => stamp,
            null or DBNull => null,
            var unexpected => throw new InvalidOperationException(
                $"'{table}'.rotation_id came back as '{unexpected}'."),
        };
    }

    /// <summary>Reads one row's blind index back, as the database holds it.</summary>
    private static async Task<byte[]> NameKeyOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        await using NpgsqlCommand command = new($"select name_key from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);

        return await command.ExecuteScalarAsync() is byte[] nameKey
            ? nameKey
            : throw new InvalidOperationException($"No row of '{table}' is filed under {rowId}.");
    }

    /// <summary>Which budget one row belongs to, read on the superuser connection.</summary>
    /// <remarks>
    /// The foreign-row case turns on this: a payee seeded into the caller's own budget by mistake would
    /// be re-sealed rather than refused, and the case would then measure the opposite thing.
    /// </remarks>
    private static async Task<Guid> BudgetOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        await using NpgsqlCommand command = new($"select budget_id from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);

        return await command.ExecuteScalarAsync() is Guid budgetId
            ? budgetId
            : throw new InvalidOperationException($"No row of '{table}' is filed under {rowId}.");
    }

    /// <summary>The account's one staged rotation identifier, or a failure saying how many there were.</summary>
    /// <remarks>
    /// Sole rather than first: <c>user_id</c> is the primary key of <c>key_rotations</c>, so a second row
    /// is a database that has lost the rule making two concurrent runs unstorable — not a row to choose
    /// between.
    /// </remarks>
    private static async Task<Guid> StagedRotationIdAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select rotation_id from key_rotations where user_id = @id", admin);
        command.Parameters.AddWithValue("id", userId);

        List<Guid> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetGuid(0));
        }

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException(
                $"Expected exactly one staged rotation, found {rows.Count}.");
    }

    /// <summary>
    /// Seeds a signed-in account and puts one narrative-bearing row into each of the five arms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written through the domain's own factories over the container superuser connection</b>, the
    /// way every other arrangement of narrative rows in this suite is: the product offers no route that
    /// creates a category group or a payee in bulk, and rows written by raw SQL would carry envelopes no
    /// production write path could have produced.
    /// </para>
    /// <para>
    /// <b>The session is seeded rather than established through a ceremony</b>, because a chunk needs no
    /// assertion — its whole point is that it does not. The credential the seeding writes is a passkey,
    /// which is also what <see cref="StageRotationAsync" /> needs to open a run under.
    /// </para>
    /// </remarks>
    private static async Task<Furnished> FurnishAccountAsync(PostgresTestHost host, string subject)
    {
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            subject, kind: SessionKind.Full);

        await using BudgetoidDbContext db = AdminDb(host, signedIn.BudgetId);

        Account account = Account.Create(
            Guid.CreateVersion7(),
            signedIn.BudgetId,
            SealedNarrative.Indexed(SeededAccountLabel),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            SeedInstant);
        Payee payee = Payee.Create(
            Guid.CreateVersion7(),
            signedIn.BudgetId,
            SealedNarrative.Indexed(SeededPayeeLabel),
            SeedInstant);

        // The second row of this arm, under a label of its own — payees carry a blind-index uniqueness
        // rule over one budget, so two rows sharing a label would collide rather than seed a second row.
        Payee secondPayee = Payee.Create(
            Guid.CreateVersion7(),
            signedIn.BudgetId,
            SealedNarrative.Indexed(SeededSecondPayeeLabel),
            SeedInstant);
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            signedIn.BudgetId,
            SealedNarrative.Indexed(SeededGroupLabel),
            SealedNarrative.Description(SeededGroupNote),
            0,
            SeedInstant);
        db.Accounts.Add(account);
        db.Payees.Add(payee);
        db.Payees.Add(secondPayee);
        db.CategoryGroups.Add(group);
        await db.SaveChangesAsync();

        // A save of its own: a category names its group by foreign key, so the group has to be on the
        // database before it can be filed under.
        Category category = Category.Create(
            Guid.CreateVersion7(),
            signedIn.BudgetId,
            group.Id,
            SealedNarrative.Indexed(SeededCategoryLabel),
            SealedNarrative.Description(SeededCategoryNote),
            0,
            SeedInstant);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            signedIn.BudgetId,
            account.Id,
            -10m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description(SeededTransactionNote),
            SeedInstant);

        // The second row of this arm. Transactions have no name and no unique index but the key, so its
        // note is the only thing that has to differ — and it does, because a chunk that wrote the first
        // entry's value into both rows must fail on the second.
        Transaction secondTransaction = Transaction.Create(
            Guid.CreateVersion7(),
            signedIn.BudgetId,
            account.Id,
            -47m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 14),
            SealedNarrative.Description(SeededSecondTransactionNote),
            SeedInstant);
        db.Transactions.Add(transaction);
        db.Transactions.Add(secondTransaction);
        await db.SaveChangesAsync();

        return new Furnished(
            signedIn,
            account.Id,
            payee.Id,
            secondPayee.Id,
            group.Id,
            category.Id,
            transaction.Id,
            secondTransaction.Id);
    }

    /// <summary>
    /// Stages one rotation for <paramref name="furnished" /> and returns the identifier a chunk has to
    /// quote.
    /// </summary>
    /// <remarks>
    /// <b>Through <see cref="KeyRotation.Begin" /> over the account's loaded passkey</b> rather than an
    /// <c>insert</c>, the idiom <c>KeyRotationRepositoryTests</c> and <c>ResealChunkTests</c> keep: the
    /// factory refuses an absent or over-wide manifest, an epoch below the floor, an empty identifier
    /// and a credential that is not a passkey, so a seeded row is one a begin could really have written.
    /// The seals a begin writes beside it are deliberately absent — nothing a chunk does reads one, and
    /// seeding them would suggest otherwise.
    /// </remarks>
    private static async Task<Guid> StageRotationAsync(PostgresTestHost host, Furnished furnished)
    {
        await using BudgetoidDbContext db = AdminDb(host, furnished.BudgetId);
        Credential passkey = await db.Credentials.SingleAsync(
            credential => credential.UserId == furnished.UserId
                          && credential.Type == CredentialType.Passkey);
        KeyRotation rotation = KeyRotation.Begin(
            passkey,
            Guid.CreateVersion7(),
            ManifestFixture.Mint().Manifest,
            StagedRotationEpoch,
            SeedInstant);
        db.KeyRotations.Add(rotation);
        await db.SaveChangesAsync();

        return rotation.RotationId;
    }

    /// <summary>
    /// A context on the container superuser connection bound to one ambient budget, so the five
    /// budget-owned tables' query filters resolve while the rows are written.
    /// </summary>
    private static BudgetoidDbContext AdminDb(PostgresTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    private static async Task<NpgsqlConnection> OpenAdminAsync(PostgresTestHost host)
    {
        NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    /// <summary>
    /// A host serving requests under the application's own authentication, so a seeded session cookie is
    /// read by the handler that reads one in production.
    /// </summary>
    /// <remarks>
    /// No <c>repointsProviderSchemeToTestHandler</c>, because nothing here drives the registration
    /// ceremony — see this class's remarks for why a chunk suite stages its rotations rather than
    /// beginning them.
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
}
