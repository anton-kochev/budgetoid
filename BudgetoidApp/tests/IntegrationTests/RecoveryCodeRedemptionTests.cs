using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Signing in with a recovery code over real HTTP: what one code buys, what it costs the account, what a
/// second presentation of the same code gets, and what every refusal looks like.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server never sees a code.</b> The browser mints one, derives a verifier <c>V = HKDF(code, …)</c>
/// of exactly 32 bytes and sends only <c>V</c> as base64url; the row holds <c>SHA-256(V)</c>. So a
/// redemption hashes what it was handed and looks the row up by that hash — there is no account named in
/// the request, no credential id, and nothing else to scope the lookup by. That is what
/// <c>recovery_code_hashes</c> is exempt from row-level security for (ADR 0016), and it is why the two
/// orderings below are the whole security property rather than implementation detail.
/// </para>
/// <para>
/// <b>Every set here is issued through the real route.</b> <c>POST /api/me/recovery-codes</c> exists
/// today, so each test runs a passkey registration, a re-authentication ceremony and a generation before
/// it redeems anything. Seeding <c>recovery_code_hashes</c> directly would be shorter and would prove
/// less: what makes a redemption meaningful is that it matches a row this product wrote, under the
/// credential this product filed it against. Every ceremony reports a signature counter of zero, which
/// is what an authenticator backing a synced passkey does, so one device proves presence as many times
/// as a test needs.
/// </para>
/// <para>
/// <b><see cref="Redemption_WithAValidCode_OpensAFullSessionAndReportsWhatIsLeft" /> is the control for
/// the whole file, and it is also the control for an ordering no test here names directly.</b> The
/// identity has to be published <em>before</em> the transaction opens: opening a transaction opens a
/// connection, and that is when <c>SessionContextInterceptor</c> writes <c>app.current_user_id</c>.
/// Opened first, the setting reaches the database as <c>''</c> and every policed statement inside fails
/// with <c>22P02</c> — and <c>sessions</c> is policed by <c>user_isolation</c>, so the session insert is
/// exactly such a statement. Swap the two lines in the handler and that test goes red with a 500. It is
/// load-bearing for that reason as much as for the happy path, and a reader tidying the handler will not
/// know unless it is written down here.
/// </para>
/// <para>
/// <b><see cref="Redemption_WithTheSameCodeTwice_IsRefusedTheSecondTime" /> holds one redemption per
/// code, and it holds that by assertion rather than by ordering.</b> What it shows is that the
/// <c>DELETE</c> really committed, that the replay is turned away, and that the account is left with one
/// session and the nine codes it should still have. It is <em>not</em> what holds FR-054's ordering:
/// consume-before-establish and establish-before-consume both leave the same committed state behind
/// here, because the two writes share one transaction and the second request arrives after it commits.
/// The ordering is held by
/// <c>RedeemRecoveryCodeHandlerTests.HandleAsync_WhenTheCodeIsSpentByAConcurrentRedemption_RefusesAndOpensNoSession</c>,
/// which is the only arrangement in either suite where the consume can fail with the session write
/// already behind it — see the remarks on that test for why the asymmetry is worth a rule.
/// </para>
/// <para>
/// <b>Every row is counted on <see cref="PostgresTestHost.ConnectionString" /></b> — the container
/// superuser — and never on the application role. <c>sessions</c> carries <c>user_isolation</c>, which is
/// <c>FOR ALL</c>, so a policed connection reports zero rows for a session that is there exactly as it
/// does for one that is not.
/// </para>
/// <para>
/// No test here names a production type belonging to this story. They address the route over HTTP and
/// read the wire body, so while the endpoint is unmapped they fail on a status assertion against a real
/// 404 rather than failing to compile — the difference between a red test that is telling us something
/// and one that is telling us nothing.
/// </para>
/// </remarks>
public sealed class RecoveryCodeRedemptionTests
{
    /// <summary>
    /// The route under test.
    /// </summary>
    /// <remarks>
    /// <b>Not under <c>/api/me</c></b>, which is the current principal's namespace and this request has
    /// no principal — it names nobody, and the account it lands on is discovered from the code. And
    /// <b>not under <c>/api/passkeys</c></b>, which is the two WebAuthn ceremonies and this is neither.
    /// A sub-resource rather than a verb: the thing being created is a redemption of the account's set.
    /// </remarks>
    private const string RedemptionPath = "/api/recovery-codes/redemption";

    private const string RecoveryCodesPath = "/api/me/recovery-codes";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";

    /// <summary>The account under test in most of the file.</summary>
    private const string Subject = "google-redeeming";

    /// <summary>
    /// The second account: the one whose set makes "this code's owner" measurable against "the only set
    /// in the table", and whose bearer token is the one presented in the wrong-account test.
    /// </summary>
    private const string OtherSubject = "google-redeeming-bystander";

    /// <summary>
    /// How many codes an issued set holds. Restated rather than read off the handler, because the number
    /// is the pin: a test taking its expectation from the type under test agrees with whatever that type
    /// later decides.
    /// </summary>
    private const int RequiredCodeCount = 10;

    /// <summary>
    /// The exact width of a verifier, decoded — <c>RecoveryCodeHash.VerifierLength</c>, restated for the
    /// same reason.
    /// </summary>
    private const int VerifierLength = 32;

    /// <summary>
    /// The one member of the request body.
    /// </summary>
    /// <remarks>
    /// One member and no second one. There is nothing else a redemption may carry: an account id, an
    /// email or a credential id would each be a value the server would have to either ignore or trust,
    /// and trusting one would let an anonymous caller name the account a code is matched against.
    /// </remarks>
    private const string VerifierMember = "verifier";

    /// <summary>The kind of session a set of recovery codes opens, as the column spells it.</summary>
    private const string FullSessionKind = "full";

    private const string KindMember = "kind";
    private const string ExpiresMember = "expiresAtUtc";
    private const string RemainingMember = "remaining";

