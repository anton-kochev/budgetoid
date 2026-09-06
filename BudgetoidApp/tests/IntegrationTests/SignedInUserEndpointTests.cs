using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That a signed-in account can ask who it is and be told its own email address, and that the answer
/// is the caller's address rather than some other account's.
/// </summary>
/// <remarks>
/// <para>
/// Driven through the real HTTP pipeline rather than the handler, because half of what is measured
/// here is which account the request arrives as. The address is not a parameter the caller passes; it
/// is resolved from the session the cookie names, by the authentication handler above the route, and a
/// handler tested in isolation would be handed the identity these tests exist to check the pipeline
/// produces.
/// </para>
/// <para>
/// <see cref="Me_ForASecondAccount_RespondsWithThatAccountsAddressAndNotTheFirsts" /> is the control
/// for <see cref="Me_ForAnAuthenticatedOwner_RespondsWithTheAccountsEmailAddress" />, and without it
/// the happy path is worth very little: a handler that returns a hardcoded string — or reads the
/// first row of <c>users</c> — satisfies the single-account test perfectly, because with one account
/// established every wrong answer and the right one are the same value. It takes a second account to
/// tell a resolved address apart from a constant.
/// </para>
/// <para>
/// The happy path is in turn the control for the one refusal left — an anonymous 401. A route that was
/// never mapped at all refuses every caller and would satisfy it, but so would a route mapped behind a
/// policy nobody can clear, while the feature does not exist. This test is what says the door opens for
/// somebody.
/// </para>
/// <para>
/// Neither test names a production type. They address the route over HTTP and read the wire body, so
/// while the endpoint is missing they fail on the status assertion against a real 404 rather than
/// failing to compile — which is the difference between a red test that is telling us something and
/// one that is telling us nothing.
/// </para>
/// <para>
/// <b>There used to be two refusals here and now there is one, because the second state stopped
/// existing.</b> An authenticated principal naming an account that does not exist was reachable while a
/// provider bearer authenticated this route: the token outlived the account it named by up to an hour,
/// the provisioning middleware answered it a titled <c>NoAccountTitle</c> 401, and a
/// <c>ProvisionsUserAttribute</c> added to this group by mistake would have turned one retried
/// <c>GET /api/me</c> into a resurrected, passkey-less account. That is why the test that drove it
/// counted rows rather than reading a status. None of it is reachable now. This route authenticates
/// from the session cookie and from nothing else, the cookie is only ever issued over a session row,
/// and a session row is only ever written beside the account it names — so "authenticated, and no such
/// account" is not a state the pipeline can be in, and there is no marker left to add. The refusal
/// below is consequently the only one, and its title is no longer evidence of anything, which is said
/// where it is asserted.
/// </para>
/// <para>
/// <see cref="Me_ResponseCarriesTheEmailAndNothingElse" /> is a <b>pin</b>, and it was green the day it
/// was written, which was the point rather than an apology. Nothing else in either suite goes red when
/// an <c>id</c> or a <c>createdAtUtc</c> starts arriving in this response: the happy-path test reads the
/// <c>email</c> member and would keep passing beside a second one, and the two-account test only
/// refuses a member carrying the <em>other</em> account's address. The defect it exists to catch is one
/// a later reader adds — a handler widened to return the whole row because the shape was to hand — and
/// a test that only went red once would have to be written after the id had already shipped to a
/// client.
/// </para>
/// <para>
/// <b>THREE CASES HERE ARE RED ON PURPOSE UNTIL THE RESPONSE GAINS A <c>budgetId</c> MEMBER</b> —
/// <see cref="Me_ForAnAuthenticatedOwner_CarriesTheAmbientBudgetId" />,
/// <see cref="Me_ForASecondAccount_CarriesThatAccountsBudgetAndNotTheFirsts" /> and the widened
/// expectation in the pin. They are written first because the value is a client-side cryptographic
/// input rather than something a screen shows: the blind index over a name is computed in a browser from
/// <c>budgetoid/blind-index/v1 ⌷ table ⌷ column ⌷ budgetId ⌷ normalized-name</c>, and the budget is the
/// one part of that message the browser cannot derive from anything it holds. Until it arrives, an
/// operator holding two budgets' rows can see that both hold a payee, account, category or category
/// group of the same name, because the message carries no budget and the index key is per account — the
/// correlation NFR-014 refuses. The two new cases keep this file's own rule and name no production type;
/// they address the route over HTTP and read the wire body, so while the member is missing they fail on
/// an assertion against a real response rather than failing to compile.
/// </para>
/// <para>
/// <b><see cref="Me_ForASubjectWhoseProviderAddressChanged_RespondsWithTheStoredAddress" /> kept its
/// rule and lost its mechanism, and what replaced the mechanism is worth reading before touching
/// it.</b> The rule is that a later provider token has no authority over what registration stored: the
/// registered address is deliberately never refreshed from the provider, so a person who changes their
/// Google address must still be shown the address their account can actually be reached at. It used to
/// be arranged as two bearer clients on one subject carrying different <c>email</c> claims, because
/// that was the only arrangement in which "read out of the account" and "echoed off the token that
/// asked" were different strings — every other test here authenticates with the same value it then
/// expects back, and a delegate reading <c>httpContext.User.FindFirstValue("email")</c> satisfied the
/// lot, the two-account control included. That arrangement is gone with the bearer.
/// </para>
/// <para>
/// Two things replace it. The echo defect is now caught by the <em>happy path</em>, for free: the
/// session cookie's principal carries <c>sub</c>, <c>session_id</c> and <c>session_kind</c> and no
/// address of any kind, so an endpoint reading an <c>email</c> claim answers empty for every caller
/// alive. And the product rule is arranged the only way that is still real — a second provider token,
/// same subject, different address, presented to the one surface a provider token still reaches, which
/// is registration. The row half of that is
/// <c>AccountRegistrationTests.Registration_WhenTheSubjectAlreadyHasAnAccount_Returns409AndChangesNothing</c>,
/// which takes a census of every relation before and after; this is the wire half, and it is the one
/// that says what the person is shown afterwards.
/// </para>
/// <para>
/// <b>The two halves are not interchangeable, which is why this one may not be simplified into a
/// refusal read off the options leg.</b> That census counts <em>rows</em>. An <c>UPDATE</c> of
/// <c>users.email</c> moves no count, so a write path that refused the second registration and
/// refreshed the stored address on its way out satisfies every relation's before-and-after exactly.
/// Reading the address back after the write path has seen the new token is the only thing in either
/// suite that can see that — so the request under test here remains the one that reaches the write
/// path, and the arrangement note on the test says how it still gets there now that the options leg
/// turns this token away a leg earlier.
/// </para>
/// </remarks>
public sealed class SignedInUserEndpointTests
{
    private const string MePath = "/api/me";

