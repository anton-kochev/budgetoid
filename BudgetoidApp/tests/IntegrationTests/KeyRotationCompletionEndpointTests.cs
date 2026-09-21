using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.KeyRotations.CompleteKeyRotation;
using Domain.Payees;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The completion leg of a content-key rotation — <c>POST /api/me/key-rotation/completion</c> — driven
/// over real HTTP against a real account, on the route's own terms rather than the handler's.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>CompleteKeyRotationHandler</c>, <c>WrappedAccountKeys.Promote</c>, the four
/// <c>IKeyRotationRepository</c> members the completion reads and writes through, and the adapter under
/// them all exist and are reachable by nothing.</b> That handler's own remarks say so in as many words —
/// "no route reaches this handler yet" — and this file is the commit that makes the sentence false. Every
/// case below is red until a route is mapped, and every one of them turns into an assertion about
/// behaviour the moment one is.
/// </para>
/// <para>
/// <b>Every test posts the wire shape rather than naming a request record</b>, the idiom
/// <c>KeyRotationBeginEndpointTests</c> and <c>ResealChunkEndpointTests</c> keep for the same reason:
/// nothing here depends on a type the endpoint has not been written with yet. It also means the
/// <b>wire member is pinned by use</b> — one member, <c>rotationId</c>, and nothing else. A route
/// spelling it differently binds it to <see cref="Guid.Empty" /> and the happy path below finds an
/// account that never moved.
/// </para>
/// <para>
/// <b>NOTHING HERE RESTATES WHAT <c>CompleteKeyRotationHandlerTests</c> AND
/// <c>KeyRotationCompletionTests</c> ALREADY HOLD, AND THE OMISSIONS ARE DELIBERATE.</b> The order of the
/// handler's refusals, the substitution of the staged identifier for the caller's, the discard at the top
/// of the transactional delegate, the promotion sitting inside the unit of work, the escape of
/// <c>RotationScopeException</c>, the 500 an account with no manifest earns, and "each factor adopts its
/// own seal rather than a zipped neighbour's" are all held one or two tiers down by instruments that can
/// see the side of a delegate a write landed on. Repeating any of them over HTTP would buy a slower copy
/// of a sharper test. What is <em>only</em> visible from here is the route: that it exists, that it is
/// behind the full-session gate, what status each refusal reaches a client as, that the accepted case
/// answers 204 carrying nothing, and that a refusal really left the account's live rows alone.
/// </para>
/// <para>
/// <b>204 AND NO EPOCH IN THE BODY, WHICH IS THE ONE DECISION THIS FILE OWNS OUTRIGHT.</b> A client must
/// not advance its rotation-epoch record from a number this route handed back:
/// <c>docs/business-logic/account-keys.md</c> requires that record to rise only after the four-refusal
/// gate over <c>GET /api/me/account-keys</c> has passed, and an epoch returned here is one a client could
/// advance from without judging anything — an oracle rather than an observation. So the happy path reads
/// the body and asserts it <b>empty</b> rather than merely ignoring it; a status assertion alone would
/// not see a number somebody added under a 204.
/// </para>
/// <para>
/// <b>No re-authentication, and its absence is a decision every case here relies on.</b> The destructive
/// act and the authorizing act are two legs of one operation and the authorization was created at the
/// begin — the request where the account committed to a new generation. An attacker holding a session and
/// no authenticator reaches exactly two outcomes: completing a run before the client meant to, which the
/// completeness gate refuses, or completing a finished run, which is what the legitimate client was about
/// to do. Neither is a capability the begin's gate did not already grant. So every body below carries one
/// member, and a route that grew a gate would answer 401 to all of them.
/// </para>
/// <para>
/// <b>A ROUTE THAT WAS NEVER MAPPED ANSWERS TWO OF THE STATUSES A FILE LIKE THIS REACHES FOR, AND THIS
/// GROUP HAS NOW BEEN BITTEN BY BOTH FACES OF IT.</b> A request matching <b>no endpoint</b> is still seen
/// by <see cref="Microsoft.AspNetCore.Authorization.AuthorizationMiddleware" />, which applies the
/// fallback policy to it as readily as to one that matched an unmarked endpoint — so an unserved path
/// answers <b>403</b> to a locked session and a bare <b>404</b> to everybody else.
/// <c>KeyRotationBeginEndpointTests</c>' locked-session case passed before the begin route existed, and
/// <c>ResealChunkEndpointTests</c>' foreign-row case passed before the chunk route did.
/// <list type="bullet">
/// <item><description><b>A case expecting 403</b> needs an accepting arm on the same path whose status
/// only a handler can produce — the full-session request in
/// <see cref="CompleteKeyRotation_FromALockedSession_Answers403" />.</description></item>
/// <item><description><b>A case expecting 404</b> needs the response <em>body</em>, because an unmatched
/// request is written a 404 with no content type and no length. <b>No case here expects one</b>, which is
/// why that discriminator appears nowhere below.</description></item>
/// </list>
/// The four remaining cases assert 409, 400 or 204, and each of them reads a member of the body that only
/// a handler that <em>ran</em> can have written: the <c>conflictKind</c> token, or the <c>errors</c> key.
/// </para>
/// <para>
/// <b>Twelve factors under three credentials, which is not a round number chosen for effect.</b> Two
/// passkeys and a card of ten recovery codes: a set of codes is ten separate secrets under a single
/// <see cref="Credential" />, so it is ten <c>wrapped_account_keys</c> rows, and a route that promoted
/// "the factor" — or that promoted the passkeys and left the card behind — is the shape that reddens
/// nothing and costs somebody their way back in. Each row is seeded with a filler of its own and each
/// staged seal with another, in two blocks that do not overlap and neither of which is
/// <see cref="RepositoryTestHost.SeededAccountKeysFiller" />, so "promoted" and "left alone" cannot read
/// the same for any factor.
/// </para>
/// <para>
/// <b>Rotations are staged through <see cref="KeyRotation.Begin" /> and the real adapter over the
/// container superuser connection rather than through <c>POST /api/me/key-rotation</c>.</b> The begin is
/// gated by a fresh WebAuthn assertion, which costs a registration ceremony, a synthetic authenticator
/// and a re-authentication nonce per test — and a completion reads nothing a begin writes except the
/// staging row and its seals. Driving it here would make every case below able to fail for the begin's
/// reasons, which <c>KeyRotationBeginEndpointTests</c> already owns. Through the adapter and the loaded
/// rows, because <see cref="KeyRotationSeal.For" /> takes the entity and refuses a great deal a
/// fabricated row would sail past.
/// </para>
/// <para>
/// <b>Every arrangement and every read-back runs on <see cref="PostgresTestHost.ConnectionString" /></b>
/// — the container superuser — and only the act runs as the application, through the API's own
/// least-privilege connection. Half of what is asserted is that a column did <em>not</em> move, and a
/// policed connection reports a row it cannot see exactly as it reports one that did not change. The
/// read-back context is also one that did not write the rows, which is what makes every assertion below a
/// statement about PostgreSQL rather than about an identity map.
/// </para>
/// </remarks>
public sealed class KeyRotationCompletionEndpointTests
{
    private const string CompletionPath = "/api/me/key-rotation/completion";