    /// <summary>
    /// The members of a successful redemption, joined exactly as
    /// <see cref="Redemption_WithAValidCode_OpensAFullSessionAndReportsWhatIsLeft" /> builds them.
    /// </summary>
    /// <remarks>
    /// Ordinal order, which is the order that assertion sorts the observed members into, so a second
    /// member arriving is a comparison of two strings that names it rather than of two numbers.
    /// </remarks>
    private const string RedemptionMembers = $"{ExpiresMember}, {KindMember}, {RemainingMember}";

    /// <summary>
    /// The member of a problem-details body that is a new value on every request rather than on every
    /// cause, and therefore the one member a comparison of two refusals must not compare.
    /// </summary>
    private const string TraceIdMember = "traceId";

    /// <summary>
    /// The title a 401 carries when nothing chose it — <c>UseStatusCodePages</c> writing a problem
    /// document from the status code alone, with no handler behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every refusal assertion in this file has to rule this value out, and the reason is a false
    /// green this file already produced.</b> ASP.NET Core applies the fallback authorization policy to
    /// requests that match <em>no endpoint at all</em>, so while this route is unmapped an anonymous
    /// POST to it is answered 401 by the pipeline rather than 404 — which means "refused, and nothing was
    /// written" is satisfied perfectly by a route that does not exist.
    /// <see cref="Redemption_WhenRefused_WritesNoSessionAndSpendsNoCode" /> passed on exactly that before
    /// this constant existed.
    /// </para>
    /// <para>
    /// Ruling it out is also the positive claim about the response: this route answers with a sentence of
    /// its own, written by a handler that decided to refuse.
    /// </para>
    /// </remarks>
    private const string StatusCodeOnlyTitle = "Unauthorized";

    /// <summary>
    /// How many refusals <see cref="EveryReachableRedemptionRefusal_ProducesTheIdenticalResponse" />
    /// drives. Named so that deleting one from the list is a failing test rather than a shorter and still
    /// perfectly green one.
    /// </summary>
    private const int ReachableRedemptionRefusals = 7;

    /// <summary>
    /// The response headers whose value is a new one on every <b>request</b> rather than on every
    /// <b>cause</b>, and which a comparison of two refusals must therefore not compare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The names are still compared — <see cref="ComparableHeadersOf" /> replaces the value and keeps the
    /// header — so one of these appearing on one refusal and not on another still fails.
    /// </para>
    /// <para>
    /// Nothing in the in-memory pipeline emits most of them today. They are listed because a header that
    /// starts being emitted later would otherwise turn a true assertion into a permanently red one, and
    /// the cure a hurried reader reaches for is deleting the header comparison rather than adding a name
    /// here.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> PerRequestHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Date", "Server", "traceparent", "tracestate", "Request-Id" };

