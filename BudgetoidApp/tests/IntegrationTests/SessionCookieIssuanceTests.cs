using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Application.Passkeys;
using Domain.Sessions;
using Infrastructure.Persistence.Provisioning;
using Microsoft.Net.Http.Headers;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The minting half: the three paths that establish a session hand out the handle it is presented by,
/// and the one path that establishes none hands out nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own file rather than three additions to the three path files, and the reason is a rule rather
/// than a preference.</b> <see cref="PasskeyCeremonyTests" />, <see cref="RecoveryCodeRedemptionTests" />
/// and <see cref="RecoveryCodeGenerationTests" /> each predate this change and each is about what its own
/// route does to the database; what is new here is one claim made three times — a handle leaves the
/// server, in a cookie, naming the session that request just opened — and it is a claim about the three
/// paths <em>together</em>. The negative control below only means anything read beside them, and it sits
/// on the generation route, which is the one path that both establishes and does not.
/// </para>
/// <para>
/// <b>The cookie name is written out as a literal here, not read off <c>SessionCookie.Name</c>.</b> It is
/// wire contract: a browser sends the bytes, not the symbol, so a test reading the constant agrees with
/// whatever the constant says and stays green through a rename that signs out every account already
/// holding one. <see cref="SessionCookieAuthenticationTests" /> states the same argument for the reading
/// half, and <see cref="FirstPartyRequestTests.ClientHeader" /> is used from over there for the same
/// reason it is declared over there.
/// </para>
/// <para>
/// <b>The digest is computed here with <see cref="SHA256" /> rather than through
/// <c>SessionToken.HashOf</c>.</b> What every test below claims is that the row stores the SHA-256 of the
/// value the cookie carries; a test that hashed through the production member would keep agreeing with it
/// after both spellings moved together, and the symptom of that agreement is every browser in the world
/// holding a handle no lookup can find.
/// </para>
/// <para>
/// <b>Every row is counted on the container superuser connection</b>, never on the application role.
/// <c>sessions</c> carries <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed connection reports
/// zero rows for a session that is there exactly as it does for one that is not — and half of what is
/// asserted here is that a row exists.
/// </para>
/// </remarks>
public sealed class SessionCookieIssuanceTests
{
    /// <summary>
    /// The name a request presents its session handle under. See the remarks on the class for why this is
    /// a literal rather than a production constant.
    /// </summary>
    private const string CookieName = "__Host-budgetoid-session";

    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";
    private const string AssertionPath = "/api/passkeys/assertion";
    private const string RedemptionPath = "/api/recovery-codes/redemption";
    private const string RecoveryCodesPath = "/api/me/recovery-codes";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string AccountsPath = "/api/accounts";
    private const string MePath = "/api/me";

    /// <summary>
    /// The members a successful assertion answers with, joined in ordinal order.
    /// </summary>
    /// <remarks>
    /// Written out rather than read off the endpoint's own record, which is private to it in any case:
    /// the point of the comparison is that the set has not widened, and an expectation derived from the
    /// type under test widens with it.
    /// </remarks>
    private const string AssertionMembers = "expiresAtUtc, kind";

    /// <summary>The members a successful redemption answers with, joined in ordinal order.</summary>
    private const string RedemptionMembers = "expiresAtUtc, kind, remaining";

    /// <summary>How many codes an issued set holds, restated rather than read off the handler.</summary>
    private const int RequiredCodeCount = 10;

    /// <summary>The exact width of a verifier, decoded — <c>RecoveryCodeHash.VerifierLength</c>.</summary>
    private const int VerifierLength = 32;