    /// <summary>How many passkeys the account holds, and how many factors a card of codes is.</summary>
    /// <remarks>
    /// Two passkeys rather than one, because a set-shaped bug that promoted "the passkey" would be
    /// invisible on an account holding exactly one of everything.
    /// </remarks>
    private const int PasskeyCount = 2;

    /// <inheritdoc cref="PasskeyCount" />
    private const int RecoveryCodeSetSize = 10;

    /// <summary>The number of <c>wrapped_account_keys</c> rows a furnished account holds.</summary>
    /// <remarks>
    /// Written out rather than derived from the fixture, <c>ResealChunkEndpointTests</c>' habit with its
    /// own row count: a seeder that quietly stopped writing the card would otherwise shrink the factor
    /// set back to the shape this number exists to refuse, and every sweep below would go on passing over
    /// a set of one.
    /// </remarks>
    private const int FactorCount = PasskeyCount + RecoveryCodeSetSize;

    /// <summary>
    /// The generation a staged run carries here, and the one the account ends at.
    /// </summary>
    /// <remarks>
    /// <see cref="RepositoryTestHost" /> seeds an account's first manifest at
    /// <see cref="FactorManifest.MinimumRotationEpoch" />, which is where registration files it, so every
    /// account here has never rotated and the completion is its first.
    /// <see cref="PromotedRotationEpoch" /> is written out as a literal beside the expression that
    /// derives it, so "rose by exactly one" is checked against a number this file states rather than only
    /// against arithmetic it performed.
    /// </remarks>
    private const int StagedRotationEpoch = FactorManifest.MinimumRotationEpoch + 1;

    /// <inheritdoc cref="StagedRotationEpoch" />
    private const int PromotedRotationEpoch = 2;

    /// <summary>
    /// The two blocks of fillers this file's payloads carry — the twelve rows as they stand, and the
    /// twelve values their seals stage.
    /// </summary>
    /// <remarks>
    /// Neither block overlaps the other and neither reaches
    /// <see cref="RepositoryTestHost.SeededAccountKeysFiller" />, which is what the default seeding
    /// writes: a row that had kept the seeder's own payload is therefore distinguishable from one that
    /// kept this file's, and both from one that adopted a seal.
    /// </remarks>
    private const byte StoredFillerBase = 0x40;

    /// <inheritdoc cref="StoredFillerBase" />
    private const byte StagedFillerBase = 0x10;

    /// <summary>How many bytes a seeded WebAuthn credential id carries.</summary>
    /// <remarks>
    /// Only the width and the distinctness matter: nothing on this path verifies a signature, and the
    /// column is unique, so the bytes are drawn at random rather than filled — two passkeys on one
    /// account need two handles, and a shared host may hold more than one account.
    /// </remarks>
    private const int WebAuthnCredentialIdLength = 16;

    /// <summary>The label the one outstanding narrative row of the incomplete case is sealed under.</summary>
    private const string OutstandingPayeeLabel = "completion grocer";