    /// <summary>
    /// A valid code opens a full session, says how many are left, and leaves the row behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the control for every refusal in the file.</b> A route that was never mapped refuses
    /// every caller and satisfies all of them; a route whose lookup can never match satisfies all of them
    /// while the feature does not exist. This is the test that says the door opens for somebody.
    /// </para>
    /// <para>
    /// <b>It is also the control for the identity-before-transaction ordering</b> described in the class
    /// remarks: the session insert is a policed statement, so a handler that opened its transaction
    /// before publishing the account fails here with <c>22P02</c> and a 500. Nothing else in this file
    /// would notice, because every other test either expects a refusal or would read that 500 as one more
    /// way of saying no.
    /// </para>
    /// <para>
    /// <b>The response is asserted member by member and then as a whole set.</b> The kind is the fact
    /// that matters — how much of the account this sign-in reaches — and the remaining count is what the
    /// screen has to render next. What may never appear is the session's id: returning it would hand the
    /// client a stable handle to a session, and the likeliest way this design is broken later is somebody
    /// deciding that handle is close enough to a token to start accepting it. Asserted against the
    /// payload as it went over the wire rather than against a parsed document, because a value the
    /// serializer escaped, or one folded into a member added later, reads as absent through a parse.
    /// </para>
    /// <para>
    /// <b>The consumed row is identified by value, not by count.</b> Nine rows and the nine
    /// <em>correct</em> rows are the same number, and a handler that deleted an arbitrary row of the set —
    /// or the whole set bar one — leaves the same count behind while invalidating codes the person is
    /// still holding.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Redemption_WithAValidCode_OpensAFullSessionAndReportsWhatIsLeft()
    {
        // Arrange — a real account, a real passkey, a real set issued through the real route.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        string[] verifiers = Verifiers();
        await IssueSetAsync(client, device, userId, verifiers);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setId = await ResolveSetCredentialIdAsync(admin, userId);

        // No session before the act, or the row counted afterwards is one the arrangement produced.
        await Assert.That((await ReadSessionsAsync(admin)).Count).IsEqualTo(0);

        // Act — a client carrying no token at all, which is the state a recovery sign-in arrives in.
        HttpResponseMessage response = await RedeemAsync(host.Factory.CreateClient(), verifiers[0]);

        // Assert — the status first, so a body missing because the request was refused reads as the
        // refusal it is rather than as a member list nobody would recognise as a 401.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        JsonObject body = JsonNode.Parse(payload)!.AsObject();

        // Ordered before joining, so a second member produces the same message whichever order the
        // serializer emitted it in — a red that reads differently between runs is a red people stop
        // trusting. Never ContainsKey: a containment check over member names can never fail, because
        // every widening leaves the three below present and the check green.
        string members = string.Join(", ", body.Select(member => member.Key).Order(StringComparer.Ordinal));
        await Assert.That(members).IsEqualTo(RedemptionMembers);

        await Assert.That(body[KindMember]!.GetValue<string>()).IsEqualTo(FullSessionKind);
        await Assert.That(body[RemainingMember]!.GetValue<int>()).IsEqualTo(RequiredCodeCount - 1);
        await Assert.That(ExpiryOf(body)).IsGreaterThan(DateTime.UtcNow);

        // One session, on the account the code belongs to, opened by the set's own credential — which is
        // what CK_sessions_kind_matches_credential makes 'full' checkable against.
        IReadOnlyList<SessionRow> sessions = await ReadSessionsAsync(admin);
        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].UserId).IsEqualTo(userId);
        await Assert.That(sessions[0].CredentialId).IsEqualTo(setId);
        await Assert.That(sessions[0].Kind).IsEqualTo(FullSessionKind);

        // And no handle to it left the server, in either spelling of a Guid.
        await Assert.That(payload).DoesNotContain(sessions[0].Id.ToString("D"));
        await Assert.That(payload).DoesNotContain(sessions[0].Id.ToString("N"));

        // Exactly the presented code is gone, and the other nine are the nine the client still holds.
        await Assert.That(await StoredHashesAsync(admin, userId))
            .IsEquivalentTo(ExpectedHashesOf(verifiers.Skip(1)));
    }

    /// <summary>
    /// The same code presented twice is refused the second time, and the account is left with one session
    /// rather than two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this holds is that a code is spent once: the <c>DELETE</c> committed, the replay is turned
    /// away, and the account keeps exactly the nine codes it should.</b> A handler that verified against
    /// the row and left it in place — or removed it on a save this transaction later rolled back — opens a
    /// second session here, and a recovery code is a full-session credential, so that is an unbounded
    /// number of sign-ins from one intercepted value.
    /// </para>
    /// <para>
    /// <b>It is deliberately not the test that holds FR-054's ordering, and the docstring that said so was
    /// wrong.</b> The handler consumes the code and then establishes the session; reverse those two lines
    /// and this test stays green. Both writes live in one transaction, so the first request commits the
    /// same rows under either ordering, and the second request — sequential, on a fresh client, arriving
    /// long after that commit — dies at the discovery read, which is the line before either of them. The
    /// arrangement that can tell the orderings apart is one where the consume fails with the session write
    /// already behind it, which needs two redemptions in flight at once over a store that is not unwinding
    /// them for us:
    /// <c>RedeemRecoveryCodeHandlerTests.HandleAsync_WhenTheCodeIsSpentByAConcurrentRedemption_RefusesAndOpensNoSession</c>.
    /// A reader who believes a false claim about which line a test defends is a reader who deletes the
    /// line.
    /// </para>
    /// <para>
    /// <b>Both halves are asserted, and the session count is the half that carries the weight.</b> The
    /// 401 alone is also what a handler answers when it consumed the code correctly <em>and</em> when it
    /// consumed nothing but happens to refuse replays some other way; the count of sessions afterwards is
    /// what says the second request did not get what it came for. The remaining hashes are compared by
    /// value for the same reason: a second consumption would take a different row and leave the same
    /// count of eight… nine behind is only the right answer if it is the right nine.
    /// </para>
    /// <para>
    /// The second request is sent on a <em>new</em> anonymous client, so nothing about the first response
    /// — a cookie, a header the framework attached — can be what the second is refused for. Nothing sets
    /// one today; the point is that this test keeps saying the same thing if something ever does.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Redemption_WithTheSameCodeTwice_IsRefusedTheSecondTime()
    {
        // Arrange — an account, a set, and one code already spent through the real route.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        string[] verifiers = Verifiers();
        await IssueSetAsync(client, device, userId, verifiers);

        HttpResponseMessage first = await RedeemAsync(host.Factory.CreateClient(), verifiers[0]);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That((await ReadSessionsAsync(admin)).Count).IsEqualTo(1);

        // Act — the same value again, on a client that knows nothing about the first exchange.
        HttpResponseMessage second = await RedeemAsync(host.Factory.CreateClient(), verifiers[0]);

        // Assert
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // Still one session, so the replay opened nothing.
        await Assert.That((await ReadSessionsAsync(admin)).Count).IsEqualTo(1);

        // And it took nothing else with it: the nine live codes are the nine the client still holds.
        await Assert.That(await StoredHashesAsync(admin, userId))
            .IsEquivalentTo(ExpectedHashesOf(verifiers.Skip(1)));
    }

    /// <summary>
    /// Every refusal this route can produce, driven end to end and compared whole — status, headers and
    /// body together, and all of them against each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A caller able to tell one refusal from another on this route is a caller learning what is
    /// stored.</b> "No such code" told apart from "that code was already used" says a value the caller
    /// presented was once real, which is exactly what somebody working through a partially-observed
    /// recovery card wants to know; told apart from "that was not base64url" it says the same thing about
    /// the encoding, and told apart from "too long" it hands out the width of a verifier for free. They
    /// all leave as one status, one set of headers and one body.
    /// </para>
    /// <para>
    /// <b>The headers are compared because a difference does not have to be in the body to be read.</b>
    /// A <c>WWW-Authenticate</c> challenge added to one arm, a <c>Content-Length</c> that moves with a
    /// sentence somebody lengthened, a caching or correlation header attached by one branch and not
    /// another: each is a distinguisher a status-and-body comparison waves through, and each is
    /// scriptable. <see cref="PerRequestHeaders" /> holds the names whose <em>values</em> vary per
    /// request; their names are still compared.
    /// </para>
    /// <para>
    /// <b>Two of these entries are the same line of the handler, and the list says so rather than
    /// implying otherwise.</b> "Unknown verifier" and "already redeemed" both fall out of the discovery
    /// read returning nothing — a spent code was deleted, so there is no second branch for it to take, and
    /// no assertion here could tell the two apart even if there were. The entry is kept because the
    /// <em>caller's</em> two cases are genuinely different — one value was never real, the other was real
    /// a moment ago — and it is that difference the response may not carry; it is not kept because it
    /// covers a distinct step.
    /// </para>
    /// <para>
    /// <b>The one entry that can reach further is the loser of a real race</b>, and it is the only refusal
    /// this route offers that is reachable over HTTP past the discovery read. Two redemptions of one code
    /// in flight together: the loser either finds the row gone on the re-read inside its transaction, or
    /// finds it, blocks on the winner's lock and meets a <c>DELETE</c> that matches nothing — which is
    /// EF's <c>DbUpdateConcurrencyException</c>, and which <c>RecoveryCodeRepository.ConsumeAsync</c>
    /// translates into this same refusal. Which of the two it lands on is the scheduler's to decide, so
    /// this entry does not <em>guarantee</em> it exercised the deeper arm; what it does guarantee is that
    /// every arm it can land on answers identically. Left untranslated, that path is a 500 with a stack
    /// trace — an oracle saying the value presented was real — which is what this entry would then report
    /// as a difference.
    /// </para>
    /// <para>
    /// <b>Compared to each other rather than to a literal</b>, because the property is
    /// indistinguishability rather than any particular sentence. Two refusals compared against each other
    /// prove only that those two agree, so this drives all of them and asserts they collapse to a single
    /// value.
    /// </para>
    /// <para>
    /// <b>The one value they collapse to is asserted to be a 401 that is not the passkey sentence</b>,
    /// and both halves are deliberate. Without the status, a route answering every one of these
    /// identically for a reason unrelated to the feature — 404 on an unmapped path, 405 on a wrong verb —
    /// satisfies the comparison and nothing else. And
    /// <see cref="PasskeyVerificationExceptionHandler.Title" /> is refused by name because "The passkey
    /// could not be verified." on a recovery-code route is a <em>wrong</em> sentence, which is worse than
    /// a distinguishable one: it tells the person holding a card that the thing they do not have is the
    /// thing that failed. Reusing that exception is the shortest path to green for everything else here,
    /// so it is closed explicitly.
    /// </para>
    /// <para>
    /// <b>The oversized entry is refused before anything is decoded</b>, on a ceiling in the shape
    /// <c>PasskeyPayloadLimits</c> uses — and unlike those, this bound is exact rather than generous,
    /// because a verifier is a fixed 32 bytes. Nothing observable distinguishes "refused at the ceiling"
    /// from "refused at the lookup", which is the point; what the entry pins is that a caller cannot name
    /// how much memory a refusal costs on an anonymous route.
    /// </para>
    /// <para>
    /// <b>The empty body is an entry rather than a framework 400</b>, for the reason the generation
    /// route's request record states: marking the member <c>required</c> would buy a 400 that tells a
    /// caller the server has an opinion about the member's shape before it has refused them, and it would
    /// be a second answer this route can give. A body of <c>{}</c> binds the member to null and lands on
    /// the same refusal as everything else. What this does <em>not</em> cover is no body at all, a literal
    /// <c>null</c>, or a member of the wrong JSON type: those are deserialization failures raised before
    /// the handler is entered, and the gap is accepted for the reason the erasure states — a failure to
    /// deserialize is a fact about the caller's own request and says nothing about what is stored.
    /// </para>
    /// <para>
    /// <b>What this list cannot show is how deep an entry got.</b> Nothing here can observe which step
    /// refused a request — that indistinguishability is the property being asserted — so an entry that
    /// quietly began failing at an earlier step than the one it was written for stays green and stops
    /// covering the step it was added for.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryReachableRedemptionRefusal_ProducesTheIdenticalResponse()
    {
        // Arrange — an account holding a real set, and one code of it already spent so the
        // already-redeemed arm is a value that really was live a moment ago.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        string[] verifiers = Verifiers();
        await IssueSetAsync(client, device, userId, verifiers);

        HttpClient anonymous = host.Factory.CreateClient();
        await Assert.That((await RedeemAsync(anonymous, verifiers[0])).StatusCode).IsEqualTo(HttpStatusCode.OK);

        // A second code, spent by two requests racing for it. Exactly one can win: the row is removed
        // inside a transaction, so the other either reads it gone or blocks on the lock and deletes
        // nothing. The loser's response is one of the refusals compared below.
        HttpResponseMessage[] racing = await Task.WhenAll(
            RedeemAsync(host.Factory.CreateClient(), verifiers[1]),
            RedeemAsync(host.Factory.CreateClient(), verifiers[1]));

        await Assert.That(racing.Count(response => response.StatusCode == HttpStatusCode.OK)).IsEqualTo(1);
        HttpResponseMessage lostTheRace = racing.Single(response => response.StatusCode != HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        string[] survivingHashes = await StoredHashesAsync(admin, userId);
        await Assert.That(survivingHashes.Length).IsEqualTo(RequiredCodeCount - 2);

        // Act
        List<(string Reason, HttpResponseMessage Response)> refusals = [];

        // No member at all. The record declares nothing required, so this binds to null and the handler's
        // own decode is the first thing to see it.
        refusals.Add(("no verifier member", await anonymous.PostAsJsonAsync(RedemptionPath, new { })));

        // A well-formed verifier of the right width that names no row. The arm every wrong guess lands
        // on, and the one an enumeration attempt would be built out of.
        refusals.Add(("unknown verifier", await RedeemAsync(anonymous, Verifiers(1)[0])));

        // The code spent above: once real, now gone. The SAME LINE of the handler as the entry above —
        // a spent code was deleted, so the discovery read finds nothing for either — and it is here
        // because the two are different facts about the CALLER, not because they are different steps.
        refusals.Add(("already redeemed", await RedeemAsync(anonymous, verifiers[0])));

        // The loser of the race arranged above: the one refusal reachable from outside that gets past the
        // discovery read at all, on whichever of the two deeper arms the scheduler handed it.
        refusals.Add(("lost the race", lostTheRace));

        // Standard base64's two extra characters, which base64url replaces with '-' and '_', in a value
        // that is otherwise a perfectly good encoding of the right width — so this is refused for its
        // alphabet and not for its length.
        refusals.Add(("not base64url", await RedeemAsync(anonymous, NotBase64Url())));

        // Short: a real base64url encoding of fewer bytes than a verifier is. It decodes, and then it is
        // the wrong width, which is a different step from the one above and the same answer.
        refusals.Add((
            "short verifier",
            await RedeemAsync(anonymous, Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength - 1)))));

        // Oversized, and far enough over that no ceiling worth the name admits it. Refused before the
        // decode, which nothing here can see and nothing here should be able to.
        refusals.Add((
            "oversized verifier",
            await RedeemAsync(anonymous, Base64UrlText.Encode(RandomNumberGenerator.GetBytes(4096)))));

        // Status, headers and body as one string per refusal, so a difference anywhere in the response is
        // a difference in the comparison rather than in a part of it nobody compared.
        List<(string Reason, string Response)> observed = [];
        foreach ((string reason, HttpResponseMessage response) in refusals)
        {
            string headers = ComparableHeadersOf(response);
            observed.Add((reason, $"{(int)response.StatusCode} [{headers}] {await ReadComparableBodyAsync(response)}"));
        }

        // Assert — each refusal against the first, with its own name on both sides of the comparison so a
        // failure says which one drifted rather than only that something did.
        string first = observed[0].Response;
        foreach ((string reason, string response) in observed)
        {
            await Assert.That($"{reason} => {response}").IsEqualTo($"{reason} => {first}");
        }

        await Assert.That(observed.Count).IsEqualTo(ReachableRedemptionRefusals);
        await Assert.That(observed.Select(entry => entry.Response).Distinct().Count()).IsEqualTo(1);

        // The headers really were part of that comparison. Without this the header half is satisfied by a
        // pipeline that emits none at all, or by a helper that quietly reads an empty collection — and a
        // comparison of two empty strings agrees about nothing.
        await Assert.That(ComparableHeadersOf(refusals[0].Response)).Contains("Content-Type: ");

        // The one value they collapse to, and the two sentences it may not be: the passkey handler's,
        // and the one the pipeline writes when no handler chose the status at all.
        await Assert.That(refusals[0].Response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(first.Contains(PasskeyVerificationExceptionHandler.Title, StringComparison.Ordinal))
            .IsFalse();
        await Assert.That(first.Contains($"\"{StatusCodeOnlyTitle}\"", StringComparison.Ordinal)).IsFalse();

        // And not one of them spent a code on the way to saying no.
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(survivingHashes);
    }

    /// <summary>
    /// A refused redemption writes no session and spends no code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stated on its own because the status cannot see it.</b> A 401 is what a handler answers when it
    /// refused before touching anything, and also what one answers when it established a session, then
    /// noticed the verifier did not match and returned the refusal anyway — the shape a handler acquires
    /// the day somebody moves the session insert above the lookup "so the happy path reads better". The
    /// row count is the only thing that distinguishes them.
    /// </para>
    /// <para>
    /// <b>The account really holds a set</b>, so "no code was spent" is a claim about ten live rows
    /// surviving rather than about an empty table, and the ten are compared by value: a handler that
    /// consumed the nearest row it could find while refusing the request leaves ten… nine rows and a
    /// person one code poorer for a request that failed.
    /// </para>
    /// <para>
    /// The control is <see cref="Redemption_WithAValidCode_OpensAFullSessionAndReportsWhatIsLeft" />: it
    /// is what says a session can be written here at all, without which "no session was written" is a
    /// claim about a route that never writes one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Redemption_WhenRefused_WritesNoSessionAndSpendsNoCode()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        string[] verifiers = Verifiers();
        await IssueSetAsync(client, device, userId, verifiers);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — a verifier of exactly the right shape that names no row.
        HttpResponseMessage response = await RedeemAsync(host.Factory.CreateClient(), Verifiers(1)[0]);

        // Assert — the status, and then that something chose it. Both are needed: see
        // StatusCodeOnlyTitle, which is the value this assertion passed on before it was written.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        string title = await ReadTitleAsync(response);
        await Assert.That(title).IsNotEqualTo(StatusCodeOnlyTitle);
        await Assert.That(title).IsNotEqualTo(PasskeyVerificationExceptionHandler.Title);

        await Assert.That((await ReadSessionsAsync(admin)).Count).IsEqualTo(0);
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
    }

    /// <summary>
    /// The session lands on the account the code belongs to, and not on the account whose token was
    /// attached to the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is <c>CompleteAssertionHandler</c>'s own property, restated on the route that inherits
    /// it</b>, and it is the reason that handler never reads <c>IUserContext</c> for the account. The
    /// route is anonymous, so user provisioning returns on the route's marker and publishes nobody
    /// whatever token arrived — but a handler that reached for the request's identity anyway would find
    /// nothing on a genuine sign-in and <em>something</em> here, and the something is the wrong account.
    /// A client interceptor that attaches a bearer to every request is enough to arrange that by
    /// accident.
    /// </para>
    /// <para>
    /// <b>Both accounts hold a set, which is what makes the claim measurable at all.</b> With one set in
    /// the table, "the code's owner" and "the only owner there is" are the same account, and a handler
    /// that took the account from anywhere else would still land on it.
    /// </para>
    /// <para>
    /// <b>The presented token belongs to a real, established account</b> rather than to nobody: a token
    /// naming an account that does not exist would be refused upstream for a reason that has nothing to
    /// do with this rule, and the test would pass over the defect it was written for.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Redemption_TakesTheAccountFromTheCode_NotFromTheBearerToken()
    {
        // Arrange — two established accounts, each holding a set of its own.
        await using PostgresTestHost host = await StartHostAsync();

        HttpClient alice = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(alice);
        SyntheticAuthenticator alicesDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(alice, alicesDevice);
        Guid aliceId = await ResolveUserIdAsync(host, Subject);
        string[] alicesVerifiers = Verifiers();
        await IssueSetAsync(alice, alicesDevice, aliceId, alicesVerifiers);

        HttpClient bob = host.Factory.CreateAuthenticatedClient(OtherSubject);
        await ApiFactory.EstablishAccountAsync(bob);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(bob, bobsDevice);
        Guid bobId = await ResolveUserIdAsync(host, OtherSubject);
        string[] bobsVerifiers = Verifiers();
        await IssueSetAsync(bob, bobsDevice, bobId, bobsVerifiers);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid bobsSetId = await ResolveSetCredentialIdAsync(admin, bobId);

        // Act — Bob's code, presented on Alice's authenticated client.
        HttpResponseMessage response = await RedeemAsync(alice, bobsVerifiers[0]);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        IReadOnlyList<SessionRow> sessions = await ReadSessionsAsync(admin);
        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].UserId).IsEqualTo(bobId);
        await Assert.That(sessions[0].CredentialId).IsEqualTo(bobsSetId);

        // Stated as its own inequality as well as an equality above, because this is the assertion whose
        // failure message names the defect rather than only reporting that two ids differ.
        await Assert.That(sessions[0].UserId).IsNotEqualTo(aliceId);

        // Bob spent one code; Alice spent none.
        await Assert.That(await StoredHashesAsync(admin, bobId))
            .IsEquivalentTo(ExpectedHashesOf(bobsVerifiers.Skip(1)));
        await Assert.That(await StoredHashesAsync(admin, aliceId))
            .IsEquivalentTo(ExpectedHashesOf(alicesVerifiers));
    }

    /// <summary>
    /// An anonymous request naming a subject with no account creates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row counts are the whole of this test.</b> A status assertion cannot tell a route that
    /// refuses from a route that mints an account and <em>then</em> refuses — and a provider id token
    /// stays valid for up to an hour after the account it names is erased, so a <c>ProvisionsUser</c>
    /// marker arriving on this route would turn one retried redemption into a resurrected, passkey-less
    /// account that the re-authentication gate in front of erasure can never remove again. This route must
    /// never carry that marker, and it is a likelier accident here than on <c>/api/me</c>: this is the
    /// route people reach for when they cannot get in, which reads a great deal like a route that should
    /// be able to create something.
    /// </para>
    /// <para>
    /// <b>Counted unscoped, on the superuser connection.</b> The id an accidental marker would mint is one
    /// no assertion here could name, so a count filtered to this subject would pass over the very row it
    /// exists to catch — and <c>users</c>, <c>budgets</c> and <c>sessions</c> are policed by
    /// <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed connection reports zero for a row that
    /// is there exactly as it does for one that is not.
    /// </para>
    /// <para>
    /// <b>The two title assertions are what make the counts mean anything.</b> An unmapped path answers
    /// 404 and leaves the same empty tables behind, so the status is asserted first; and the refusal may
    /// not be <see cref="UserProvisioningMiddleware.NoAccountTitle" />, because that title is the
    /// middleware's answer to an authenticated principal it could not resolve — reaching it would mean the
    /// route is <em>not</em> anonymous, and a genuine recovery sign-in from a browser holding a stale
    /// provider token would be refused before the handler ever saw the code.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Redemption_ForASubjectWithNoAccount_CreatesNothing()
    {
        // Arrange — an authenticated client that has deliberately never called EstablishAccountAsync, on
        // a host where nothing else has either.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-redeeming-unprovisioned");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        HttpResponseMessage response = await RedeemAsync(client, Verifiers(1)[0]);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        string title = await ReadTitleAsync(response);
        await Assert.That(title).IsNotEqualTo(UserProvisioningMiddleware.NoAccountTitle);
        await Assert.That(title).IsNotEqualTo(StatusCodeOnlyTitle);

        await Assert.That(await ScalarAsync(admin, "select count(*) from users")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from credentials")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from budgets")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from sessions")).IsEqualTo(0L);
    }

    /// <summary>
    /// Redeeming the last code of a set leaves the set's credential standing, with nothing left in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Somebody will try to clean this up, and it must not be cleaned up.</b> An empty set looks like a
    /// row with no purpose, so the tidy-minded change is to delete the <c>credentials</c> row when the
    /// last code goes. That row is what the session just established hangs off — <c>sessions</c>
    /// references <c>credentials(id, user_id, type)</c> with <c>ON DELETE CASCADE</c> — so deleting it
    /// would cascade away the session the redemption opened, in the same request, and sign the person
    /// straight back out. Worse, it would mean an <em>anonymous</em> request removing a <c>credentials</c>
    /// row: <c>credentials</c> is exempt from row-level security, so nothing beneath the application would
    /// be watching a delete that no identity authorized.
    /// </para>
    /// <para>
    /// <b>Every code is redeemed through the route</b>, rather than nine being deleted out of band and the
    /// tenth driven, so the state the last redemption meets is the state this product produces. The
    /// remaining count is asserted on every response and not only the last: a count that jumped, stalled
    /// or restarted mid-way would otherwise be invisible behind a correct final zero.
    /// </para>
    /// <para>
    /// <b>Ten sessions is the second half of the claim.</b> Nothing revokes an earlier session when a
    /// later code is spent — each redemption is its own sign-in — and that is what makes "the credential
    /// still stands" observable as more than a row count.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Redemption_OfTheLastCode_LeavesTheSetStandingWithNothingLeft()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        string[] verifiers = Verifiers();
        await IssueSetAsync(client, device, userId, verifiers);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setId = await ResolveSetCredentialIdAsync(admin, userId);

        // Act — all ten, each on a client of its own, counting down as it goes.
        for (int spent = 0; spent < RequiredCodeCount; spent++)
        {
            HttpResponseMessage response = await RedeemAsync(host.Factory.CreateClient(), verifiers[spent]);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

            JsonObject body = await ReadJsonObjectAsync(response);
            await Assert.That(body[RemainingMember]!.GetValue<int>()).IsEqualTo(RequiredCodeCount - spent - 1);
        }

        // Assert — nothing left to redeem.
        await Assert.That((await StoredHashesAsync(admin, userId)).Length).IsEqualTo(0);

        // And the set is still there, under the same credential every one of those sessions hangs off.
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, userId)).IsEqualTo(setId);

        IReadOnlyList<SessionRow> sessions = await ReadSessionsAsync(admin);
        await Assert.That(sessions.Count).IsEqualTo(RequiredCodeCount);
        await Assert.That(sessions.All(session => session.CredentialId == setId)).IsTrue();
    }

    /// <summary>
    /// One session row, as the tests that count them read it.
    /// </summary>
    /// <remarks>
    /// The id is carried so the happy path can assert it never left the server, which is the one use it
    /// has: no assertion here matches a session <em>by</em> id.
    /// </remarks>
    private sealed record SessionRow(Guid Id, Guid UserId, Guid CredentialId, string Kind);

    /// <summary>
    /// <paramref name="count" /> distinct verifiers of <see cref="VerifierLength" /> bytes each, base64url
    /// encoded exactly as a browser would send them.
    /// </summary>
    /// <remarks>
    /// Random rather than fixed vectors, which costs nothing: no assertion depends on the value of a
    /// verifier — every expected hash is computed from the bytes the test itself produced — so
    /// repeatability is not at stake, and randomness is what makes an "unknown verifier" reliably unknown.
    /// </remarks>
    private static string[] Verifiers(int count = RequiredCodeCount) =>
    [
        .. Enumerable.Range(0, count)
            .Select(_ => Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength))),
    ];

    /// <summary>
    /// A verifier-shaped string carrying standard base64's two extra characters, which base64url replaces
    /// with '-' and '_'.
    /// </summary>
    /// <remarks>
    /// Substituted into a good encoding rather than produced by <c>Convert.ToBase64String</c>, because a
    /// random value encoded that way need contain neither character — its only guaranteed difference is
    /// the '=' padding, which the decoder accepts deliberately. The length is untouched, so this is
    /// refused for its alphabet and not for its width.
    /// </remarks>
    private static string NotBase64Url()
    {
        char[] mangled = [.. Verifiers(1)[0]];
        mangled[0] = '+';
        mangled[1] = '/';

        return new string(mangled);
    }

    /// <summary>The SHA-256 of each verifier, as the rows store it, ordered.</summary>
    private static string[] ExpectedHashesOf(IEnumerable<string> verifiers) =>
    [
        .. verifiers
            .Select(verifier => Convert.ToHexString(SHA256.HashData(Base64UrlText.Decode(verifier))))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Presents one verifier to the redemption route.
    /// </summary>
    /// <remarks>
    /// The body carries one member and the client carries whatever token the caller gave it — usually
    /// none, which is the state a recovery sign-in arrives in, and deliberately a real one in
    /// <see cref="Redemption_TakesTheAccountFromTheCode_NotFromTheBearerToken" />.
    /// </remarks>
    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string verifier) =>
        client.PostAsJsonAsync(RedemptionPath, new Dictionary<string, string> { [VerifierMember] = verifier });

    /// <summary>
    /// Issues one set through the real route: the re-authentication options leg, <paramref name="device" />
    /// answering the nonce it issued, and the generation post.
    /// </summary>
    /// <remarks>
    /// Fails loudly on any non-success status, because a silent no-op here would leave the redemption
    /// under test matching against a set that was never written — which for a refusal assertion reads as a
    /// pass.
    /// </remarks>
    private static async Task IssueSetAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        IReadOnlyList<string> verifiers)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);

        // signCount stays at zero on every ceremony in this file: a synced authenticator reports zero
        // every time, and PasskeySignatureCounter.Accept reads a repeated zero as no movement rather than
        // as a clone, so one device proves presence as often as a test needs.
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);

        HttpResponseMessage response = await client.PostAsJsonAsync(RecoveryCodesPath, new
        {
            codes = SubmissionsOf(verifiers),
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// One whole submission per verifier, as the issuing route spells a set: the verifier, a factor of
    /// its own, and the pair of envelopes sealed under that code's key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ten submissions and never ten verifiers beside one factor and one pair.</b> A set is ten
    /// separate secrets under a single <c>credentials</c> row and the client derives a key-encryption
    /// key from each <em>code</em>, so one pair for the whole set would seal the account under whichever
    /// code that pair belonged to and nine of the ten would open nothing.
    /// </para>
    /// <para>
    /// Nothing in this file redeems an envelope — a redemption presents a verifier and gets a session —
    /// so what these have to be is well-formed, and fresh: two codes of one set repeating a factor
    /// identifier is a refusal of its own, and <c>factor_id</c> is the table's primary key —
    /// <c>PK_wrapped_account_keys</c> — unique across the whole table rather than per account.
    /// </para>
    /// </remarks>
    private static object[] SubmissionsOf(IReadOnlyList<string> verifiers) =>
    [
        .. verifiers.Select(verifier =>
        {
            WrappedKeyFixture keys = WrappedKeyFixture.Mint();

            return new
            {
                verifier,
                factorId = keys.FactorId,
                wrappedContentKey = keys.WrappedContentKey,
                wrappedIndexKey = keys.WrappedIndexKey,
            };
        }),
    ];

    /// <summary>
    /// Runs both authenticated legs of a registration, so the account really holds a passkey a signature
    /// answers to rather than material seeded out of band.
    /// </summary>
    /// <remarks>
    /// The account already exists when this runs, and the two ways it got there are both above the call:
    /// <see cref="ApiFactory.CreateSignedInClientAsync" /> seeds the whole account behind the client it
    /// hands out, and the tests still reaching the route with a provider bearer establish theirs on the
    /// line above, where the fact that they need one is visible. Neither passkey leg mints an account and
    /// neither does <c>/api/me/*</c>, so a client arriving here without one is refused with a 401 for a
    /// reason no test here is about. Written out in this file rather than shared, because
    /// <c>RecoveryCodeGenerationTests</c> and <c>CredentialRevocationTests</c> each make the same choice
    /// for the same reason.
    /// </remarks>
    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        WrappedKeyFixture keys = WrappedKeyFixture.Mint();
        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedContentKey = keys.WrappedContentKey,
            wrappedIndexKey = keys.WrappedIndexKey,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        JsonNode options = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// The response body as an object, so a test can ask whether a member is <b>there</b> and not only
    /// what it deserializes to.
    /// </summary>
    /// <remarks>
    /// <b>Once per response.</b> The content stream is handed out once and then sits at its end, so a
    /// second call against the same response parses nothing and throws — a failure that names a JSON
    /// reader and not the route, in a test that looked like it was asking two questions about one payload.
    /// A test wanting two facts from one body parses it here and reads both off the result.
    /// </remarks>
    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();

    /// <summary>
    /// The session expiry, read as text and normalised to UTC.
    /// </summary>
    /// <remarks>
    /// Parsed rather than deserialized through <c>GetValue&lt;DateTime&gt;</c> so an instant serialized
    /// without an offset is read as the UTC it is, instead of being reinterpreted in the runner's zone —
    /// which would make this assertion pass or fail on where the test ran.
    /// </remarks>
    private static DateTime ExpiryOf(JsonObject body) => DateTime.Parse(
        body[ExpiresMember]!.GetValue<string>(),
        CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// The whole body, with the one member that varies per <b>request</b> rather than per <b>cause</b>
    /// replaced by a fixed placeholder.
    /// </summary>
    /// <remarks>
    /// <c>traceId</c> is a new value on every request, including two requests refused for the identical
    /// reason, so comparing it would compare the trace and not the refusal. The member is replaced rather
    /// than removed, so a <c>traceId</c> that stopped being emitted still fails and any other member
    /// appearing, disappearing or differing fails with it.
    /// </remarks>
    private static string ComparableBodyOf(JsonObject body)
    {
        JsonObject comparable = body.DeepClone().AsObject();
        if (comparable.ContainsKey(TraceIdMember))
        {
            comparable[TraceIdMember] = "<one per request>";
        }

        return comparable.ToJsonString();
    }

    /// <summary>
    /// <see cref="ComparableBodyOf" /> for a caller that needs nothing else out of the response.
    /// </summary>
    private static async Task<string> ReadComparableBodyAsync(HttpResponseMessage response) =>
        ComparableBodyOf(await ReadJsonObjectAsync(response));

    /// <summary>
    /// Every header of a response — the message's and the content's — as one ordered string, with the
    /// values that vary per request replaced by a fixed placeholder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both collections, because <c>Content-Type</c> and <c>Content-Length</c> live on the content and
    /// are exactly the two a refusal is likeliest to differ in.</b> Ordered by name, so a comparison never
    /// fails on the order a server happened to emit them in.
    /// </para>
    /// <para>
    /// The values in <see cref="PerRequestHeaders" /> are replaced rather than dropped, for the reason
    /// <see cref="ComparableBodyOf" /> replaces <c>traceId</c>: a header that stopped being emitted on one
    /// arm must still fail.
    /// </para>
    /// </remarks>
    private static string ComparableHeadersOf(HttpResponseMessage response) =>
        string.Join(
            "; ",
            response.Headers
                .Concat(response.Content.Headers)
                .Select(header => $"{header.Key}: {ComparableHeaderValue(header.Key, header.Value)}")
                .Order(StringComparer.Ordinal));

    /// <summary>One header's value, or a placeholder when the value is a new one on every request.</summary>
    private static string ComparableHeaderValue(string name, IEnumerable<string> values) =>
        PerRequestHeaders.Contains(name) ? "<one per request>" : string.Join(",", values);

    /// <summary>
    /// The <c>title</c> of a problem-details body, which is the only member that says which of this
    /// route's several possible 401s answered.
    /// </summary>
    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        (await ReadJsonObjectAsync(response))["title"]!.GetValue<string>();

    /// <summary>
    /// Reads back the user provisioning minted for <paramref name="subject" />. Nothing the API returns
    /// names it, so the lookup goes through the credential the middleware resolved on.
    /// </summary>
    private static async Task<Guid> ResolveUserIdAsync(PostgresTestHost host, string subject)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select user_id from credentials where provider = 'google' and subject = @subject",
            connection);
        command.Parameters.AddWithValue("subject", subject);

        return await command.ExecuteScalarAsync() switch
        {
            Guid userId => userId,
            var unexpected => throw new InvalidOperationException(
                $"Provisioning wrote no account for subject '{subject}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>The <c>credentials.id</c> of the row standing for an account's set of recovery codes.</summary>
    private static async Task<Guid> ResolveSetCredentialIdAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select id from credentials where user_id = @userId and type = 'recovery_codes'",
            admin);
        command.Parameters.AddWithValue("userId", userId);

        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"No recovery-code set is filed under that account, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Every unredeemed code of one account, as the hex of the value the row holds, ordered.
    /// </summary>
    /// <remarks>
    /// Ordered here and on the expectation side, so every comparison is about the set of stored values
    /// rather than about the order a scan returned them in — no read on this path promises one.
    /// </remarks>
    private static async Task<string[]> StoredHashesAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select encode(verifier_hash, 'hex') from recovery_code_hashes where user_id = @userId",
            admin);
        command.Parameters.AddWithValue("userId", userId);

        List<string> hashes = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            hashes.Add(reader.GetString(0).ToUpperInvariant());
        }

        return [.. hashes.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every session row in the database, on the superuser connection and scoped to nothing.
    /// </summary>
    /// <remarks>
    /// Unscoped deliberately: a session written for the wrong account is exactly the defect
    /// <see cref="Redemption_TakesTheAccountFromTheCode_NotFromTheBearerToken" /> is looking for, and a
    /// read filtered to the expected account would pass straight over it. <c>sessions</c> carries
    /// <c>user_isolation</c>, which is <c>FOR ALL</c>, so this cannot be read on the application role at
    /// all — a policed connection reports zero rows for a session that is there exactly as it does for one
    /// that is not.
    /// </remarks>
    private static async Task<IReadOnlyList<SessionRow>> ReadSessionsAsync(NpgsqlConnection admin)
    {
        await using NpgsqlCommand command = new(
            "select id, user_id, credential_id, kind from sessions order by created_at_utc",
            admin);

        List<SessionRow> sessions = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sessions.Add(new SessionRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3)));
        }

        return sessions;
    }

    /// <summary>How many sets the account holds, which the product's own index bounds at one.</summary>
    private static async Task<long> CountSetsAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from credentials where user_id = @userId and type = 'recovery_codes'",
            admin);
        command.Parameters.AddWithValue("userId", userId);

        return await ReadCountAsync(command);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection admin, string sql)
    {
        await using NpgsqlCommand command = new(sql, admin);

        return await ReadCountAsync(command);
    }

    /// <summary>
    /// Runs a counting query, refusing anything that is not a count.
    /// </summary>
    /// <remarks>
    /// Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the query
    /// changed shape, and that should fail loudly here instead of at the assertion.
    /// </remarks>
    private static async Task<long> ReadCountAsync(NpgsqlCommand command) =>
        await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{command.CommandText}', got '{unexpected ?? "null"}'."),
        };

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();

        return host;
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because the one test
    /// above that arranges its account through a session cookie needs the cookie handler to be asked.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="StartHostAsync" /> rather than replacing it, and the reason is
    /// <see cref="ReadSessionsAsync" />. Every other test here counts <c>sessions</c> rows across the
    /// whole database — deliberately, since a session written for the wrong account is what
    /// <see cref="Redemption_TakesTheAccountFromTheCode_NotFromTheBearerToken" /> is looking for — and
    /// seeding a sign-in writes one, so those counts change value the moment such a client is handed out.
    /// That is a decision about what the counts should say rather than a change of client.
    /// </remarks>
    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();

        return host;
    }
}