    [Test]
    public async Task Me_ForAnAuthenticatedOwner_RespondsWithTheAccountsEmailAddress()
    {
        // Arrange — the address the client authenticates with is written down here rather than left to
        // the factory's default, so the assertion below compares against a value this test chose. A
        // test that re-derived the expected address the same way the client did would agree with
        // itself no matter which account answered.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync("google-owner", OwnerAddress);

        // Act
        HttpResponseMessage response = await client.GetAsync(MePath);

        // Assert — the media type only, never the whole Content-Type header. The charset the framework
        // appends is a framework detail this feature makes no claim about, and pinning the full string
        // would go red on a framework change that altered nothing anyone can observe.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");

        JsonNode document = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))
            ?? throw new InvalidOperationException("The endpoint answered an empty body.");
        await Assert.That(document["email"]!.GetValue<string>()).IsEqualTo(OwnerAddress);
    }

    /// <summary>
    /// That the response carries the ambient budget's identifier, rendered as a hyphenated UUID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The client cannot compute a blind index without this value, which is the whole reason it is
    /// published.</b> The index message is
    /// <c>budgetoid/blind-index/v1 ⌷ table ⌷ column ⌷ budgetId ⌷ normalized-name</c>, and the budget id
    /// is the only one of those five the browser cannot derive from what it already holds: the grammar
    /// and the table-and-column pair are the client's own constants, the name is what somebody typed,
    /// and the index key is in custody — but the budget is resolved server-side from the session cookie
    /// and named in no request and no other response. Without it the client can seal a name and cannot
    /// index one, so every write to a blind-indexed column is unreachable.
    /// </para>
    /// <para>
    /// <b>The expected value comes from the seeding and is never read off the response.</b>
    /// <c>CreateSignedInClientAsync</c> hands back the budget it wrote, so the comparison is against a
    /// row this test knows exists. A test that re-read the member it is asserting, or that derived the
    /// expectation the same way the endpoint does, would agree with itself whichever budget answered.
    /// </para>
    /// <para>
    /// <b><c>ToString("D")</c> and not <c>ToString()</c>, though the two agree today.</b> The default
    /// format IS <c>D</c>, so this spelling changes nothing about the value and everything about what a
    /// reader is being told: the wire carries the hyphenated form, that is what a browser will parse and
    /// what it will feed into the index message byte for byte, and a response that started emitting
    /// <c>N</c> or <c>B</c> would key every name in the account differently while remaining a perfectly
    /// valid UUID. Naming the format is what makes that a pinned decision rather than a default nobody
    /// chose.
    /// </para>
    /// <para>
    /// The member is read with <c>?.</c> rather than <c>!</c> deliberately. While the endpoint does not
    /// publish it, the forgiving spelling fails with a null-reference exception and no reader can tell
    /// that from a broken arrangement; this way the red says the member is missing and names what was
    /// expected in its place.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Me_ForAnAuthenticatedOwner_CarriesTheAmbientBudgetId()
    {
        // Arrange — the budget id is taken from the seeding, which is the only place in this test that
        // knows which budget the account owns.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, _, Guid budgetId) =
            await host.Factory.CreateSignedInClientAsync("google-owner", OwnerAddress);

        // Act
        HttpResponseMessage response = await client.GetAsync(MePath);

        // Assert — the status first, so a body missing because the request failed reads as the failure
        // it is rather than as a member that did not arrive.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonNode document = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))
            ?? throw new InvalidOperationException("The endpoint answered an empty body.");
        await Assert.That(document["budgetId"]?.GetValue<string>()).IsEqualTo(budgetId.ToString("D"));
    }

    /// <summary>
    /// That two established accounts each receive their own address, and that neither is shown the
    /// other's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The negative half is the half that carries the weight. "Mine is present" passes against a
    /// handler that reads the first row of <c>users</c> whenever the caller happens to be that row,
    /// and it passes against a document that answers with everyone's address at once — a query that
    /// forgot its owner filter and serialized the lot. Asserting that the other account's address does
    /// not appear <em>anywhere in the payload</em>, rather than merely that the <c>email</c> member
    /// holds the right value, is what refuses the second of those: an extra member carrying a stranger's
    /// address is a leak whether or not the member this test reads is correct.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Me_ForASecondAccount_RespondsWithThatAccountsAddressAndNotTheFirsts()
    {
        // Arrange — account A is established first and account B second, and that ordering is
        // load-bearing rather than incidental. An unfiltered read, or one that takes First(), returns
        // whichever row was written first: with A ahead of B, B asking for itself is answered with A's
        // address and the test goes red. Seed B first and the same broken handler answers B correctly,
        // and the test passes for a reason that has nothing to do with the feature.
        await using PostgresTestHost host = await StartSignedInHostAsync();

        (HttpClient first, _, _) = await host.Factory.CreateSignedInClientAsync("google-first", FirstAddress);
        (HttpClient second, _, _) = await host.Factory.CreateSignedInClientAsync("google-second", SecondAddress);

        // Act — both callers ask, because one of them alone cannot distinguish a resolved address from
        // a constant. Asking as A as well as B is also what would catch a handler that had simply been
        // made to return the *last* row instead of the first.
        HttpResponseMessage secondResponse = await second.GetAsync(MePath);
        HttpResponseMessage firstResponse = await first.GetAsync(MePath);

        // Assert — B first, since it is the caller the ordering above was arranged to trap.
        await AssertAnsweredWithAsync(secondResponse, SecondAddress, FirstAddress);
        await AssertAnsweredWithAsync(firstResponse, FirstAddress, SecondAddress);
    }

    /// <summary>
    /// That each account is told its own budget, and never the other account's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the risky one of the pair, and the risk is a read of <c>budgets</c> that forgot its
    /// filter.</b> The value is meant to come from the ambient budget the authentication handler
    /// resolved — <c>IBudgetContext.BudgetId</c> — and the obvious wrong implementation is a query over
    /// <c>budgets</c> scoped by nothing, or by <c>user_id</c> with a <c>First()</c> on the end. Both are
    /// plausible, both compile, and against a single seeded account both are indistinguishable from the
    /// right answer, because with one budget in the table every wrong row and the right one are the same
    /// row.
    /// </para>
    /// <para>
    /// <b>The seeding order is therefore load-bearing rather than incidental.</b> Account A is
    /// established first and account B second, so an unfiltered read answers B with A's budget and this
    /// test goes red. Reverse the two and the same broken handler answers B correctly, and the case
    /// passes for a reason that has nothing to do with the feature. It is the sibling
    /// <see cref="Me_ForASecondAccount_RespondsWithThatAccountsAddressAndNotTheFirsts" />'s argument,
    /// applied to a value that has a second table to be read out of and is therefore easier to get
    /// wrong.
    /// </para>
    /// <para>
    /// <b>The negative half is over the whole payload and not over the member.</b> Asserting that
    /// <c>budgetId</c> holds B's value refuses the swap and admits the leak: a document carrying a
    /// second member with A's budget in it — a widened projection, a debug field, a list of the budgets
    /// this user owns — satisfies the positive half completely. Under this change a leaked budget id is
    /// not merely an identifier: it is the value another account's blind indexes are keyed on, so it
    /// hands the holder the ability to recompute a stranger's index for any name they can guess, which
    /// is the exact correlation NFR-014 exists to refuse.
    /// </para>
    /// <para>
    /// <b>Both callers ask, for the reason the address pair does.</b> B alone cannot tell a resolved
    /// budget from a constant, and it would not catch a handler that had been made to return the LAST
    /// budget rather than the first.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Me_ForASecondAccount_CarriesThatAccountsBudgetAndNotTheFirsts()
    {
        // Arrange — A first, then B. See the remark: reversing this makes the case green against the
        // unfiltered read it exists to catch.
        await using PostgresTestHost host = await StartSignedInHostAsync();

        (HttpClient first, _, Guid firstBudgetId) =
            await host.Factory.CreateSignedInClientAsync("google-first", FirstAddress);
        (HttpClient second, _, Guid secondBudgetId) =
            await host.Factory.CreateSignedInClientAsync("google-second", SecondAddress);

        // The two budgets really are two, which is the arrangement rather than an assertion about the
        // product. A seeding that handed both accounts one budget would make every claim below pass
        // while measuring nothing at all.
        await Assert.That(secondBudgetId).IsNotEqualTo(firstBudgetId);

        // Act
        HttpResponseMessage secondResponse = await second.GetAsync(MePath);
        HttpResponseMessage firstResponse = await first.GetAsync(MePath);

        // Assert — B first, since it is the caller the ordering above was arranged to trap.
        await AssertCarriedBudgetAsync(secondResponse, secondBudgetId, firstBudgetId);
        await AssertCarriedBudgetAsync(firstResponse, firstBudgetId, secondBudgetId);
    }

    /// <summary>
    /// That a subject whose address at the provider has changed since it registered is answered the
    /// address its account is <em>registered under</em>, and never the one on the token it arrived with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One subject and two provider tokens, not two accounts. Two accounts would measure isolation,
    /// which is already covered; what is measured here is that a <em>later</em> token has no authority
    /// over what registration wrote, which is the rule the users-and-ownership documentation states and
    /// the reason the endpoint exists at all — the address a person is shown must be the one their
    /// account can be reached at, not whatever the provider says today.
    /// </para>
    /// <para>
    /// <b>The second token is presented to registration, because registration is the only surface a
    /// provider token still reaches.</b> This used to be arranged by making the second request to
    /// <c>/api/me</c> itself with a bearer whose claim had moved, which measured the same rule one step
    /// closer to the assertion. That is no longer possible and, more to the point, no longer the shape
    /// of the risk: the cookie's principal carries no address, so no delegate here can echo a claim that
    /// is not there, and the one moment a stored address could be refreshed from a new token is a second
    /// pass through the write path. It is refused, and the assertion afterwards is that the refusal
    /// changed nothing a reader can see.
    /// </para>
    /// <para>
    /// <b>The <c>409</c> is asserted, and not as ceremony.</b> A second registration that succeeded
    /// would mean a second account under the same subject, whereupon the cookie below still names the
    /// first one and the assertion passes for a reason that has nothing to do with this rule. A second
    /// registration that failed for some unrelated reason — a drifted ceremony driver, a lost scheme
    /// repointing — never reaches the write path at all, and the test would then be measuring an attempt
    /// that was never made.
    /// </para>
    /// <para>
    /// <b>The nonce is opened by a stranger, and that is the arrangement rather than a way around
    /// one.</b> The options leg now refuses a subject that already has an account before it mints
    /// anything — <c>AccountRegistrationTests.SecondOptionsRequest_ForASubjectThatAlreadyHasAnAccount_IsRefused</c>
    /// is where that refusal is the subject — so the changed token can no longer open a ceremony of its
    /// own, and driving both legs as it would stop the request under test before it reached the code
    /// that writes an address. Beginning as <see cref="StrangerSubject" /> and finishing as
    /// <see cref="ChangedSubject" /> is the begin-then-finish race the finish leg's conflict check exists
    /// for: two requests with a gap, and an account created in the gap by another tab, another device or
    /// a retry already in flight.
    /// </para>
    /// <para>
    /// <b>Reaching the write path is the point, not an accident of how this used to be written.</b>
    /// Asserting the options leg's 409 instead would be a cheaper test that measures a different fact —
    /// that this token cannot start a ceremony — and would leave nothing anywhere driving the write path
    /// with an address that differs from the stored one. That is the only code that can overwrite a
    /// stored address, and the census next door cannot see it do so, because an <c>UPDATE</c> moves no
    /// row count. <b>Do not delete or downgrade this test as covered by the options leg's refusal.</b>
    /// </para>
    /// </remarks>
    [Test]
    public async Task Me_ForASubjectWhoseProviderAddressChanged_RespondsWithTheStoredAddress()
    {
        // Arrange — the account is registered under address A, by a provider token carrying A, and the
        // cookie the ceremony left behind is what every later request here presents.
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        ApiFactory.SignedInClient registered =
            await host.Factory.RegisterAccountAsync(ChangedSubject, RegisteredAddress);

        // The same person after a Google address change: the same subject — which is what the credential
        // resolves on and therefore what makes this one account rather than two — carrying address B.
        using HttpClient afterTheChange = host.Factory.CreateAuthenticatedClient(
            ChangedSubject,
            ChangedProviderAddress);

        // A ceremony opened by a subject nobody has registered, because the registered one is refused a
        // leg earlier now. The stranger only opens it — it never finishes, so no second account comes
        // into being and the reads below still have exactly one row to find.
        using HttpClient stranger = host.Factory.CreateAuthenticatedClient(
            StrangerSubject,
            StrangerAddress);
        IssuedRegistrationOptions ceremony = await RegistrationCeremony.BeginAsync(stranger);

        // Act — the new token presented to the one path that writes an address, which refuses it. It is
        // finished over the stranger's live nonce because that is the only way this token reaches that
        // path at all now, and reaching it is what the assertion below is about.
        HttpResponseMessage reregistered = await RegistrationCeremony.RegisterOverAsync(
            afterTheChange,
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId),
            ceremony.Challenge);

        // Assert — the attempt really reached the write path and was turned away there.
        await Assert.That(reregistered.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        // And the account still answers with the address it was registered under. Both halves, through
        // the helper the two-account test uses, because the halves are the same two here: A is the
        // answer, and B appears nowhere in the payload. The positive half alone would go green against a
        // handler that returned A beside a second member carrying B, and the negative half alone would
        // go green against a handler that answered a third account's address entirely. The helper also
        // reads the body as the text that went over the wire, which is what keeps B from hiding behind
        // an escape sequence.
        HttpResponseMessage response = await registered.Client.GetAsync(MePath);
        await AssertAnsweredWithAsync(response, RegisteredAddress, ChangedProviderAddress);
    }

    /// <summary>
    /// A caller carrying nothing is refused, which is now the whole of what this says.
    /// </summary>
    /// <remarks>
    /// <b>This test used to discriminate and now barely does, and that is stated rather than
    /// hidden.</b> It carried a second assertion — that the title was not the provisioning
    /// middleware's <c>NoAccountTitle</c> — and that inequality was the interesting half: without it, a
    /// route that had lost its authorization entirely still passed, because an anonymous request would
    /// walk on to the middleware, find no account for a principal it could not even name, and be
    /// answered the middleware's own 401. There is no middleware and no second 401, so the inequality
    /// had nothing left to rule out. What remains catches exactly one thing that nothing else in this
    /// file catches: the application's fallback policy being deleted or this group being marked
    /// <c>AllowAnonymous</c>, either of which answers this request <c>200</c>. The first of those is
    /// invisible to <c>AnonymousSurfaceTests</c>, which reads the marker rather than issuing a request;
    /// the second is not. That is the entire remaining value, and it is worth one cheap test.
    /// </remarks>
    [Test]
    public async Task Me_WithoutAuthentication_IsRefusedWithUnauthorized()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act — no cookie and no token, so nothing authenticates and the fallback policy decides.
        // GetAsync rather than GetStreamAsync: the latter throws on any non-2xx, so a route that
        // answered 200 to an anonymous caller would fail this test as a transport error rather than as
        // the status assertion it is.
        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync(MePath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// That the response object carries exactly two members, <c>budgetId</c> and <c>email</c>, and no
    /// third one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never <c>Contains("email")</c>, and that is the whole point of the shape.</b> A containment
    /// check over the member names is a test that can never fail: every widening of the response — an
    /// internal <c>id</c>, a <c>createdAtUtc</c>, whatever the next row projection brings — leaves
    /// <c>email</c> present and the assertion green. The members are joined and compared whole, the
    /// shape <c>DataMinimizationSchemaTests.Schema_PinsTheColumnsOfTheUserRow</c> uses on the table
    /// this response is read out of.
    /// </para>
    /// <para>
    /// Joined rather than counted for the same reason it is there: a count goes red on the second
    /// member too, but it says "1 != 2" and leaves the reader to work out which member arrived. The
    /// joined string names it in the failure message.
    /// </para>
    /// <para>
    /// Why it matters that an id stays out is argued on <c>SignedInUser</c> itself: every tenancy value
    /// in this API is resolved server-side from the authenticated subject and none is ever addressed by
    /// the client, and publishing an id that nothing displays is the first half of a client-supplied
    /// tenancy parameter.
    /// </para>
    /// <para>
    /// <b>THIS EXPECTATION WAS WIDENED FROM <c>email</c> TO <c>budgetId, email</c>, AND WIDENING A PIN IS
    /// THE CHEAPEST WAY TO FAKE ONE.</b> A pin whose expectation is edited every time it goes red is a
    /// changelog, so the widening carries the rule that admitted the member, and the next one has to
    /// satisfy the same rule or be refused.
    /// </para>
    /// <para>
    /// <b>The rule is not "the screen displays it".</b> Nothing renders a budget identifier and nothing
    /// is going to. What earns a member here is that <em>the client cannot derive it and cannot complete
    /// its half of a cryptographic contract without it</em>. The blind index is
    /// <c>budgetoid/blind-index/v1 ⌷ table ⌷ column ⌷ budgetId ⌷ normalized-name</c>, computed in a
    /// browser under a key this server has never held; four of those five parts the browser already has,
    /// and the budget is resolved from the session cookie and named nowhere else in this API. Withhold
    /// it and no name can be written to a blind-indexed column at all.
    /// </para>
    /// <para>
    /// <b>A user id fails that test and stays unpublished, which is what makes the rule a rule.</b> It
    /// is equally underivable and equally undisplayed — and no client-side computation needs it, so the
    /// only thing publishing it would buy is a value a later route could accept as a tenancy parameter.
    /// The same refusal covers a session id, a credential id and a <c>createdAtUtc</c>: underivable is
    /// half the test, and load-bearing for something the browser must compute is the other half.
    /// </para>
    /// <para>
    /// <b>What this pin cannot see, and what therefore is not claimed here.</b> It reads member NAMES,
    /// so a <c>budgetId</c> carrying the wrong budget satisfies it perfectly — that is
    /// <see cref="Me_ForASecondAccount_CarriesThatAccountsBudgetAndNotTheFirsts" />'s job — and it says
    /// nothing about the spelling of the value, which
    /// <see cref="Me_ForAnAuthenticatedOwner_CarriesTheAmbientBudgetId" /> pins as <c>D</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Me_ResponseCarriesTheEmailAndNothingElse()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();

        // Act
        HttpResponseMessage response = await client.GetAsync(MePath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as an empty member list, which is a shape this test would otherwise
        // report as "no members arrived" and nobody would read as a 401.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject document = await ReadObjectAsync(response);

        // Ordered before joining, so a second member produces the same message whichever order the
        // serializer emitted it in — a red that reads differently between runs is a red people stop
        // trusting.
        string members = string.Join(
            ", ",
            document.Select(member => member.Key).Order(StringComparer.Ordinal));
        await Assert.That(members).IsEqualTo("budgetId, email");
    }

    /// <summary>
    /// The address <see cref="Me_ForAnAuthenticatedOwner_RespondsWithTheAccountsEmailAddress" />
    /// authenticates with. Deliberately not the <c>{subject}@example.com</c> shape the factory falls
    /// back to, so an endpoint that rebuilt the address from the subject instead of reading the stored
    /// account would be visible here rather than agreeing by construction.
    /// </summary>
    private const string OwnerAddress = "owner@budgetoid.test";

    /// <summary>The address of the account established first, and the one a <c>First()</c> returns.</summary>
    private const string FirstAddress = "first-account@budgetoid.test";

    /// <summary>The address of the account established second, which no unfiltered read reaches.</summary>
    private const string SecondAddress = "second-account@budgetoid.test";

    /// <summary>
    /// The one subject behind both clients in
    /// <see cref="Me_ForASubjectWhoseProviderAddressChanged_RespondsWithTheStoredAddress" />. Named
    /// rather than typed twice: the two clients being the <em>same</em> subject is the whole arrangement,
    /// and a typo in the second literal would silently turn that test into a second, weaker copy of the
    /// two-account control — green, and measuring nothing it was written for.
    /// </summary>
    private const string ChangedSubject = "google-owner";

    /// <summary>The address the account was registered under, and the only one it may ever answer with.</summary>
    private const string RegisteredAddress = "registered@budgetoid.test";

    /// <summary>
    /// The address the provider reports <em>after</em> the change — carried in the claim, stored nowhere.
    /// Deliberately not a substring of <see cref="RegisteredAddress" /> and not a superstring of it, so
    /// the "appears nowhere in the body" half fails on an echo rather than on the correct answer.
    /// </summary>
    private const string ChangedProviderAddress = "changed-at-the-provider@budgetoid.test";

    /// <summary>
    /// The provider identity that opens the nonce
    /// <see cref="Me_ForASubjectWhoseProviderAddressChanged_RespondsWithTheStoredAddress" /> finishes
    /// over. Deliberately <em>not</em> <see cref="ChangedSubject" />: the options leg refuses a subject
    /// that already has an account, so the registered one cannot mint a challenge, and the whole
    /// arrangement is that a live nonce and a registered subject meet on one finish leg.
    /// </summary>
    private const string StrangerSubject = "google-stranger";

    /// <summary>
    /// The stranger's address. Distinct from all three above so a leak of it would be visible, and it
    /// is never stored — the stranger opens a ceremony and never finishes one.
    /// </summary>
    private const string StrangerAddress = "stranger@budgetoid.test";

    /// <summary>
    /// Asserts both directions of one caller's answer: that its own address is the value of
    /// <c>email</c>, and that the other account's address appears nowhere in the body it was sent.
    /// </summary>
    /// <remarks>
    /// The body is read once, as text, and parsed from that text rather than from the response stream —
    /// the negative half needs the payload exactly as it went over the wire. Re-rendering a parsed
    /// document with <c>ToJsonString</c> would put the check at the mercy of whichever characters the
    /// serializer's encoder happens to escape, and an address hidden behind an escape sequence is a leak
    /// a <c>Contains</c> over the re-rendered text would report as absent.
    /// </remarks>
    private static async Task AssertAnsweredWithAsync(
        HttpResponseMessage response,
        string ownAddress,
        string otherAddress)
    {
        // The status first, so a body that is missing because the request failed reads as the failure it
        // is rather than as a parse error several lines further down.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        JsonNode document = JsonNode.Parse(payload)
            ?? throw new InvalidOperationException("The endpoint answered an empty body.");

        await Assert.That(document["email"]!.GetValue<string>()).IsEqualTo(ownAddress);
        await Assert.That(payload).DoesNotContain(otherAddress);
    }

    /// <summary>
    /// Asserts both directions of one caller's budget: that <c>budgetId</c> holds its own, and that the
    /// other account's appears nowhere in the body it was sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AssertAnsweredWithAsync" />'s shape and its argument about reading the payload as the
    /// text that went over the wire, applied to the other value this response carries. It is a separate
    /// helper rather than a widened one because the two claims have different lifetimes: the address
    /// pair is settled, and the budget pair is the half a later reader will be tempted to narrow to the
    /// member.
    /// </para>
    /// <para>
    /// <b>Both spellings of the stranger's budget are refused, and the second is not paranoia.</b> A
    /// leak through a member the serializer emitted from a <see cref="Guid" /> arrives hyphenated, and
    /// one that arrived through a hand-built string or a base64url binary member need not. Refusing
    /// <c>D</c> and <c>N</c> costs one line and closes the spelling a <c>Contains</c> over the
    /// hyphenated form alone would report as absent.
    /// </para>
    /// </remarks>
    private static async Task AssertCarriedBudgetAsync(
        HttpResponseMessage response,
        Guid ownBudgetId,
        Guid otherBudgetId)
    {
        // The status first, so a body that is missing because the request failed reads as the failure it
        // is rather than as a parse error several lines further down.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        JsonNode document = JsonNode.Parse(payload)
            ?? throw new InvalidOperationException("The endpoint answered an empty body.");

        await Assert.That(document["budgetId"]?.GetValue<string>())
            .IsEqualTo(ownBudgetId.ToString("D"));
        await Assert.That(payload).DoesNotContain(otherBudgetId.ToString("D"));
        await Assert.That(payload).DoesNotContain(otherBudgetId.ToString("N"));
    }

    /// <summary>
    /// The response body as a JSON object, refusing anything that is not one.
    /// </summary>
    /// <remarks>
    /// The cast is checked rather than forgiven: a response that answered an array, a bare string or
    /// <c>null</c> would give an empty member list through a lenient read, and an empty list is what the
    /// pin above reads as "one member, and it is not email" — a red nobody could interpret. Failing here
    /// says the response stopped being an object at all.
    /// </remarks>
    private static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response) =>
        await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()) as JsonObject
        ?? throw new InvalidOperationException("The endpoint answered something other than a JSON object.");

    /// <summary>
    /// The <c>title</c> of a problem-details body, which is the only member that says which of this
    /// route's refusals answered.
    /// </summary>
    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!["title"]!.GetValue<string>();

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{sql}', got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because the requests
    /// above authenticate from a session cookie.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="StartHostAsync" /> rather than replacing it. One test still wants a
    /// factory that authenticates nobody at all —
    /// <see cref="Me_WithoutAuthentication_IsRefusedWithUnauthorized" /> — and it makes its request
    /// through <c>CreateClient</c> either way, so the flag decides nothing there and the plain host is
    /// the cheaper arrangement to read.
    /// </remarks>
    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// A host that can run the registration ceremony <em>and</em> read the cookie it hands back.
    /// </summary>
    /// <remarks>
    /// Both flags, and both are required by
    /// <see cref="Me_ForASubjectWhoseProviderAddressChanged_RespondsWithTheStoredAddress" />: the two
    /// <c>/api/registration</c> routes declare a policy naming the provider's scheme and nothing else,
    /// so the ceremony needs that scheme repointed at the test handler, and the session it hands back is
    /// a cookie only the application's own handler can read. <c>RegisterAccountAsync</c> refuses loudly
    /// rather than silently misbehaving if either is missing.
    /// </remarks>
    private static async Task<PostgresTestHost> StartRegisteringHostAsync()
    {
        PostgresTestHost host = new(
            usesApplicationAuthentication: true,
            repointsProviderSchemeToTestHandler: true);
        await host.StartAsync();
        return host;
    }
}