    /// <summary>
    /// Fixed UTC instant for every row this file writes itself. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing rather than
    /// decoration.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// <b>The riskiest case.</b> A rotation with one row still un-re-sealed is refused <b>409</b>, and not
    /// one factor and not the manifest is promoted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE STATUS IS HALF THE ASSERTION AND THE LESSER HALF.</b> A route that promoted the twelve rows
    /// and the manifest and <em>then</em> threw answers exactly this 409 — the handler's refusals are
    /// ordered so that cannot happen, but the ordering lives one ring down and a route wiring the call in
    /// the wrong place is not what that ordering holds. So the whole of the account's key material is
    /// snapshotted before the act and compared byte for byte afterwards: the manifest's epoch, the
    /// manifest's bytes, and every one of the twelve <c>encapsulated_account_keys</c>. Compared as
    /// base64url text rather than as byte collections, because TUnit's <c>IsEquivalentTo</c> defaults to
    /// <c>CollectionOrdering.Any</c> and a permuted payload would satisfy it.
    /// </para>
    /// <para>
    /// <b>The cost of getting this wrong is the reason it is first.</b> A promotion that runs while one
    /// row is still sealed under the superseded content key overwrites the only copies of the generation
    /// that row needs, and the row is unreadable <b>for ever</b>: no exception, no SQLSTATE, nothing
    /// logged, and no repair path. Every other case in this file is about a status; this one is about the
    /// bytes.
    /// </para>
    /// <para>
    /// <b>One unstamped payee, and it is seeded rather than left over.</b> A furnished account here holds
    /// no narrative row at all — its default budget carries no name — so the gate would answer
    /// <em>complete</em> without a chunk having run, which is what a presence-aware read is supposed to
    /// do. The payee is what makes the account genuinely outstanding, and its stamp is asserted absent as
    /// a premise: without that, this case is the happy path wearing another name.
    /// </para>
    /// <para>
    /// <b><c>conflictKind</c> is read and it is what says a handler ran.</b> 409 is a status no unmapped
    /// path can produce, but the token is the member that separates this refusal from the other 409 on
    /// this very route — <c>rotation_already_completed</c>, one step away in the handler — and from the
    /// <c>factor_set_moved</c> beside it. The remedy each names is different: send the outstanding chunks,
    /// do nothing at all, or run a whole new ceremony. The <em>sentence</em> is deliberately not pinned;
    /// it is the handler's own message copied verbatim into a Production body, and asserting it here would
    /// make this a phrasing test.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CompleteKeyRotation_WithARowStillUnsealed_Answers409AndPromotesNothing()
    {
        // Arrange — twelve factors, a staged run naming all of them, and one narrative row no chunk has
        // been sent for.
        await using PostgresTestHost host = await StartHostAsync();
        Rotating rotating = await FurnishAccountAsync(host, "google-completion-owner");
        Guid payeeId = await SeedUnstampedPayeeAsync(host, rotating);
        Staged staged = await StageRotationAsync(host, rotating);
        KeyState before = await SnapshotAsync(host, rotating.UserId);

        // The premises, or every comparison below is vacuous: the account really holds twelve factors, the
        // outstanding row really carries no stamp, and the staged values really differ from the live ones.
        await Assert.That(rotating.FactorIds.Count).IsEqualTo(FactorCount);
        await Assert.That(before.FactorKeys.Count).IsEqualTo(FactorCount);
        await Assert.That(await StampOfPayeeAsync(host, rotating, payeeId)).IsNull();
        await Assert.That(Differences(staged.AsPromoted(), before)).IsNotEmpty();

        // Act
        HttpResponseMessage response = await rotating.Client.PostAsJsonAsync(
            CompletionPath, CompletionBody(staged.RotationId));

        // Assert — refused rather than faulted, and refused rather than accepted.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NoContent);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        // The remedy a client branches on: send the chunks you still owe, after which this very request
        // succeeds. Written out rather than derived from the enum member's name, which is the rule
        // ConflictKindSpelling exists to keep.
        JsonObject problem = await ReadJsonObjectAsync(response);
        await Assert.That(problem["conflictKind"]).IsNotNull();
        await Assert.That(problem["conflictKind"]!.GetValue<string>()).IsEqualTo("rotation_incomplete");

        // AND NOTHING MOVED. The half a status assertion cannot see.
        await Assert.That(Differences(before, await SnapshotAsync(host, rotating.UserId))).IsEmpty();
    }

    /// <summary>
    /// The happy path: <b>204</b> with an empty body, every one of the twelve factors carrying the bytes
    /// of <b>its own</b> staged seal, and the manifest at the staged generation — exactly one above the
    /// stored one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>204 AND NEVER 200, AND THE BODY IS READ RATHER THAN IGNORED.</b> A completion creates no
    /// resource and renames none, so there is nothing to address — and an epoch member would be a number a
    /// client could advance its rotation-epoch record from without passing the four-refusal gate over
    /// <c>GET /api/me/account-keys</c> that record's rule requires. The client re-reads the account keys
    /// and advances there. A status assertion alone would not see a number somebody added under a 204, so
    /// the content is compared against the empty string.
    /// </para>
    /// <para>
    /// <b>AND THE HEADERS ARE SWEPT, BECAUSE THE RULE IS ABOUT THE VALUE ESCAPING AND NOT ABOUT THE
    /// BODY.</b> A route answering 204 and putting the new generation in an <c>ETag</c>, a
    /// <c>Location</c> or a header of its own hands a client exactly the number
    /// <c>docs/business-logic/account-keys.md</c> says it may not have — one it could advance its
    /// rotation-epoch record from having judged nothing, which is the oracle that rule exists to prevent.
    /// A header is as good an oracle as a body. So what is asserted is not a list of header names to keep
    /// in step with whatever gets added next: <b>no response header value, anywhere, is the promoted
    /// epoch</b>, and the two headers a reader would reach for first are additionally named because they
    /// are the two a framework can attach without anybody typing them. <b>Do not read the named pair as
    /// the rule and the sweep as belt-and-braces</b> — it is the other way round, and a header added later
    /// is covered by the sweep precisely because the sweep names nothing.
    /// </para>
    /// <para>
    /// <b>The sweep unframes before it compares</b>, so a quoted or weak entity tag cannot smuggle the
    /// value past a comparison made against bare text — <c>W/"2"</c> and <c>"2"</c> are the epoch as
    /// surely as <c>2</c> is. And it is proved <em>before</em> the subject is touched, against a response
    /// built here carrying the number two ways: a sweep that reported nothing would satisfy the assertion
    /// below perfectly, which is how this kind of check dies quietly.
    /// </para>
    /// <para>
    /// <b>Each factor is matched against its OWN staged seal, never against "some new bytes".</b> A route
    /// whose handler paired the two sets positionally would give every row a well-formed 158-byte value
    /// of the right version that only some <em>other</em> factor's private key can open: twelve good
    /// rows, no exception, no SQLSTATE, and an account that opens with none of them. Neither read carries
    /// an <c>ORDER BY</c>, so a zip is wrong here by construction rather than by arrangement.
    /// </para>
    /// <para>
    /// <b>The offenders are collected and asserted empty rather than compared one at a time</b>, so a
    /// failure names every factor that did not move instead of the first — and a TUnit string assertion
    /// truncates, which is why the collection is what is asserted on.
    /// </para>
    /// <para>
    /// <b>The account carries no narrative row, and that is not laziness.</b> The gate's own correctness —
    /// the <c>IS DISTINCT FROM</c> spelling that decides whether a never-stamped row is outstanding — is
    /// <c>RotationCompletenessTests</c>', and the refusal it produces is this file's first case. Here the
    /// account holds nothing to re-seal, so the gate answers complete without a stamp being written, which
    /// is what a presence-aware read is supposed to do and is not what is being measured.
    /// </para>
    /// <para>
    /// <b>The staging row and its seals are asserted still standing.</b> A completion deletes nothing —
    /// the application role holds no <c>DELETE</c> on either rotation table, so a route reaching for one
    /// would answer <c>42501</c> rather than tidying up, and a premature delete would destroy the only
    /// copies of a generation these rows have just been rewritten under.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CompleteKeyRotation_WhenEveryRowIsSealed_Answers204AndPromotesEveryFactor()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        Rotating rotating = await FurnishAccountAsync(host, "google-completion-owner");
        Staged staged = await StageRotationAsync(host, rotating);
        KeyState before = await SnapshotAsync(host, rotating.UserId);