    /// <summary>
    /// That a verified assertion sets a cookie, and that the very next request presenting it is
    /// authenticated as that account and reaches that account's budget content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The strongest test in this file, because it is the only one that closes the loop.</b> Every
    /// other claim here is read out of the database: the handle is stored, its digest matches, its row
    /// names the right session. All of that is satisfied by a token minted correctly and written into a
    /// cookie the reading half cannot use — the wrong encoding, the wrong width, an escaped character —
    /// and none of those is visible from a row. Presenting the cookie back is what says the two halves
    /// agree, and it is the whole reason this test drives a second request.
    /// </para>
    /// <para>
    /// <b>It needs the application's own authentication left standing</b>, which is what
    /// <see cref="SessionCookieAuthenticationTests.CreateApiFactory" /> is for: a factory that names
    /// <c>TestAuthHandler</c> as the default authenticate scheme makes the cookie handler structurally
    /// unreachable, so the second request below would be answered by the test scheme and prove nothing.
    /// The passkey is therefore seeded rather than registered through the route, since registration is an
    /// authenticated leg and this host has no test scheme to authenticate it with.
    /// </para>
    /// <para>
    /// <b>Two routes, because the cookie has to publish two separate things.</b> <c>/api/me</c> answers
    /// from the identity; the account list is filtered by the ambient budget. Either alone leaves the
    /// other unmeasured, which is the same pairing
    /// <see cref="SessionCookieAuthenticationTests.AValidCookie_PublishesTheAccountAndItsAmbientBudget" />
    /// makes for a seeded handle.
    /// </para>
    /// <para>
    /// <b>The cookie's expiry is compared to the session's, not to an interval.</b> How long a session
    /// lasts is product policy pinned against a fixed clock in the unit suite; what this route owes is
    /// that the instant written onto the client is the row's own, so a browser cannot go on presenting a
    /// handle the server has already stopped honouring — or stop presenting one that still works. A
    /// second of tolerance, because the header carries whole seconds and the row carries microseconds.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AVerifiedAssertion_SetsACookieTheNextRequestAuthenticatesWith()
    {
        // Arrange — one account with a budget, one row of budget content in it, and a passkey the
        // synthetic device can really sign for.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        await SeedAccountAsync(host, owner.BudgetId, OwnerAccountName);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, device.CredentialId, device.CoseKey, device.Algorithm);
        HttpClient anonymous = factory.CreateClient();

        // Act — the two anonymous legs, on a client carrying no credential of any kind, which is the
        // state a sign-in actually arrives in.
        HttpResponseMessage signIn = await PostAssertionAsync(anonymous, device, owner.UserId);

        // Assert — the status first, so a body or a header missing because the request was refused reads
        // as the refusal it is rather than as a cookie nobody wrote.
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(signIn);
        string cookieValue = SessionCookieValueOf(signIn);

        // Well-formed as the reading half requires: unpadded base64url of exactly the token width. A
        // handle that is neither is refused before any row is read, and the refusal is indistinguishable
        // from a handle nobody ever issued.
        await Assert.That(cookieValue).DoesNotContain("=");
        await Assert.That(Base64UrlText.Decode(cookieValue).Length).IsEqualTo(SessionToken.TokenLength);

        // The row the handle names, and the digest it is stored under.
        SessionRow session = await SoleSessionAsync(host.ConnectionString);
        SessionTokenRow handle = await SoleSessionTokenAsync(host.ConnectionString);
        await Assert.That(handle.TokenHash).IsEqualTo(DigestOf(cookieValue));
        await Assert.That(handle.SessionId).IsEqualTo(session.Id);
        await Assert.That(handle.UserId).IsEqualTo(owner.UserId);

        // The cookie dies with the session it names rather than on an interval of its own.
        DateTimeOffset cookieExpiry = ExpiryOfCookie(signIn);
        DateTime sessionExpiry = ExpiryOf(body);
        await Assert.That((cookieExpiry - new DateTimeOffset(sessionExpiry)).Duration())
            .IsLessThan(TimeSpan.FromSeconds(1));

        // Act, again — the request that matters: the cookie, presented back, and nothing else.
        HttpResponseMessage me = await SendWithCookieAsync(anonymous, HttpMethod.Get, MePath, cookieValue);
        HttpResponseMessage accounts =
            await SendWithCookieAsync(anonymous, HttpMethod.Get, AccountsPath, cookieValue);

        // Assert — the identity the cookie published, and the ambient budget it published beside it.
        await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await EmailOfAsync(me)).IsEqualTo(OwnerEmail);
        await Assert.That(accounts.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await accounts.Content.ReadAsStringAsync()).Contains(OwnerAccountName);
    }

    /// <summary>
    /// That a refused assertion stores no handle, and hands nothing back on the wire either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row is what this holds; the header is held by the framework, and the difference was found
    /// by mutation.</b> The endpoint was changed to set the cookie <em>before</em> calling the handler
    /// and this test stayed green. Every refusal on this leg leaves by exception, and
    /// <c>UseExceptionHandler</c> clears the response before writing the ProblemDetails — so a
    /// <c>Set-Cookie</c> written earlier in the pipeline is wiped by ASP.NET Core whatever the endpoint
    /// did. Do not read the empty header below as evidence that the cookie is written after the
    /// verification: that ordering is held by
    /// <see cref="AVerifiedAssertion_SetsACookieTheNextRequestAuthenticatesWith" />, where a value the
    /// endpoint minted itself is a cookie whose digest is not the one stored, and that mutation goes red
    /// there.
    /// </para>
    /// <para>
    /// <b>The count over <c>session_tokens</c> is the claim nothing else in this file makes.</b> No
    /// other test here drives a refused request at all, and no rollback covers this one: the assertion
    /// path opens its transaction only after the signature verifies, and
    /// <see cref="ISessionRepository.AddAsync" />'s implementation saves on its own. A handler that
    /// filed a handle before it verified anything would therefore leave a committed row behind for a
    /// caller who never signed in — while still leaving exactly one row on the success path, so every
    /// positive assertion in this file would stay green.
    /// </para>
    /// <para>
    /// <b>The header assertion is kept rather than deleted, on a narrower reading.</b> It costs nothing
    /// beside a request the row assertion already needs, it states the wire half of the same sentence,
    /// and it starts discriminating the day a refusal on this leg answers by returning a result instead
    /// of throwing — which is a change somebody could make believing it changes nothing.
    /// <see cref="AFirstIssueOfRecoveryCodes_SetsNoCookie" /> is the contrast: that response is a 200,
    /// no clear runs, and there the absent header really is the endpoint's own doing.
    /// </para>
    /// <para>
    /// The refusal is a credential nobody registered, which is the cheapest of the seven the sign-in leg
    /// can answer and leaves no session behind for a handle to have named.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ARefusedAssertion_StoresNoHandleAndSetsNoCookie()
    {
        // Arrange — an account and a registered passkey, so "refused" is a verdict on this request
        // rather than on a database with nothing in it. The device that signs is a different one.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator registered = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, registered.CredentialId, registered.CoseKey, registered.Algorithm);
        SyntheticAuthenticator stranger = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        HttpClient anonymous = factory.CreateClient();

        // Act
        HttpResponseMessage refused = await PostAssertionAsync(anonymous, stranger, owner.UserId);

        // Assert — refused, no handle stored, and nothing on the wire the client could keep.
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(SetCookieHeadersOf(refused)).IsEqualTo(string.Empty);
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from session_tokens"))
            .IsEqualTo(0L);
    }

    /// <summary>
    /// That the assertion response says what the session is and hands over no way of naming it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted over the body's own member names, whole, and never by searching the payload for a
    /// string.</b> A search for the session id passes the day the id is renamed, re-encoded, or folded
    /// into a member added later; a member list that has to equal a written-out set fails on any of them.
    /// The payload search below is a second question, not the same one: what must not be there is the
    /// token itself, which is a value no member name can rule out.
    /// </para>
    /// <para>
    /// <b>The token half is the new claim.</b> The cookie is <c>HttpOnly</c> precisely so the handle is
    /// unreadable by script, and a handler that also returned it in the body would hand it straight back
    /// to whatever the page's own code — or any script sharing it — can read. That is not an
    /// authentication defect the reading half can see: both copies of the value work.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AVerifiedAssertion_CarriesNeitherTheHandleNorASessionIdentifierInItsBody()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, device.CredentialId, device.CoseKey, device.Algorithm);

        // Act
        HttpResponseMessage response = await PostAssertionAsync(factory.CreateClient(), device, owner.UserId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        JsonObject body = JsonNode.Parse(payload)!.AsObject();

        // Ordered before joining, so a member added later produces the same message whichever order the
        // serializer emitted it in. Never ContainsKey: a containment check over member names can never
        // fail, because every widening leaves the two below present and the check green.
        string members = string.Join(", ", body.Select(member => member.Key).Order(StringComparer.Ordinal));
        await Assert.That(members).IsEqualTo(AssertionMembers);

        // And no handle to the session left the server anywhere but the cookie, in either spelling of a
        // Guid and in the encoding the cookie itself carries.
        SessionRow session = await SoleSessionAsync(host.ConnectionString);
        await Assert.That(payload).DoesNotContain(session.Id.ToString("D"));
        await Assert.That(payload).DoesNotContain(session.Id.ToString("N"));
        await Assert.That(payload).DoesNotContain(SessionCookieValueOf(response));
    }

    /// <summary>
    /// That a redemption sets a cookie, and that the handle it hands over names a session belonging to
    /// the account the redeemed code was filed under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two accounts, each holding a set, and that is the arrangement rather than decoration.</b> With
    /// one account seeded, "the account this code belongs to" and "the only account in the table" are the
    /// same answer, and a handler that took whichever owner it found first would pass. The bystander's
    /// set is issued first, so a read that takes the earliest row signs the caller in as the wrong person
    /// and this test goes red.
    /// </para>
    /// <para>
    /// <b>The verdict is read out of <c>session_tokens</c> rather than by presenting the cookie back.</b>
    /// Issuing a set is an authenticated leg, so this host is the one whose default scheme is the test
    /// handler — the cookie could not be read on a second request here however correct the production
    /// code was. What the row can say, and says, is which session the handle names and whose account that
    /// session is: the loop from cookie to authenticated request is closed on the assertion path, where a
    /// host with the application's own authentication can be arranged.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ARedemption_SetsACookieNamingTheCodeOwnersSession()
    {
        // Arrange — the bystander first, so the earliest row in every table is the wrong one.
        await using PostgresTestHost host = await StartHostAsync();
        SyntheticAuthenticator bystanderDevice =
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        HttpClient bystander = host.Factory.CreateAuthenticatedClient(OtherSubject);
        await RegisterPasskeyAsync(bystander, bystanderDevice);
        Guid bystanderId = await ResolveUserIdAsync(host, OtherSubject);
        await IssueSetAsync(bystander, bystanderDevice, bystanderId, Verifiers());

        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        string[] verifiers = Verifiers();
        await IssueSetAsync(client, device, userId, verifiers);

        // No handle before the act, or the row read afterwards is one the arrangement produced.
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from session_tokens"))
            .IsEqualTo(0L);

        // Act — a client carrying no token at all, which is the state a recovery sign-in arrives in.
        HttpResponseMessage response = await RedeemAsync(host.Factory.CreateClient(), verifiers[0]);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string cookieValue = SessionCookieValueOf(response);
        await Assert.That(Base64UrlText.Decode(cookieValue).Length).IsEqualTo(SessionToken.TokenLength);

        // One session, and one handle, and the handle names that session for that account — never the
        // bystander, whose set was issued first and whose account is what a lookup that dropped its
        // owner predicate would land on.
        SessionRow session = await SoleSessionAsync(host.ConnectionString);
        SessionTokenRow handle = await SoleSessionTokenAsync(host.ConnectionString);
        await Assert.That(session.UserId).IsEqualTo(userId);
        await Assert.That(handle.TokenHash).IsEqualTo(DigestOf(cookieValue));
        await Assert.That(handle.SessionId).IsEqualTo(session.Id);
        await Assert.That(handle.UserId).IsEqualTo(userId);
        await Assert.That(handle.UserId).IsNotEqualTo(bystanderId);
    }

    /// <summary>
    /// That a refused redemption sets no cookie and stores no handle.
    /// </summary>
    /// <remarks>
    /// The control for the test above, and it carries more weight on this route than on the assertion
    /// one: this is the route somebody reaches for when they cannot get in, so a handler that filed a
    /// handle before the verifier matched would leave a committed row an anonymous caller could sign in
    /// with as whoever the arrangement happened to resolve. Read the two assertions the way
    /// <see cref="ARefusedAssertion_StoresNoHandleAndSetsNoCookie" /> says to — this refusal leaves by
    /// exception too, so the absent header is ASP.NET Core clearing the response and the count over
    /// <c>session_tokens</c> is the half that discriminates.
    /// </remarks>
    [Test]
    public async Task ARefusedRedemption_StoresNoHandleAndSetsNoCookie()
    {
        // Arrange — a real account holding a real set, so the refusal is about the value presented.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await IssueSetAsync(client, device, userId, Verifiers());

        // Act — a well-formed verifier of the right width that no row was ever written for.
        HttpResponseMessage response = await RedeemAsync(host.Factory.CreateClient(), Verifiers(1)[0]);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(SetCookieHeadersOf(response)).IsEqualTo(string.Empty);
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from session_tokens"))
            .IsEqualTo(0L);
    }

    /// <summary>
    /// That the redemption response says what the session is and what the card has left, and hands over
    /// neither the handle nor a way of naming the session.
    /// </summary>
    /// <remarks>
    /// The member list is the assertion leg's claim on this route's own shape — see
    /// <see cref="AVerifiedAssertion_CarriesNeitherTheHandleNorASessionIdentifierInItsBody" /> for why it
    /// is a whole-set comparison and why the payload search beside it is a different question.
    /// </remarks>
    [Test]
    public async Task ARedemption_CarriesNeitherTheHandleNorASessionIdentifierInItsBody()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        string[] verifiers = Verifiers();
        await IssueSetAsync(client, device, userId, verifiers);

        // Act
        HttpResponseMessage response = await RedeemAsync(host.Factory.CreateClient(), verifiers[0]);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        JsonObject body = JsonNode.Parse(payload)!.AsObject();
        string members = string.Join(", ", body.Select(member => member.Key).Order(StringComparer.Ordinal));
        await Assert.That(members).IsEqualTo(RedemptionMembers);

        SessionRow session = await SoleSessionAsync(host.ConnectionString);
        await Assert.That(payload).DoesNotContain(session.Id.ToString("D"));
        await Assert.That(payload).DoesNotContain(session.Id.ToString("N"));
        await Assert.That(payload).DoesNotContain(SessionCookieValueOf(response));
    }

    /// <summary>
    /// That a regeneration which swept a live session sets a cookie, and that the handle names the
    /// session opened over the <b>new</b> set rather than the one it ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the path the whole rule exists for.</b> Somebody who lost their authenticator, redeemed
    /// a code, registered a replacement passkey and is now regenerating the card is signed in on a
    /// session this very request revokes — so unless the response hands back a handle to the session it
    /// opened in its place, they are given ten fresh codes and thrown out of the flow in the same
    /// response, at the worst possible moment.
    /// </para>
    /// <para>
    /// <b>The credential the handle's session hangs off is asserted, not merely that a handle exists.</b>
    /// A session opened over the credential this request just deleted leaves with the cascade, so a
    /// cookie naming it would be a handle to a row that is already gone — and the failure surfaces as a
    /// 401 on the next request, with nothing anywhere naming the cause.
    /// </para>
    /// <para>
    /// The session under the replaced set is written out of band, for the reason
    /// <c>RecoveryCodeGenerationTests</c> gives about its own arrangement: a session on a recovery-code
    /// credential is written by redeeming a code, and driving a redemption here would make this test
    /// depend on that whole path to arrange one row.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ARegenerationThatSweptALiveSession_SetsACookieForTheNewSetsSession()
    {
        // Arrange — a real first set, and one live session hanging off it.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await EnsureOkAsync(await GenerateAsync(client, device, userId));

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid replacedSetId = await ResolveSetCredentialIdAsync(admin, userId);
        await InsertRecoveryCodeSessionAsync(admin, userId, replacedSetId, handle: null);

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body["sessionsEnded"]!.GetValue<int>()).IsEqualTo(1);

        string cookieValue = SessionCookieValueOf(response);
        await Assert.That(Base64UrlText.Decode(cookieValue).Length).IsEqualTo(SessionToken.TokenLength);

        // Exactly one handle, and it names the account's one live session — which is the one over the
        // set this request just issued, not the one it revoked.
        SessionTokenRow handle = await SoleSessionTokenAsync(host.ConnectionString);
        await Assert.That(handle.TokenHash).IsEqualTo(DigestOf(cookieValue));
        await Assert.That(handle.UserId).IsEqualTo(userId);

        Guid newSetId = await ResolveSetCredentialIdAsync(admin, userId);
        await Assert.That(newSetId).IsNotEqualTo(replacedSetId);

        SessionRow[] live = await LiveSessionsAsync(admin, userId);
        await Assert.That(live.Length).IsEqualTo(1);
        await Assert.That(live[0].CredentialId).IsEqualTo(newSetId);
        await Assert.That(handle.SessionId).IsEqualTo(live[0].Id);
    }

    /// <summary>
    /// That a first issue of recovery codes sets no cookie.
    /// </summary>
    /// <remarks>
    /// <b>The negative control for the test above, and without it a handler that minted unconditionally
    /// passes every other test in this file.</b> Writing down a card for the first time signs nobody in:
    /// the codes have never opened a session, nothing was swept, and a cookie handed over here is a
    /// sign-in somebody never made — at onboarding, indistinguishable from a compromise, and revoking it
    /// does not undo having been told it. The response's own <c>session</c> member is asserted null in
    /// the same breath, so a handler that reported nothing while setting a cookie cannot pass on the body
    /// alone.
    /// </remarks>
    [Test]
    public async Task AFirstIssueOfRecoveryCodes_SetsNoCookie()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);

        // No session and no handle before the act, or every row counted afterwards is the arrangement's.
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from sessions"))
            .IsEqualTo(0L);

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body["sessionsEnded"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(body["session"] is null).IsTrue();

        await Assert.That(SetCookieHeadersOf(response)).IsEqualTo(string.Empty);
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from session_tokens"))
            .IsEqualTo(0L);
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from sessions"))
            .IsEqualTo(0L);
    }

    /// <summary>
    /// That a sign-in whose handle cannot be stored leaves no session behind either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The token row is written in the same <c>SaveChanges</c> as the session it opens, and this is
    /// what that buys.</b> A session committed without its handle is a sign-in nobody can present — the
    /// person is told they are in and the very next request is a 401 — and a handle committed without its
    /// session is a cookie naming a row that never existed. Neither is visible from a response: both
    /// answer 200.
    /// </para>
    /// <para>
    /// <b>The failure is induced by taking the grant away, not by faking an exception.</b> <c>INSERT</c>
    /// on <c>session_tokens</c> is revoked from the application role on this host's own database — a
    /// per-database catalog, cloned per host and dropped with it — so the statement that writes the
    /// handle really is refused, by PostgreSQL, with the <c>42501</c> the grant matrix produces. The
    /// revoke happens after the arrangement's requests, so the Development startup block that applies
    /// <c>app-role-grants.sql</c> has already run and does not put the grant back.
    /// </para>
    /// <para>
    /// <b>The consumed code is asserted still there, and that is the sharpest half.</b> The redemption
    /// spends a code before it establishes anything, so a handler whose token write sat outside the unit
    /// of work would leave the code gone and the person holding a card one line shorter with nothing to
    /// show for it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ARedemptionWhoseHandleCannotBeStored_LeavesNeitherTheSessionNorTheSpentCode()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        string[] verifiers = Verifiers();
        await IssueSetAsync(client, device, userId, verifiers);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(
            admin,
            $"revoke insert on session_tokens from {DatabaseProvisioning.AppRoleName}");

        // Act
        HttpResponseMessage response = await RedeemAsync(host.Factory.CreateClient(), verifiers[0]);

        // Assert — not a success, which is the honest answer to a write the role may not make. The
        // status is asserted as "not OK" rather than as a particular code: what this test is about is
        // what the database is left holding, and pinning the shape of an unforeseen failure here would
        // pin an error contract nobody has designed.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);

        // Neither row, and the code unspent: one unit of work, rolled back whole.
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from sessions"))
            .IsEqualTo(0L);
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from session_tokens"))
            .IsEqualTo(0L);
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from recovery_code_hashes"))
            .IsEqualTo((long)RequiredCodeCount);
    }

    /// <summary>
    /// That replacing a set whose session was carrying a handle does not die on a missing
    /// <c>DELETE</c> grant for <c>session_tokens</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>session_tokens</c> is the third table <c>GenerateRecoveryCodesHandler</c>'s
    /// never-materialise rule binds, and it fails the loud way.</b> The replaced set's session rows are
    /// loaded by the revocation sweep and discarded before the credential is removed; the handles hanging
    /// off those sessions must never be loaded at all. Were they tracked, EF would emit its own
    /// <c>DELETE FROM session_tokens</c> — on a table granted <c>SELECT, INSERT</c> and deliberately no
    /// <c>DELETE</c> — and the request would die with <c>42501</c> having removed nothing. Those rows are
    /// meant to leave by the database's own cascade from <c>sessions</c>, which runs with the referencing
    /// table owner's privileges rather than this role's.
    /// </para>
    /// <para>
    /// <b>Do not answer a red here with a grant.</b> The SQLSTATE names a privilege and the cause is the
    /// change tracker; ADR 0019 records the absent <c>DELETE</c> as the thing that makes the mistake
    /// loud, and <c>recovery_code_hashes</c> next door is the table where the same mistake succeeds
    /// silently instead.
    /// </para>
    /// <para>
    /// The arrangement is what the existing session test on that route deliberately leaves out: it writes
    /// the session and <em>the handle it is presented by</em>, which is the only shape in which this trap
    /// exists at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ARegenerationReplacingASetWhoseSessionHadAHandle_DoesNotFailOnAMissingDeleteGrant()
    {
        // Arrange — a real first set, one live session over it, and a stored handle for that session.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await EnsureOkAsync(await GenerateAsync(client, device, userId));

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid replacedSetId = await ResolveSetCredentialIdAsync(admin, userId);
        await InsertRecoveryCodeSessionAsync(admin, userId, replacedSetId, handle: TokenBytes(0x5A));

        // One handle before the act, or this test is the plain replacement path with extra steps.
        await Assert.That(await CountAsync(host.ConnectionString, "select count(*) from session_tokens"))
            .IsEqualTo(1L);

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId);

        // Assert — both halves stated, because 500 is the specific answer this test exists to refuse and
        // a bare equality check would report it as "not OK".
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The replaced set's handle left with the cascade, and the only one left is the new session's.
        SessionTokenRow handle = await SoleSessionTokenAsync(host.ConnectionString);
        await Assert.That(handle.TokenHash).IsNotEqualTo(Convert.ToHexString(SHA256.HashData(TokenBytes(0x5A))));
    }

    /// <summary>The account under test on the recovery-code paths.</summary>
    private const string Subject = "google-cookie-issuing";

    /// <summary>
    /// The second account: the one whose set makes "this code's owner" measurable against "the only set
    /// in the table".
    /// </summary>
    private const string OtherSubject = "google-cookie-issuing-bystander";

    /// <summary>The subject and address of the account the assertion path signs in as.</summary>
    private const string OwnerSubject = "google-cookie-issuing-owner";

    private const string OwnerEmail = "cookie-issuing-owner@budgetoid.test";

    /// <summary>The name of the seeded budget content, distinctive enough to find in a payload.</summary>
    private const string OwnerAccountName = "Issuing Owners Current Account";

    /// <summary>One <c>sessions</c> row, in the columns this file asks about.</summary>
    private sealed record SessionRow(Guid Id, Guid UserId, Guid CredentialId, string Kind);

    /// <summary>
    /// One <c>session_tokens</c> row: the digest as hex, and the two ids it pairs.
    /// </summary>
    /// <remarks>
    /// Hex rather than bytes, so a failure prints a value a reader can compare by eye against the digest
    /// the test computed from the cookie.
    /// </remarks>
    private sealed record SessionTokenRow(string TokenHash, Guid SessionId, Guid UserId);

    /// <summary>Runs both anonymous legs of a sign-in and posts what the device produced.</summary>
    private static async Task<HttpResponseMessage> PostAssertionAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId)
    {
        byte[] challenge = await BeginCeremonyAsync(client, AssertionOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));

        return await client.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
    }

    /// <summary>
    /// Sends one request carrying the first-party client header and the session cookie, verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cookie is written onto the request by hand rather than left to the client's own jar, and that
    /// is not fussiness: the cookie is <c>Secure</c> and the test host answers on <c>http://localhost</c>,
    /// so a cookie container would correctly decline to send it back and the request under test would
    /// arrive carrying nothing at all — a 401 that reads exactly like the feature not working.
    /// </para>
    /// <para>
    /// The header goes on every request because the CSRF control refuses one without it before anything
    /// looks at the cookie — see <see cref="FirstPartyRequestTests" />, which owns that claim. A test here
    /// that omitted it would be reading a 403 and calling it a rejected handle.
    /// </para>
    /// </remarks>
    private static Task<HttpResponseMessage> SendWithCookieAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string cookieValue)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Add(FirstPartyRequestTests.ClientHeader, FirstPartyRequestTests.ClientHeaderValue);
        request.Headers.Add("Cookie", $"{CookieName}={cookieValue}");

        return client.SendAsync(request);
    }

    /// <summary>Presents one verifier to the redemption route.</summary>
    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string verifier) =>
        client.PostAsJsonAsync(RedemptionPath, new Dictionary<string, string> { ["verifier"] = verifier });

    /// <summary>
    /// Runs the whole issuing ceremony: the re-authentication options leg, the device answering the nonce
    /// it issued, and the generation post.
    /// </summary>
    /// <remarks>
    /// <c>signCount</c> stays at zero on every ceremony here, which is what an authenticator backing a
    /// synced passkey reports, so one device proves presence as many times as a test needs.
    /// </remarks>
    private static async Task<HttpResponseMessage> GenerateAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        IReadOnlyList<string>? verifiers = null)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);

        return await client.PostAsJsonAsync(RecoveryCodesPath, new
        {
            codes = SubmissionsOf(verifiers ?? Verifiers()),
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
    }

    /// <summary>Issues one set through the real route, failing loudly on any non-success status.</summary>
    /// <remarks>
    /// A silent no-op here would leave the redemption under test matching against a set that was never
    /// written, which for a refusal assertion reads as a pass.
    /// </remarks>
    private static async Task IssueSetAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        IReadOnlyList<string> verifiers) =>
        await EnsureOkAsync(await GenerateAsync(client, device, userId, verifiers));

    /// <summary>
    /// One whole submission per verifier: the verifier, a factor of its own, and the pair of envelopes
    /// sealed under that code's key.
    /// </summary>
    /// <remarks>
    /// Ten submissions and never ten verifiers beside one factor and one pair — a set is ten separate
    /// secrets and the client derives a key-encryption key from each <em>code</em>. Fresh every time,
    /// because <c>factor_id</c> is <c>PK_wrapped_account_keys</c> and therefore unique table-wide, and
    /// half this file issues twice.
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
    /// answers to.
    /// </summary>
    /// <remarks>
    /// The account is established first, on the one route group allowed to mint one: neither passkey leg
    /// provisions and neither does <c>/api/me/*</c>, so without this line the very first request is
    /// refused and every test here would be red for a reason it is not about.
    /// </remarks>
    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        await ApiFactory.EstablishAccountAsync(client);

        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        WrappedKeyFixture keys = WrappedKeyFixture.Mint();
        await EnsureOkAsync(await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedContentKey = keys.WrappedContentKey,
            wrappedIndexKey = keys.WrappedIndexKey,
        }));
    }

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        await EnsureOkAsync(response);
        JsonNode options = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// Fails naming the status and the body when an arrangement's own request did not succeed.
    /// </summary>
    /// <remarks>
    /// <c>EnsureSuccessStatusCode</c> throws without the body, and every refusal on these routes says
    /// which of several 401s answered in exactly that body. A failed arrangement reported as "401" and
    /// nothing else is a debugging session; reported with its sentence it is one line.
    /// </remarks>
    private static async Task<HttpResponseMessage> EnsureOkAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        throw new InvalidOperationException(
            $"An arrangement request failed with {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// <paramref name="count" /> distinct verifiers of <see cref="VerifierLength" /> bytes each, base64url
    /// encoded exactly as a browser would send them.
    /// </summary>
    private static string[] Verifiers(int count = RequiredCodeCount) =>
    [
        .. Enumerable.Range(0, count)
            .Select(_ => Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength))),
    ];

    /// <summary>A token of <see cref="SessionToken.TokenLength" /> bytes, every one of them the fill.</summary>
    private static byte[] TokenBytes(byte fill) => [.. Enumerable.Repeat(fill, SessionToken.TokenLength)];

    /// <summary>The SHA-256 of the value a cookie carries, as hex, computed without production code.</summary>
    private static string DigestOf(string cookieValue) =>
        Convert.ToHexString(SHA256.HashData(Base64UrlText.Decode(cookieValue)));

    /// <summary>
    /// The value of the session cookie this response sets, or a sentence saying what arrived instead.
    /// </summary>
    /// <remarks>
    /// Throwing rather than returning null, so a missing header fails at the line that wanted it and says
    /// which headers the response really carried. Every caller goes on to decode the value, and a null
    /// there would fail as a reference exception naming nothing.
    /// </remarks>
    private static string SessionCookieValueOf(HttpResponseMessage response)
    {
        string[] headers = SetCookieHeaderList(response);
        string? issued = headers.FirstOrDefault(header =>
            header.StartsWith($"{CookieName}=", StringComparison.Ordinal));

        if (issued is null)
        {
            throw new InvalidOperationException(
                $"The response set no '{CookieName}' cookie. Set-Cookie: "
                + (headers.Length == 0 ? "<none>" : string.Join(" | ", headers)));
        }

        string value = SetCookieHeaderValue.Parse(issued).Value.ToString();

        return value.Length > 0
            ? value
            : throw new InvalidOperationException($"The '{CookieName}' cookie was set to an empty value.");
    }

    /// <summary>
    /// Every <c>Set-Cookie</c> header on the response, joined — empty when there are none.
    /// </summary>
    /// <remarks>
    /// Joined rather than counted so the assertion that no cookie was set fails carrying the header that
    /// was, which is the difference between a number and a diagnosis.
    /// </remarks>
    private static string SetCookieHeadersOf(HttpResponseMessage response) =>
        string.Join(" | ", SetCookieHeaderList(response));

    private static string[] SetCookieHeaderList(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? [.. values] : [];

    /// <summary>The instant the session cookie is written to expire at.</summary>
    private static DateTimeOffset ExpiryOfCookie(HttpResponseMessage response)
    {
        string issued = SetCookieHeaderList(response)
            .First(header => header.StartsWith($"{CookieName}=", StringComparison.Ordinal));

        return SetCookieHeaderValue.Parse(issued).Expires
               ?? throw new InvalidOperationException("The session cookie carries no expiry.");
    }

    /// <summary>
    /// The session expiry off a response body, read as text and normalised to UTC.
    /// </summary>
    /// <remarks>
    /// Parsed rather than deserialized through <c>GetValue&lt;DateTime&gt;</c>, so an instant serialized
    /// without an offset is read as the UTC it is instead of being reinterpreted in the runner's zone —
    /// which would make the comparison pass or fail on where the test ran.
    /// </remarks>
    private static DateTime ExpiryOf(JsonObject body) => DateTime.Parse(
        body["expiresAtUtc"]!.GetValue<string>(),
        CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();

    private static async Task<string> EmailOfAsync(HttpResponseMessage response) =>
        (await ReadJsonObjectAsync(response))["email"]!.GetValue<string>();

    /// <summary>
    /// The one <c>sessions</c> row in the database, or a failure saying how many there were.
    /// </summary>
    /// <remarks>
    /// Sole rather than first: two rows for one sign-in is the replay defect the unit suite pins, and a
    /// test that read the first of them would report a perfectly plausible answer.
    /// </remarks>
    private static async Task<SessionRow> SoleSessionAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select id, user_id, credential_id, kind from sessions order by created_at_utc",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<SessionRow> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(new SessionRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3)));
        }

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException($"Expected exactly one session, found {rows.Count}.");
    }

    /// <summary>The one <c>session_tokens</c> row, or a failure saying how many there were.</summary>
    private static async Task<SessionTokenRow> SoleSessionTokenAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select encode(token_hash, 'hex'), session_id, user_id from session_tokens",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<SessionTokenRow> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(new SessionTokenRow(
                reader.GetString(0).ToUpperInvariant(),
                reader.GetGuid(1),
                reader.GetGuid(2)));
        }

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException($"Expected exactly one session handle, found {rows.Count}.");
    }

    /// <summary>
    /// Every session of one account that nothing has revoked.
    /// </summary>
    /// <remarks>
    /// Scoped by <c>user_id</c> rather than by credential, because the question is what the account is
    /// left holding: a count over one credential cannot see a session opened over the wrong one, and that
    /// is exactly the row a mistaken handler writes.
    /// </remarks>
    private static async Task<SessionRow[]> LiveSessionsAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            """
            select id, user_id, credential_id, kind
            from sessions
            where user_id = @userId and revoked_at_utc is null
            """,
            admin);
        command.Parameters.AddWithValue("userId", userId);

        List<SessionRow> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SessionRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3)));
        }

        return [.. rows];
    }

    /// <summary>
    /// Writes the session a redemption opens over a set, and optionally the handle it is presented by.
    /// </summary>
    /// <remarks>
    /// Out of band because driving a redemption to arrange one row would make these tests depend on that
    /// whole path. The three copied columns must agree with the credential's own, or the composite
    /// foreign key to <c>credentials(id, user_id, type)</c> refuses the row — which is what makes this
    /// arrangement honest rather than a fabricated shape, and the same holds of the handle against
    /// <c>sessions(id, user_id)</c>.
    /// </remarks>
    private static async Task InsertRecoveryCodeSessionAsync(
        NpgsqlConnection admin,
        Guid userId,
        Guid credentialId,
        byte[]? handle)
    {
        Guid sessionId = Guid.CreateVersion7();
        DateTime nowUtc = DateTime.UtcNow;

        await using (NpgsqlCommand command = new(
            """
            insert into sessions
                (id, user_id, credential_id, credential_type, kind, created_at_utc, expires_at_utc)
            values (@id, @userId, @credentialId, 'recovery_codes', 'full', @created, @expires)
            """,
            admin))
        {
            command.Parameters.AddWithValue("id", sessionId);
            command.Parameters.AddWithValue("userId", userId);
            command.Parameters.AddWithValue("credentialId", credentialId);
            command.Parameters.AddWithValue("created", nowUtc);
            command.Parameters.AddWithValue("expires", nowUtc.AddHours(1));

            // One row, or the test that reads it is measuring an arrangement that never happened.
            await Assert.That(await command.ExecuteNonQueryAsync()).IsEqualTo(1);
        }

        if (handle is null)
        {
            return;
        }

        await using NpgsqlCommand token = new(
            "insert into session_tokens (token_hash, session_id, user_id) values (@hash, @sessionId, @userId)",
            admin);
        token.Parameters.AddWithValue("hash", SHA256.HashData(handle));
        token.Parameters.AddWithValue("sessionId", sessionId);
        token.Parameters.AddWithValue("userId", userId);
        await Assert.That(await token.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

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
    /// Writes one row of budget content with raw SQL on the container superuser connection.
    /// </summary>
    /// <remarks>
    /// Raw SQL rather than the domain factory through EF, because <c>Account</c> carries the
    /// <c>BudgetIsolation</c> query filter and a seeding context is built without an <c>IBudgetContext</c>
    /// to satisfy it. <c>USD</c> is a currency the migration seeds.
    /// </remarks>
    private static async Task SeedAccountAsync(RepositoryTestHost host, Guid budgetId, string name)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            insert into accounts (id, budget_id, name, type, opening_balance, currency_code, created_at_utc)
            values (@id, @budget_id, @name, 'Checking', 0, 'USD', @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("budget_id", budgetId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("created_at_utc", DateTime.UtcNow);

        if (await command.ExecuteNonQueryAsync() is not 1)
        {
            throw new InvalidOperationException("Seeding an account wrote something other than one row.");
        }
    }

    /// <summary>Runs one statement on a connection the caller owns.</summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs a counting query on the container superuser connection, refusing anything that is not a count.
    /// </summary>
    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);

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
}