        // The premises: twelve factors, a manifest at the floor, and not one staged value already standing
        // in the row it is destined for — without which "promoted" and "left alone" read the same.
        await Assert.That(rotating.FactorIds.Count).IsEqualTo(FactorCount);
        await Assert.That(before.RotationEpoch).IsEqualTo(FactorManifest.MinimumRotationEpoch);
        await Assert.That(Differences(staged.AsPromoted(), before).Length).IsEqualTo(FactorCount + 2);

        // The instrument, proved before the subject is touched: a response really carrying the epoch — once
        // in a header of its own and once inside a quoted entity tag — is reported by the sweep, both
        // times. Without this the header assertion below is satisfied by a sweep that reads nothing.
        await Assert.That(EpochCarriersIn(ProbeCarryingTheEpoch())).Count().IsEqualTo(2);

        // Act
        HttpResponseMessage response = await rotating.Client.PostAsJsonAsync(
            CompletionPath, CompletionBody(staged.RotationId));

        // Assert — the status first, so a body missing because the request was refused reads as the
        // refusal it is.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(string.Empty);

        // AND THE NUMBER IS NOWHERE IN THE HEADERS EITHER. The two a framework attaches without anybody
        // typing them, named first — then the sweep, which is the rule: no header value, whatever it is
        // called, is the promoted epoch.
        await Assert.That(response.Headers.Location).IsNull();
        await Assert.That(response.Headers.ETag).IsNull();
        await Assert.That(EpochCarriersIn(response)).IsEmpty();

        // Every factor carries its own seal's bytes, and the manifest carries the staged bytes at the
        // staged generation — one comparison over the whole of the account's key material.
        KeyState after = await SnapshotAsync(host, rotating.UserId);
        await Assert.That(Differences(staged.AsPromoted(), after)).IsEmpty();

        // And the generation rose by exactly one, against a literal this file states rather than only
        // against the arithmetic that produced the staged value.
        await Assert.That(after.RotationEpoch).IsEqualTo(PromotedRotationEpoch);
        await Assert.That(after.RotationEpoch).IsEqualTo(FactorManifest.MinimumRotationEpoch + 1);

        // The staging row and its seals survive the completion.
        await Assert.That(await StagedRotationCountAsync(host, rotating.UserId)).IsEqualTo(1);
        await Assert.That(await StagedSealCountAsync(host, rotating.UserId)).IsEqualTo(FactorCount);
    }

    /// <summary>
    /// A completion re-sent after the first one succeeded is refused <b>409</b>, and changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THIS IS THE EPOCH RULE AND NOT AN EMPTY TABLE, WHICH IS WHY THE CASE EXISTS AT ALL.</b> A
    /// completion deletes nothing, so the staging row is still standing carrying the identifier this
    /// second request quotes — "is this the staged run" answers <em>yes</em> for a run that finished
    /// moments ago. What tells a finished run from a live one is the gap between the staged generation and
    /// the manifest's, and a route reaching a handler that had lost that check would rewrite every factor
    /// with the value it already holds and then be refused by <c>FactorManifest.Promote</c> as a
    /// <b>400 about the caller's arithmetic</b> — the wrong answer to a client whose first request
    /// succeeded and whose response was lost. 400 is therefore named separately, before the equality.
    /// </para>
    /// <para>
    /// <b>The first send is asserted as arrangement.</b> Without that, a route answering 409 to everything
    /// — including a first send that promoted nothing — satisfies this case perfectly.
    /// </para>
    /// <para>
    /// <b>And the account is compared against what the first send left, not against what it started
    /// with.</b> A second promotion would be harmless in the bytes of the factors and catastrophic in the
    /// manifest, which is a concurrency token: what is checked is that the epoch is still
    /// <see cref="PromotedRotationEpoch" /> and every factor still holds its own seal.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CompleteKeyRotation_ResentAfterASuccessfulCompletion_Answers409AndChangesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        Rotating rotating = await FurnishAccountAsync(host, "google-completion-owner");
        Staged staged = await StageRotationAsync(host, rotating);
        object body = CompletionBody(staged.RotationId);

        // Act — the completion, then the very same body again.
        HttpResponseMessage first = await rotating.Client.PostAsJsonAsync(CompletionPath, body);
        KeyState promoted = await SnapshotAsync(host, rotating.UserId);
        HttpResponseMessage resent = await rotating.Client.PostAsJsonAsync(CompletionPath, body);

        // The arrangement, asserted: the first send really landed, or this case is the refusal of a run
        // that was never completed.
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(Differences(staged.AsPromoted(), promoted)).IsEmpty();

        // And the staging row really is still standing, which is what makes this the epoch rule rather
        // than the "no run in flight" refusal wearing its clothes.
        await Assert.That(await StagedRotationCountAsync(host, rotating.UserId)).IsEqualTo(1);

        // Assert — refused as "already done" rather than as a caller's arithmetic being wrong.
        await Assert.That(resent.StatusCode).IsNotEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(resent.StatusCode).IsNotEqualTo(HttpStatusCode.NoContent);
        await Assert.That(resent.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        JsonObject problem = await ReadJsonObjectAsync(resent);
        await Assert.That(problem["conflictKind"]).IsNotNull();
        await Assert.That(problem["conflictKind"]!.GetValue<string>())
            .IsEqualTo("rotation_already_completed");

        // And the second request moved nothing the first had left.
        await Assert.That(Differences(promoted, await SnapshotAsync(host, rotating.UserId))).IsEmpty();
    }

    /// <summary>
    /// A session opened by the account's federated credential is refused this route with <b>403</b>, and
    /// it is the locked-session gate's 403 rather than the CSRF control's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The route must declare no <see cref="AllowsLockedSessionAttribute" />, which is the whole of
    /// what this case asks about.</b> <see cref="FullSessionRequirement" /> rides the fallback policy and
    /// the opted-out set is exactly <c>POST /api/me/session/revocation</c>. A completion overwrites the
    /// only live copies of an account's content key, so a provider sign-in reaching it would be a caller
    /// who cannot hold that key deciding which generation of it the account keeps.
    /// </para>
    /// <para>
    /// <b>THE FULL-SESSION ARM IS NOT A CONTROL, IT IS WHAT MAKES THIS CASE ABOUT A ROUTE AT ALL — and it
    /// is here because the locked half passed before the begin route existed and again before the chunk
    /// route did.</b> <c>AuthorizationMiddleware</c> applies the fallback policy to a request that matched
    /// <b>no endpoint</b> as readily as to one that matched an unmarked one, so a <c>POST</c> to a path
    /// this application does not serve is answered 403 to a locked session and 404 to everybody else. A
    /// case asserting the 403 alone is therefore green against an application in which this route was
    /// never mapped. The three statuses separate cleanly: <b>404 means no endpoint, 403 means a policy
    /// refused, and only a status from a handler that ran proves the route exists.</b>
    /// </para>
    /// <para>
    /// <b>A 400 rather than a 204 as that arm, deliberately.</b> The full session quotes a rotation nothing
    /// staged, which is the handler's own refusal — it needs no factors, no staged run and no seals, and
    /// it already establishes everything this case needs: the route is mapped, a full session reaches it,
    /// and the body got as far as a handler. The 204 is the happy path's to own. The <c>errors</c> key is
    /// read for the same reason <c>ResealChunkEndpointTests</c> reads its own: a framework 400 out of the
    /// model binder is also a 400, and only <c>RotationId</c> — PascalCase, the command's own member name,
    /// because nothing configures a <c>DictionaryKeyPolicy</c> and <see cref="ValidationExceptionHandler" />
    /// copies the dictionary verbatim — says the refusal came from a handler that <em>ran</em>.
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
    public async Task CompleteKeyRotation_FromALockedSession_Answers403()
    {
        // Arrange — one host, two accounts, two sessions: one opened by a federated credential, which is
        // the only kind that derives SessionKind.Locked, and one opened by a passkey.
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient locked = await host.Factory.CreateSignedInClientAsync(
            "google-completion-locked", kind: SessionKind.Locked);
        ApiFactory.SignedInClient full = await host.Factory.CreateSignedInClientAsync(
            "google-completion-full", kind: SessionKind.Full);

        // Act — one well-shaped, meaningless body, so the only thing differing between the two requests is
        // the session that carried it.
        HttpResponseMessage refused = await locked.Client.PostAsJsonAsync(
            CompletionPath, CompletionBody(Guid.CreateVersion7()));
        HttpResponseMessage admitted = await full.Client.PostAsJsonAsync(
            CompletionPath, CompletionBody(Guid.CreateVersion7()));

        // Assert — the admitted arm first, so a route that is not mapped at all reads as the 404 it is
        // rather than as a locked session being refused.
        await Assert.That(admitted.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(admitted.StatusCode).IsNotEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // Keyed on the member the caller can correct, which is what says the handler ran rather than the
        // model binder.
        JsonObject errors = (await ReadJsonObjectAsync(admitted))["errors"]!.AsObject();
        await Assert.That(errors.ContainsKey(nameof(CompleteKeyRotationCommand.RotationId))).IsTrue();

        // The refusal, and that it is this gate's refusal rather than the CSRF control's.
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await TitleOfAsync(refused)).IsNotEqualTo(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// A completion quoting a rotation that is not the account's staged one is answered <b>400</b> and
    /// promotes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>400 is the status <c>CompleteKeyRotationHandler</c> produces, read off the handler rather than
    /// assumed.</b> It raises <c>Domain.Common.ValidationException</c> keyed on
    /// <c>nameof(CompleteKeyRotationCommand.RotationId)</c>, and <see cref="ValidationExceptionHandler" />
    /// turns exactly that type into a 400 carrying a <c>ValidationProblemDetails</c>. It is the right
    /// status: the request is correctable by the caller, and the correction is "begin a rotation, then
    /// complete it under the identifier that begin staged".
    /// </para>
    /// <para>
    /// <b>409 was the tempting alternative and would be wrong here.</b> The handler deliberately collapses
    /// "no run is in flight" and "a different run is" into one refusal, precisely so a caller whose
    /// request was already wrong is told nothing about which of the two it was; a conflict would have to
    /// distinguish them to mean anything. The two 409s this route <em>does</em> raise are named in the
    /// negative for that reason.
    /// </para>
    /// <para>
    /// <b>The account holds a real staged run, and the quoted identifier is the only thing wrong.</b> That
    /// is what makes "nothing was promoted" a claim about a refusal rather than about an account with
    /// nothing to promote — twelve factors are standing, twelve seals are staged, and a route that took
    /// the caller's identifier through to the gate instead of the staged one could have destroyed every
    /// one of them on the strength of a run nobody is finishing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CompleteKeyRotation_ForARotationThatIsNotTheStagedOne_Answers400AndPromotesNothing()
    {
        // Arrange — one run staged, and a completion quoting another.
        await using PostgresTestHost host = await StartHostAsync();
        Rotating rotating = await FurnishAccountAsync(host, "google-completion-owner");
        Staged staged = await StageRotationAsync(host, rotating);
        Guid abandoned = Guid.CreateVersion7();
        KeyState before = await SnapshotAsync(host, rotating.UserId);

        // The premises: the two identifiers really differ, and the account really holds one of them.
        await Assert.That(abandoned).IsNotEqualTo(staged.RotationId);
        await Assert.That(await StagedRotationIdAsync(host, rotating.UserId)).IsEqualTo(staged.RotationId);

        // Act
        HttpResponseMessage response = await rotating.Client.PostAsJsonAsync(
            CompletionPath, CompletionBody(abandoned));

        // Assert — refused rather than faulted, and refused rather than accepted.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NoContent);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // Keyed on the member the caller can correct, which is what says the handler ran.
        JsonObject errors = (await ReadJsonObjectAsync(response))["errors"]!.AsObject();
        await Assert.That(errors.ContainsKey(nameof(CompleteKeyRotationCommand.RotationId))).IsTrue();

        // And not one factor and not the manifest moved.
        await Assert.That(Differences(before, await SnapshotAsync(host, rotating.UserId))).IsEmpty();
    }

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
    /// The whole of an account's key material as the database holds it: the manifest's generation, the
    /// manifest's bytes, and one encapsulated value per factor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two binary members are carried as base64url text rather than as <see cref="byte" /><c>[]</c>,
    /// and that is not presentation.</b> A record's generated equality over a byte array is reference
    /// equality, which would report two reads of an unchanged row as different and make every "promoted
    /// nothing" assertion pass for the wrong reason — and TUnit's <c>IsEquivalentTo</c> defaults to
    /// <c>CollectionOrdering.Any</c>, so a permutation of a payload would satisfy a collection comparison.
    /// One string equality can be satisfied neither way, and a failure prints two values a reader can line
    /// up.
    /// </para>
    /// <para>
    /// <b>Sorted, so the dictionary a failure is rendered from reads the same twice.</b> Neither read on
    /// the completion path carries an <c>ORDER BY</c>.
    /// </para>
    /// </remarks>
    private sealed record KeyState(
        int RotationEpoch,
        string Manifest,
        SortedDictionary<Guid, string> FactorKeys);

    /// <summary>
    /// One staged run as this file arranged it: which run, the manifest it staged, and the value each
    /// factor's seal carries.
    /// </summary>
    private sealed record Staged(
        Guid RotationId,
        string Manifest,
        SortedDictionary<Guid, string> SealsByFactor)
    {
        /// <summary>
        /// What the account's key material must read as once this run has been promoted.
        /// </summary>
        /// <remarks>
        /// <b>Built from the values that were staged rather than read back and compared against
        /// itself.</b> A route that promoted "some new bytes" — a zipped neighbour's seal, the same seal
        /// on every row — moves every column and would pass any movement test.
        /// </remarks>
        public KeyState AsPromoted() => new(PromotedRotationEpoch, Manifest, SealsByFactor);
    }

    /// <summary>The body a completion carries: one member, and deliberately nothing else.</summary>
    /// <remarks>
    /// <b>An anonymous object rather than a request record</b>, so nothing here depends on a type the
    /// endpoint has not been written with yet — and so the one wire member is pinned by use. The staged
    /// manifest, the epoch it is filed at and the value each factor adopts are all on file already;
    /// carried again here they would be a second statement of the same values, able to disagree with the
    /// staged one at the one moment a disagreement cannot be undone.
    /// </remarks>
    private static object CompletionBody(Guid rotationId) => new { rotationId };

    /// <summary>
    /// The promoted generation as a header would have to spell it.
    /// </summary>
    /// <remarks>
    /// <see cref="CultureInfo.InvariantCulture" /> because a header is an ASCII wire value and never the
    /// test machine's locale — a culture that groups digits or uses another set of them would make this
    /// sweep silently stop matching the thing it is looking for.
    /// </remarks>
    private static string PromotedRotationEpochText =>
        PromotedRotationEpoch.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Every response header whose value <b>is</b> the promoted generation, named so a failure says which
    /// one carried it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A sweep over the values rather than a check of particular headers, and that is the whole
    /// instrument.</b> The rule is that the number must not reach a client at all, so naming
    /// <c>ETag</c> and <c>Location</c> and stopping there would leave the next header somebody adds
    /// outside it — and the reasoning that gets written beside such a header is always "this is not one
    /// of the ones the test names".
    /// </para>
    /// <para>
    /// <b>Content headers are swept beside the response's own.</b> A 204 carries no body, but
    /// <c>Content-*</c> is where a value would sit if somebody reached for <c>Content-Location</c> or an
    /// entity tag on the content rather than on the message.
    /// </para>
    /// <para>
    /// <b>Whole values, never substrings.</b> A <c>Date</c> header contains the digits of almost any small
    /// number, so a containment test would report every response ever sent; equality after unframing is
    /// what makes a report mean something.
    /// </para>
    /// </remarks>
    private static string[] EpochCarriersIn(HttpResponseMessage response) =>
    [
        .. response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(header => header.Value.Select(value => (header.Key, Value: Unframed(value))))
            .Where(header => string.Equals(
                header.Value, PromotedRotationEpochText, StringComparison.Ordinal))
            .Select(header => $"{header.Key}: {header.Value}"),
    ];

    /// <summary>
    /// One header value with the framing an entity tag wears taken off it.
    /// </summary>
    /// <remarks>
    /// <b>Without this the sweep is defeated by the single most likely carrier.</b> An <c>ETag</c> is
    /// quoted by the specification and may be weak, so a route publishing the generation as one writes
    /// <c>W/"2"</c> or <c>"2"</c> — neither of which equals <c>2</c>, and both of which are the epoch in a
    /// client's hands.
    /// </remarks>
    private static string Unframed(string value)
    {
        string trimmed = value.Trim();

        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Trim('"').Trim();
    }

    /// <summary>
    /// A response that really does carry the promoted generation, twice and two ways — the control that
    /// keeps <see cref="EpochCarriersIn" /> from passing by reading nothing.
    /// </summary>
    /// <remarks>
    /// <b>Built here rather than taken from the application</b>, so the control is a statement about the
    /// sweep rather than about whatever headers a test server happens to attach to a 204 — which is a
    /// thing that varies with the host and would make this control an assertion about
    /// <c>Microsoft.AspNetCore.TestHost</c>. The two carriers are the bare header and the quoted entity
    /// tag, which are the two shapes <see cref="Unframed" /> exists for.
    /// </remarks>
    private static HttpResponseMessage ProbeCarryingTheEpoch()
    {
        HttpResponseMessage probe = new(HttpStatusCode.NoContent)
        {
            Content = new StringContent(string.Empty),
        };

        probe.Headers.TryAddWithoutValidation(
            "X-Budgetoid-Rotation-Epoch", PromotedRotationEpochText);
        probe.Headers.ETag = new EntityTagHeaderValue($"\"{PromotedRotationEpochText}\"");

        return probe;
    }

    /// <summary>
    /// Every way <paramref name="actual" /> departs from <paramref name="expected" />, as sentences a
    /// reader can line up against the request.
    /// </summary>
    /// <remarks>
    /// <b>Offenders collected rather than asserted one at a time</b>, so a failure names every factor that
    /// did not move instead of the first — and asserted on as a <em>collection</em>, because a TUnit
    /// string assertion truncates and a census reporting through <c>string.Join</c> names only its first
    /// offender.
    /// </remarks>
    private static string[] Differences(KeyState expected, KeyState actual)
    {
        List<string> offenders = [];

        if (expected.RotationEpoch != actual.RotationEpoch)
        {
            offenders.Add(
                $"rotation_epoch: expected {expected.RotationEpoch}, found {actual.RotationEpoch}");
        }

        if (!string.Equals(expected.Manifest, actual.Manifest, StringComparison.Ordinal))
        {
            offenders.Add($"manifest: expected {expected.Manifest}, found {actual.Manifest}");
        }

        foreach ((Guid factorId, string keys) in expected.FactorKeys)
        {
            if (!actual.FactorKeys.TryGetValue(factorId, out string? now))
            {
                offenders.Add($"{factorId}: no wrapped_account_keys row");
            }
            else if (!string.Equals(keys, now, StringComparison.Ordinal))
            {
                offenders.Add($"{factorId}: expected {keys}, found {now}");
            }
        }

        offenders.AddRange(actual.FactorKeys.Keys
            .Where(factorId => !expected.FactorKeys.ContainsKey(factorId))
            .Select(factorId => $"{factorId}: a factor this account was not expected to hold"));

        return [.. offenders];
    }

    /// <summary>
    /// Seeds a signed-in account and hangs twelve factors off it, under three credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two passkeys and a card of ten codes.</b> The recovery-codes credential is added here rather
    /// than through a seeder because ten factors hang off one row of <c>credentials</c> —
    /// <c>SeedPasskeyAsync</c> has no equivalent, and a set is the arrangement this file is about.
    /// </para>
    /// <para>
    /// <b>The session's own passkey is read back before the second one is filed.</b>
    /// <c>CreateSignedInClientAsync</c> writes a passkey to open a full session and hands back no
    /// credential id, so the lookup has to happen while the account still holds exactly one — a
    /// <c>Single</c> afterwards would match two rows and a <c>First</c> would pick one by coin flip.
    /// </para>
    /// <para>
    /// <b>The session is seeded rather than established through a ceremony</b>, because a completion needs
    /// no assertion — its whole point is that it does not.
    /// </para>
    /// </remarks>
    private static async Task<Rotating> FurnishAccountAsync(PostgresTestHost host, string subject)
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
        await AddFactorAsync(host, sessionPasskeyId, factorIds);
        await AddFactorAsync(host, secondPasskeyId, factorIds);

        foreach (int _ in Enumerable.Range(0, RecoveryCodeSetSize))
        {
            await AddFactorAsync(host, recoveryCodesId, factorIds);
        }

        return new Rotating(signedIn, factorIds);
    }

    /// <summary>
    /// Files one more factor against <paramref name="credentialId" />, carrying a payload nothing else in
    /// this file carries.
    /// </summary>
    /// <remarks>
    /// The filler counts up with the factor's position, because the default seeding writes the same value
    /// on every row — so a promotion that returned row A's payload for row B would be invisible on a
    /// defaulted account and is visible here. <see cref="RepositoryTestHost.SeedWrappedAccountKeysAsync" />
    /// says so in its own remarks.
    /// </remarks>
    private static async Task AddFactorAsync(
        PostgresTestHost host,
        Guid credentialId,
        List<Guid> factorIds)
    {
        Guid factorId = await RepositoryTestHost.SeedWrappedAccountKeysOnAsync(
            host.ConnectionString,
            credentialId,
            Guid.CreateVersion7(),
            encapsulatedAccountKeys: RepositoryTestHost.EncapsulatedAccountKeysPayload(
                (byte)(StoredFillerBase + factorIds.Count)));

        factorIds.Add(factorId);
    }

    /// <summary>
    /// Stages one rotation for <paramref name="rotating" />, with one seal for every factor the account
    /// holds, and returns what a completion has to promote.
    /// </summary>
    /// <remarks>
    /// <b>Through <see cref="KeyRotation.Begin" /> and the real adapter over the account's loaded rows</b>
    /// rather than an <c>insert</c>, the idiom <c>KeyRotationCompletionTests</c> keeps: the factory
    /// refuses an absent or over-wide manifest, an epoch below the floor, an empty identifier and a
    /// credential that is not a passkey, and <see cref="KeyRotationSeal.For" /> reads the owner off both
    /// the rotation and the loaded <c>wrapped_account_keys</c> row and refuses when they disagree — so a
    /// staged run here is one a begin could really have written.
    /// </remarks>
    private static async Task<Staged> StageRotationAsync(PostgresTestHost host, Rotating rotating)
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
                (byte)(StagedFillerBase + index));

            sealsByFactor[factorId] = Base64UrlText.Encode(payload);
            seals.Add(KeyRotationSeal.For(rotation, factors[factorId], payload));
        }

        await repository.StageAsync(rotation, seals);

        return new Staged(rotation.RotationId, Base64UrlText.Encode(manifest), sealsByFactor);
    }

    /// <summary>
    /// Files one narrative row no chunk has been sent for, so the completeness gate has something to
    /// report outstanding.
    /// </summary>
    /// <remarks>
    /// <b>A payee, because its whole narrative is a <em>required</em> name.</b> The gate is presence-aware
    /// on the two nullable columns that are the whole of their row's narrative — <c>budgets.name</c> and
    /// <c>transactions.description</c> — so a row of either kind could be outstanding for a reason the
    /// presence test owns rather than for the reason this case is about. A payee owes a stamp
    /// unconditionally.
    /// </remarks>
    private static async Task<Guid> SeedUnstampedPayeeAsync(PostgresTestHost host, Rotating rotating)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options,
            new TestBudgetContext(rotating.BudgetId));

        Payee payee = Payee.Create(
            Guid.CreateVersion7(),
            rotating.BudgetId,
            SealedNarrative.Indexed(OutstandingPayeeLabel),
            SeedInstant);
        db.Payees.Add(payee);
        await db.SaveChangesAsync();

        return payee.Id;
    }

    /// <summary>The stamp one payee carries, read on a context that did not write it.</summary>
    private static async Task<Guid?> StampOfPayeeAsync(
        PostgresTestHost host,
        Rotating rotating,
        Guid payeeId)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options,
            new TestBudgetContext(rotating.BudgetId));

        return (await db.Payees.SingleAsync(payee => payee.Id == payeeId)).RotationId;
    }

    /// <summary>
    /// The whole of one account's key material, read on the container superuser connection through a
    /// context that did not write a byte of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A context of this file's own, and substituting the application's would retire every assertion
    /// built on it.</b> EF answers a second read of a row it is tracking with the instance it already
    /// holds — and the act here runs inside the API host, over a context this file cannot reach anyway, so
    /// the separation is structural rather than a discipline. What it buys is that every comparison below
    /// is a statement about PostgreSQL.
    /// </para>
    /// <para>
    /// <b>On the superuser connection, because half of what is asserted is that a column did NOT move.</b>
    /// A policed connection reports a row it cannot see exactly as it reports one that did not change.
    /// </para>
    /// </remarks>
    private static async Task<KeyState> SnapshotAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);

        FactorManifest manifest = await db.FactorManifests
            .SingleAsync(row => row.UserId == userId);

        SortedDictionary<Guid, string> factorKeys = [];

        foreach (WrappedAccountKeys keys in await db.WrappedAccountKeys
                     .Where(row => row.UserId == userId)
                     .ToListAsync())
        {
            factorKeys[keys.FactorId] = Base64UrlText.Encode(keys.EncapsulatedAccountKeys.ToArray());
        }

        return new KeyState(
            manifest.RotationEpoch,
            Base64UrlText.Encode(manifest.Manifest.ToArray()),
            factorKeys);
    }

    /// <summary>The account's one staged rotation identifier, or a failure saying how many there were.</summary>
    /// <remarks>
    /// Sole rather than first: <c>user_id</c> is the primary key of <c>key_rotations</c>, so a second row
    /// is a database that has lost the rule making two concurrent runs unstorable — not a row to choose
    /// between.
    /// </remarks>
    private static async Task<Guid> StagedRotationIdAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);

        return (await db.KeyRotations.SingleAsync(rotation => rotation.UserId == userId)).RotationId;
    }

    /// <summary>How many staging rows the account holds — one before a completion and one after.</summary>
    private static async Task<int> StagedRotationCountAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);

        return await db.KeyRotations.CountAsync(rotation => rotation.UserId == userId);
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
    /// No ambient budget, which is safe because nothing read through it carries the
    /// <c>BudgetIsolation</c> filter — every table on the completion path is policed on the user instead.
    /// The two members that touch <c>payees</c> build a context of their own with a budget on it.
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
    /// No <c>repointsProviderSchemeToTestHandler</c>, because nothing here drives a registration ceremony
    /// — see this class's remarks for why a completion suite stages its rotations rather than beginning
    /// them.
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
