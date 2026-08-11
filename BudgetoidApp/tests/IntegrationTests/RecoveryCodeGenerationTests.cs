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
/// Issuing an account's set of recovery codes over real HTTP, and issuing it again: what the gate
/// refuses, what a set is allowed to look like, what a replacement takes with it, and what comes back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server never sees a code.</b> The browser mints each code, derives a verifier
/// <c>V = HKDF(code, …)</c> and sends only <c>V</c> as base64url; the row stores <c>SHA-256(V)</c>. So
/// the server cannot check entropy — a set of ten identical zero-filled verifiers is indistinguishable
/// here from a set a good generator produced — and what it can pin is the set's exact size, each
/// verifier's exact decoded width, and that the set's members are distinct. A reader looking for an
/// entropy test here should not conclude it is missing everywhere: it is a client-side property
/// verified by a client-side test.
/// </para>
/// <para>
/// <b><see cref="Generation_ReturnsNoRecoveryCodeAndNoVerifierOnTheWire" /> is the test this whole
/// feature exists to keep true.</b> Nothing a redemption could use may leave this endpoint. It is
/// asserted against the response <em>as it went over the wire</em> rather than against a deserialized
/// shape, because a value the serializer escaped — or one folded into a member some later widening
/// added — reads as absent through a parsed document.
/// </para>
/// <para>
/// <b>The ordering of the gate and the validation is a rule, not a reading preference, and
/// <see cref="Generation_WithAMalformedSetAndNoFreshAssertion_IsRefusedOnTheAssertion" /> is the only
/// thing that says so from outside.</b> Validating first would answer an unproven caller with the
/// required set size and the required verifier width — the two facts a client needs to present a set at
/// all — for a caller holding nothing but a stolen bearer token. Past the gate those same sentences cost
/// nothing, because the caller has proved possession of an authenticator registered to this account and
/// there is nobody left to enumerate about; that is why the refusals in
/// <see cref="Generation_WithAMalformedSet_IsRefusedWithASentenceAndWritesNothing" /> are 400s with real
/// sentences while every gate refusal is one byte-identical 401.
/// </para>
/// <para>
/// <b>Every ceremony here reports a signature counter of zero</b>, which is what an authenticator
/// backing a synced passkey does. <c>PasskeySignatureCounter.Accept</c> reads a repeated zero as no
/// movement rather than as a clone, so one device can prove presence as many times as a test needs
/// without the test keeping a counter ledger — which matters more on this route than on any other,
/// because issuing <em>replaces</em> and half these tests have to issue twice.
/// </para>
/// <para>
/// Every row is counted on <see cref="PostgresTestHost.ConnectionString" /> — the container superuser —
/// and never on the application role. <c>sessions</c> carries <c>user_isolation</c>, which is
/// <c>FOR ALL</c>, so a policed connection reports zero rows for a session that is still there exactly as
/// it does for one that is gone.
/// </para>
/// <para>
/// No test here names a production type belonging to this story. They address the route over HTTP and
/// read the wire body, so while the endpoint is unmapped they fail on the status assertion against a real
/// 404 rather than failing to compile — the difference between a red test that is telling us something
/// and one that is telling us nothing.
/// </para>
/// </remarks>
public sealed class RecoveryCodeGenerationTests
{
    private const string RecoveryCodesPath = "/api/me/recovery-codes";

    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";

    /// <summary>The account under test in most of the file.</summary>
    private const string Subject = "google-issuing";

    /// <summary>
    /// The bystander account: the one whose set another account's regeneration must not touch, and the
    /// one whose mere existence makes the owner predicate on the set lookup measurable.
    /// </summary>
    private const string OtherSubject = "google-issuing-bystander";

    /// <summary>
    /// How many codes an issued set holds. Restated here rather than read off the handler, because the
    /// number is the pin: a test taking its expectation from the type under test agrees with whatever
    /// that type later decides.
    /// </summary>
    private const int RequiredCodeCount = 10;

    /// <summary>The exact width of a verifier, decoded.</summary>
    private const int VerifierLength = 32;

    /// <summary>
    /// The one member of the generation response, named once so the test asserting it is present and the
    /// test reading its value cannot drift apart from each other.
    /// </summary>
    private const string SessionsEndedMember = "sessionsEnded";

    /// <summary>
    /// The members of the generation response, joined exactly as
    /// <see cref="Generation_ResponseCarriesTheSessionCountAndNothingElse" /> builds them. It is
    /// <see cref="SessionsEndedMember" /> today because the record has one member; the constant exists so
    /// that a second member arriving is a comparison of two strings rather than of two numbers.
    /// </summary>
    private const string GenerationMembers = SessionsEndedMember;

    /// <summary>
    /// The <c>ceremony</c> value the re-authentication pool is filed under, as the column stores it.
    /// </summary>
    private const string ReauthenticationCeremony = "reauthentication";

    /// <summary>
    /// The member of a problem-details body that is a new value on every request rather than on every
    /// cause, and therefore the one member a comparison of two refusals must not compare.
    /// </summary>
    private const string TraceIdMember = "traceId";

    /// <summary>
    /// How many refusals <see cref="EveryReachableGenerationRefusal_ProducesTheIdenticalResponse" />
    /// drives. Named so that deleting one from the list is a failing test rather than a shorter and still
    /// perfectly green one.
    /// </summary>
    private const int ReachableGenerationRefusals = 9;

    /// <summary>
    /// The happy path: a fresh assertion, ten well-formed verifiers, and ten rows that hold the hash of
    /// each and none of the verifiers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the control for every refusal in the file.</b> A route that was never mapped refuses
    /// every caller and satisfies all of them; a route behind a gate nobody can clear satisfies all of
    /// them while the feature does not exist. This is the test that says the door opens for somebody.
    /// </para>
    /// <para>
    /// The rows are compared by value rather than counted. Ten rows and ten <em>correct</em> rows are the
    /// same number, and only the values say which: a handler that stored the verifier itself as the
    /// "hash", or hashed the wrong member, or hashed twice, leaves a count of ten behind and hands a
    /// database reader ten live recovery codes.
    /// </para>
    /// <para>
    /// One <c>credentials</c> row of type <c>recovery_codes</c> stands for the whole set, and every hash
    /// hangs off it. That is what the cascade — and therefore the replacement below — turns on.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_AfterAFreshReauthentication_StoresOneHashPerVerifier()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        string[] verifiers = Verifiers();

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // No set before the act, or every row counted afterwards is a row the arrangement produced.
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(0L);

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId, verifiers);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Ordered hex on both sides, so the claim is about the set of stored values rather than about
        // the order the rows came back in.
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));

        // One credential for the whole set, and every code filed against it.
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
        Guid setId = await ResolveSetCredentialIdAsync(admin, userId);
        await Assert.That(await CountCodesOfSetAsync(admin, setId)).IsEqualTo((long)RequiredCodeCount);
    }

    /// <summary>
    /// That the response carries exactly <c>sessionsEnded</c>, and no second member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Green the day it is written, and that is the point rather than an apology.</b> Nothing else in
    /// either suite goes red when a second member starts arriving here: every other test on this route
    /// reads the status, the rows behind it, or <c>sessionsEnded</c> alone, and all of them keep passing
    /// beside a <c>credentialId</c> or a <c>generatedAtUtc</c>. The defect this exists to catch is one a
    /// later reader adds the day a client wants to refresh its own view from the response.
    /// </para>
    /// <para>
    /// <b>Never <c>ContainsKey</c>, and that is the whole shape of the assertion.</b> A containment check
    /// over member names can never fail: every widening leaves <c>sessionsEnded</c> present and the check
    /// green. The members are joined and compared whole, joined rather than counted so that a failure
    /// names the member that arrived instead of reporting that "1 != 2".
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_ResponseCarriesTheSessionCountAndNothingElse()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId);

        // Assert — the status first, so a body that is missing because the request was refused reads as
        // the refusal it is rather than as a member list nobody would recognise as a 401.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Ordered before joining, so a second member produces the same message whichever order the
        // serializer emitted it in — a red that reads differently between runs is a red people stop
        // trusting.
        JsonObject body = await ReadJsonObjectAsync(response);
        string members = string.Join(", ", body.Select(member => member.Key).Order(StringComparer.Ordinal));

        await Assert.That(members).IsEqualTo(GenerationMembers);

        // And it really is a number, so a member of the right name carrying the codes cannot pass.
        await Assert.That(body[SessionsEndedMember]!.GetValue<int>()).IsEqualTo(0);
    }

    /// <summary>
    /// No code and no verifier comes back — asserted against the payload exactly as it arrived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The server never held a code, so there is nothing it could return</b>, and the client already
    /// holds the ten it derived its verifiers from. What this refuses is the shape a later reader
    /// reaches for when a client wants to re-render the card: echoing the verifiers back, or returning
    /// the stored hashes so the client can "confirm" them. Either turns a response body into a value a
    /// redemption could be attempted with, and puts it in every client log on the way.
    /// </para>
    /// <para>
    /// <b>Both spellings of the secret are searched for, and neither is redundant.</b> The verifier is
    /// what the client sent; the hash is what the row holds, and a handler echoing its own stored value
    /// back would satisfy a search for the verifier alone. Both are searched for in the two encodings
    /// this exchange uses — base64url, which is how a verifier crosses JSON, and hex, which is how a
    /// reader of the table would quote one.
    /// </para>
    /// <para>
    /// Read as raw text rather than re-rendered from a parsed document, for the reason
    /// <c>CredentialListEndpointTests.Credentials_NeverCarryTheFederatedSubject</c> gives: re-rendering
    /// puts the check at the mercy of whichever characters the serializer's encoder escapes, and a value
    /// hidden behind an escape sequence is a leak a search over the re-rendered text reports as absent.
    /// The status is asserted first so an empty 404 body cannot pass as a payload that happens to
    /// contain nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_ReturnsNoRecoveryCodeAndNoVerifierOnTheWire()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        string[] verifiers = Verifiers();

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId, verifiers);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        foreach (string verifier in verifiers)
        {
            byte[] decoded = Base64UrlText.Decode(verifier);

            // The verifier as the client sent it, and as a table reader would quote it.
            await Assert.That(payload).DoesNotContain(verifier);
            await Assert.That(payload).DoesNotContain(Convert.ToHexString(decoded));

            // And the value the row actually holds, in both encodings, so a response echoing the stored
            // hash rather than the verifier cannot pass the two checks above.
            byte[] hash = SHA256.HashData(decoded);
            await Assert.That(payload).DoesNotContain(Base64UrlText.Encode(hash));
            await Assert.That(payload).DoesNotContain(Convert.ToHexString(hash));
        }
    }

    /// <summary>
    /// A second issue replaces the first: 200 again, ten rows again, and not one of them survives from
    /// the set the person threw away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>200 on both, because the resource is the account's recovery-code set, singular, and a POST
    /// replaces it.</b> A 201 on the first and a 200 on the second would make a client branch on which
    /// of two states its own account was in before it asked, which is a fact it has no way to know and
    /// no use for. A 409 on the second would refuse the request a person makes precisely when they need
    /// it most — the card is lost, and the codes on it must stop working.
    /// </para>
    /// <para>
    /// <b>Both sets are driven through the real route</b> rather than the first being seeded, so the
    /// replaced set is a set this product wrote. The previous hashes are read back before the act and
    /// compared by value afterwards: twenty rows and ten rows are both distinguishable from ten
    /// <em>correct</em> rows only if the values are compared, and a handler that deleted the new set
    /// instead of the old one leaves a count of ten and an account whose codes are all stale.
    /// </para>
    /// <para>
    /// The set's <c>credentials</c> row is asserted to have changed identity as well, because that row
    /// is what the codes cascade from: a handler that reused it would leave the old codes' foreign key
    /// intact and the delete with nothing to take.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_ForAnAccountThatAlreadyHoldsASet_ReplacesItAndAnswersOk()
    {
        // Arrange — the first set, issued through the route it will be replaced through.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);

        HttpResponseMessage first = await GenerateAsync(client, device, userId);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        string[] previousHashes = await StoredHashesAsync(admin, userId);
        Guid previousSetId = await ResolveSetCredentialIdAsync(admin, userId);
        await Assert.That(previousHashes.Length).IsEqualTo(RequiredCodeCount);

        // Act — the same account, a fresh proof, a set of ten new verifiers.
        string[] verifiers = Verifiers();
        HttpResponseMessage second = await GenerateAsync(client, device, userId, verifiers);

        // Assert — 200 on a regeneration exactly as on a first issue.
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The account holds one set, it is the new one, and none of the old codes survived.
        string[] stored = await StoredHashesAsync(admin, userId);
        await Assert.That(stored).IsEquivalentTo(ExpectedHashesOf(verifiers));
        await Assert.That(stored.Any(hash => previousHashes.Contains(hash, StringComparer.Ordinal))).IsFalse();

        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, userId)).IsNotEqualTo(previousSetId);
    }

    /// <summary>
    /// A set holding a live session is replaced, and the request answers <b>200 and not 500</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Named as an outcome on purpose, because the failure names a permission and the cause is the
    /// change tracker.</b> Replacing a set ends the sessions it opened, which loads every unrevoked
    /// <c>Session</c> of its credential into EF's change tracker. Remove the <c>Credential</c> with those
    /// dependents still tracked and EF cascades into the copies it can see and emits its own
    /// <c>DELETE FROM sessions</c> — on a table granted <c>SELECT, INSERT, UPDATE (revoked_at_utc)</c>
    /// and deliberately <b>no</b> <c>DELETE</c> — so the request dies with <c>42501</c> and a 500 before
    /// it removes anything. The fix is a second <c>DiscardTrackedEntities()</c> between the sweep and the
    /// delete, and no unit test can prove it: the fakes have no grants.
    /// </para>
    /// <para>
    /// <b>Do not answer that 500 with a grant on <c>sessions</c>.</b> The SQLSTATE names a privilege and
    /// the cause is the tracker; the absent <c>DELETE</c> is what keeps a session accountable, and
    /// <c>AppRoleGrantsTests.Database_RefusesToDeleteASession</c> pins it. The session rows are meant to
    /// leave by the database's own <c>ON DELETE CASCADE</c> from <c>credentials</c>, which runs with the
    /// referencing table owner's privileges rather than this role's. This is the twin of
    /// <see cref="CredentialRevocationTests.Revocation_WhenTheCredentialHasLiveSessions_DoesNotFailOnAMissingSessionDeleteGrant" />,
    /// which was confirmed to redden under removal of that discard on its own path.
    /// </para>
    /// <para>
    /// <b>The session is written out of band, and that is a departure this test owes an argument for.</b>
    /// A session on a recovery-code credential is written by redeeming a code, and the redemption
    /// endpoint is the next story — so there is no product path to arrange one today. The row is exactly
    /// the row that path will write: the set's own credential, <c>credential_type = 'recovery_codes'</c>
    /// and therefore <c>kind = 'full'</c>, which <c>CK_sessions_kind_matches_credential</c> is what makes
    /// checkable. The mechanism under test is reachable now, so the test is written now rather than left
    /// for a story that has no reason to look for it. Delete this arrangement the day a redemption can
    /// be driven, not before.
    /// </para>
    /// <para>
    /// <c>sessionsEnded</c> is asserted beside the status, because a 200 alone is also what a handler
    /// that never swept anything answers — and then the person who regenerated their codes would still
    /// be signed in on the device holding the card they just replaced.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_WhenTheReplacedSetHasLiveSessions_DoesNotFailOnAMissingSessionDeleteGrant()
    {
        // Arrange — a real first set, then one live session hanging off it.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);

        HttpResponseMessage first = await GenerateAsync(client, device, userId);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setId = await ResolveSetCredentialIdAsync(admin, userId);
        await InsertRecoveryCodeSessionAsync(admin, userId, setId);

        // At least one live session, because a set that opened none loads nothing into the tracker and
        // this test would then be measuring the plain replacement path.
        await Assert.That(await CountLiveSessionsAsync(admin, setId)).IsEqualTo(1L);

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId);

        // Assert — both halves stated, because 500 is the specific answer this test exists to refuse and
        // a bare equality check would report it as "not OK".
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // And the sweep really ran, so a 200 that ended nothing cannot pass.
        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body.ContainsKey(SessionsEndedMember)).IsTrue();
        await Assert.That(body[SessionsEndedMember]!.GetValue<int>()).IsEqualTo(1);
    }

    /// <summary>
    /// One account regenerating leaves another account's set exactly where it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the isolation test of the file.</b> <c>credentials</c> is exempt from row-level
    /// security — it is the table a request is resolved <em>out of</em> — so the lookup that finds the
    /// set about to be replaced is scoped by the application's owner predicate and by nothing beneath
    /// it: no policy narrows it, no query filter narrows it, and the grant is on the whole table. With
    /// that predicate gone, one account's regeneration reaches for whatever set it finds, and the
    /// account that loses its codes is not the one that asked.
    /// </para>
    /// <para>
    /// Both accounts hold a set before the act, which is what makes the predicate measurable at all:
    /// with one account seeded, "this account's set" and "the only set in the table" are the same row
    /// and dropping the owner changes no answer anywhere.
    /// </para>
    /// <para>
    /// The bystander's codes are compared by value rather than counted, for the reason the replacement
    /// test gives: ten rows is what a correct run leaves and also what a run that replaced the wrong
    /// account's set leaves.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_ForOneAccount_LeavesAnotherAccountsSetWhereItWas()
    {
        // Arrange — two established accounts, each holding a set of its own.
        await using PostgresTestHost host = await StartHostAsync();

        HttpClient alice = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator alicesDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(alice, alicesDevice);
        Guid aliceId = await ResolveUserIdAsync(host, Subject);

        HttpClient bob = host.Factory.CreateAuthenticatedClient(OtherSubject);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(bob, bobsDevice);
        Guid bobId = await ResolveUserIdAsync(host, OtherSubject);

        await Assert.That((await GenerateAsync(alice, alicesDevice, aliceId)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await GenerateAsync(bob, bobsDevice, bobId)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        string[] bobsHashes = await StoredHashesAsync(admin, bobId);
        Guid bobsSetId = await ResolveSetCredentialIdAsync(admin, bobId);
        await Assert.That(bobsHashes.Length).IsEqualTo(RequiredCodeCount);

        // Act — Alice replaces her own set, with a proof that is hers and a body that names nobody.
        string[] verifiers = Verifiers();
        HttpResponseMessage response = await GenerateAsync(alice, alicesDevice, aliceId, verifiers);

        // Assert — Alice's set moved.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StoredHashesAsync(admin, aliceId)).IsEquivalentTo(ExpectedHashesOf(verifiers));

        // And Bob's did not — the same ten values, under the same credential.
        await Assert.That(await StoredHashesAsync(admin, bobId)).IsEquivalentTo(bobsHashes);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, bobId)).IsEqualTo(bobsSetId);
        await Assert.That(await CountSetsAsync(admin, bobId)).IsEqualTo(1L);
    }

    /// <summary>
    /// Every refusal the re-authentication gate in front of this route can produce, driven end to end and
    /// compared whole — status and body together, and all of them against each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this test the gate has no coverage on this route at all.</b> With the gate gone a
    /// stolen bearer token on its own mints a set of recovery codes — and a set of recovery codes is a
    /// full-session credential, so whoever holds one signs in and reaches the account's content without
    /// the authenticator. Because issuing <em>replaces</em>, the same request destroys the real set in
    /// the same breath, which is what makes this the most valuable single request an attacker holding a
    /// token can make.
    /// </para>
    /// <para>
    /// <b>The account already holds a set, and the assertion at the end is that it still holds the same
    /// one.</b> An account with no set makes "nothing was written" a claim about an empty table, which a
    /// route that refused for any reason at all satisfies. With a real set in place, a refusal that
    /// reached the handler's body would replace it, and the comparison of ten stored values names that.
    /// </para>
    /// <para>
    /// <b>The first entry is what pins the ORDER of the gate and the validation</b> — an empty body is a
    /// request with no proof <em>and</em> no set, and it comes back as the gate's 401 rather than as a
    /// 400 naming the required set size. The dedicated test
    /// <see cref="Generation_WithAMalformedSetAndNoFreshAssertion_IsRefusedOnTheAssertion" /> states that
    /// claim on its own, where a reader will look for it.
    /// </para>
    /// <para>
    /// <b>Compared to each other rather than to a literal, because the property is
    /// indistinguishability.</b> A caller able to tell "that challenge was for another ceremony" from
    /// "that passkey is not yours" is mapping which handles exist and which pool a nonce came from while
    /// holding a stolen token. Two reasons compared against each other prove only that those two agree,
    /// so this drives all of them and asserts they collapse to one value.
    /// </para>
    /// <para>
    /// <b>Deliberately shorter than
    /// <see cref="ErasureReauthenticationTests.EveryReachableErasureRefusal_ProducesTheIdenticalResponse" />.</b>
    /// All three routes call the same <c>PasskeyReauthentication</c>, one implementation with one list of
    /// steps, so the deeper entries that file drives — untrusted origin, user-handle mismatch, a
    /// <c>webauthn.create</c> client-data type, a counter regression — are pinned there and would be
    /// re-running the same code here. What is <i>not</i> covered anywhere else is that this route invokes
    /// that gate at all. Adding entries is welcome, removing one is not, which is what
    /// <see cref="ReachableGenerationRefusals" /> is for.
    /// </para>
    /// <para>
    /// <b>What this list cannot show is how deep an entry got.</b> Nothing here can observe which step
    /// refused a request — that indistinguishability is the property being asserted — so an entry that
    /// quietly began failing at an earlier step than the one it was written for stays green and stops
    /// covering the step it was added for.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryReachableGenerationRefusal_ProducesTheIdenticalResponse()
    {
        // Arrange — an account with a passkey and a real set of codes, plus a bystander whose device is
        // one of the proofs below.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        HttpClient anonymous = host.Factory.CreateClient();
        HttpClient bob = host.Factory.CreateAuthenticatedClient(OtherSubject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator unregistered = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        await RegisterPasskeyAsync(bob, bobsDevice);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        byte[] userHandle = PasskeyEncoding.ToUserHandle(userId);

        await Assert.That((await GenerateAsync(client, device, userId)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        string[] issuedHashes = await StoredHashesAsync(admin, userId);
        await Assert.That(issuedHashes.Length).IsEqualTo(RequiredCodeCount);

        // Act — every entry carries a well-formed set unless it is the one about carrying nothing, so
        // the only reason any of them is refused is the proof in the body.
        List<(string Reason, HttpResponseMessage Response)> refusals = [];

        // Every member absent, set included. The request record declares none of the assertion members
        // required and nothing registers model validation, so {} binds them all to null and the gate's
        // own decode is the first thing to see it — the same 401 a wrong proof gets, rather than a
        // framework 400 telling a caller holding a stolen token that its proof was the thing found
        // wanting, and rather than a 400 naming the set size this body also fails to satisfy.
        refusals.Add(("no assertion members and no set", await client.PostAsJsonAsync(RecoveryCodesPath, new { })));

        // A well-formed set with no proof at all, which is the same 401 — the members are deliberately
        // not `required`, exactly as on erasure and revocation.
        refusals.Add((
            "a well-formed set and no assertion members",
            await client.PostAsJsonAsync(RecoveryCodesPath, new { verifiers = Verifiers() })));

        // A device this account never registered, answering a live nonce of its own. The unknown-handle
        // arm, and the one entry whose proof names nothing the table holds.
        byte[] unknownChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        refusals.Add((
            "unknown credential handle",
            await PostGenerationAsync(
                client,
                unregistered.Authenticate(unknownChallenge, ApiFactory.PasskeyOrigin, userHandle, signCount: 0),
                Verifiers())));

        // The sign-in pool, minted from an ANONYMOUS endpoint: anyone who can walk a person through one
        // WebAuthn prompt for this relying party holds a valid response over a nonce they chose the
        // moment for. A gate checking ConsumeAsync for a non-null answer rather than for Reauthentication
        // passes everything else and fails here.
        byte[] assertionChallenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        refusals.Add((
            "assertion challenge",
            await PostGenerationAsync(
                client,
                device.Authenticate(assertionChallenge, ApiFactory.PasskeyOrigin, userHandle, signCount: 0),
                Verifiers())));

        // The other stale pool. A registration nonce is issued to a signed-in person, which is exactly
        // the stolen-session adversary this gate exists to stop.
        byte[] registrationChallenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        refusals.Add((
            "registration challenge",
            await PostGenerationAsync(
                client,
                device.Authenticate(registrationChallenge, ApiFactory.PasskeyOrigin, userHandle, signCount: 0),
                Verifiers())));

        // Bob's registered passkey answering a live nonce issued to this account. No user handle, so the
        // gate's owner-scoped credential lookup is the only thing that can refuse it — with a handle
        // present, a lookup that had lost its owner filter would still be turned down by the check below
        // it and this entry would stay green over a gate with no binding left.
        byte[] strangerChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        refusals.Add((
            "another account's passkey",
            await PostGenerationAsync(
                client,
                bobsDevice.Authenticate(strangerChallenge, ApiFactory.PasskeyOrigin, userHandle: null, signCount: 0),
                Verifiers())));

        // One challenge answered twice: the tampered attempt burns it, so the second post is a faultless
        // response refused for nothing but the nonce already being spent. A failed attempt that did not
        // consume would make one issued challenge something an attacker grinds responses against.
        byte[] spentChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult answered = device.Authenticate(
            spentChallenge,
            ApiFactory.PasskeyOrigin,
            userHandle,
            signCount: 0);
        refusals.Add((
            "invalid signature",
            await PostGenerationAsync(client, WithFlippedSignature(answered), Verifiers())));
        refusals.Add(("consumed challenge", await PostGenerationAsync(client, answered, Verifiers())));

        // Last, and the ordering is load-bearing: the store sweeps expired rows on every issue, so an
        // expired challenge inserted before any of the options calls above would be collected by one of
        // them and this entry would be refused for a nonce nobody ever issued instead.
        byte[] expiredChallenge = await InsertChallengeAsync(host, ReauthenticationCeremony, expiresInMinutes: -5);
        refusals.Add((
            "expired challenge",
            await PostGenerationAsync(
                client,
                device.Authenticate(expiredChallenge, ApiFactory.PasskeyOrigin, userHandle, signCount: 0),
                Verifiers())));

        List<(string Reason, string Response)> observed = [];
        foreach ((string reason, HttpResponseMessage response) in refusals)
        {
            observed.Add((reason, $"{(int)response.StatusCode} {await ReadComparableBodyAsync(response)}"));
        }

        // Assert — each refusal against the first, with its own name on both sides of the comparison so a
        // failure says which one drifted rather than only that something did.
        string first = observed[0].Response;
        foreach ((string reason, string response) in observed)
        {
            await Assert.That($"{reason} => {response}").IsEqualTo($"{reason} => {first}");
        }

        await Assert.That(observed.Count).IsEqualTo(ReachableGenerationRefusals);
        await Assert.That(observed.Select(entry => entry.Response).Distinct().Count()).IsEqualTo(1);

        // The one value they collapse to is the gate's 401, and not some other response they happen to
        // share: a route that answered every one of these identically for a reason unrelated to the gate
        // would satisfy the comparison above and nothing else here.
        await Assert.That(refusals[0].Response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(first.Contains(PasskeyVerificationExceptionHandler.Title, StringComparison.Ordinal))
            .IsTrue();

        // And no refusal replaced the set on the way to saying no.
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(issuedHashes);
    }

    /// <summary>
    /// A malformed set presented <b>without</b> a fresh assertion is refused on the assertion, and the
    /// refusal is byte-identical to the one a well-formed set with no assertion gets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ordering rule, stated where a reader will look for it.</b> Validating the set first would
    /// answer an unproven caller with the required set size and the required verifier width — the two
    /// facts a client needs to present a set at all — for a caller holding nothing but a bearer token.
    /// It would also hand that caller a way to tell "the server wants ten" from "the server is refusing
    /// me", which is one bit more than a byte-identical 401 gives away.
    /// </para>
    /// <para>
    /// <b>The two bodies are compared whole rather than both being asserted to be 401</b>, and that is
    /// the whole of the pin: a route that validated first would answer the malformed request with a 400
    /// carrying a sentence, and a route that validated first and then <em>also</em> refused with 401
    /// would still leak the sentence in the body. Comparing the malformed request against the
    /// well-formed one makes both mutations name the line that drifted. The status and the title are
    /// asserted beside it, so a route answering two identical 500s cannot satisfy the comparison alone.
    /// </para>
    /// <para>
    /// The set is malformed in the one way that costs nothing to arrange — nine verifiers rather than
    /// ten. Which flavour of malformed it is does not matter: the claim is that the caller learns
    /// nothing about the set at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_WithAMalformedSetAndNoFreshAssertion_IsRefusedOnTheAssertion()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        Guid userId = await ResolveUserIdAsync(host, Subject);

        // Act — neither request carries a proof; one carries a set the server would accept and one
        // carries a set it would refuse with a sentence.
        HttpResponseMessage wellFormed = await client.PostAsJsonAsync(
            RecoveryCodesPath,
            new { verifiers = Verifiers() });
        HttpResponseMessage malformed = await client.PostAsJsonAsync(
            RecoveryCodesPath,
            new { verifiers = Verifiers(RequiredCodeCount - 1) });

        // Assert — the gate answered, and it answered the same thing to both.
        //
        // The malformed response is parsed ONCE and both facts are read off that one document. A
        // response's content stream is handed out once and then sits at its end, so a second read of the
        // same response is not a second look at the payload — it is a parse of nothing, and the test
        // dies on a JSON reader error that says nothing about the route. Reading the title off the very
        // document the comparison uses is also the stronger claim: the title and the compared body are
        // then provably the same bytes rather than two reads that could in principle disagree.
        JsonObject refusal = await ReadJsonObjectAsync(malformed);

        await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(TitleOf(refusal)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That(ComparableBodyOf(refusal)).IsEqualTo(await ReadComparableBodyAsync(wellFormed));

        // And nothing was written on the way to saying no.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(0L);
    }

    /// <summary>
    /// Past the gate, a set the server will not accept is refused with a real sentence and writes
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A 400 with a sentence, and that is legitimate precisely because it is past the gate.</b> The
    /// caller has proved possession of an authenticator registered to this account, so there is nobody
    /// left to enumerate about and a refusal that says what is wrong costs nothing — the same argument
    /// <c>CompleteRegistrationHandler</c> makes for its own sentences. Before the gate the identical
    /// sentence would be a disclosure, which is what the ordering test above is for.
    /// </para>
    /// <para>
    /// <b>Five cases over the four causes the route refuses, and <c>not base64url</c> is the only one
    /// this suite covers anywhere.</b> <c>GenerateRecoveryCodesHandlerTests</c> pins the count from both
    /// sides, the width from both sides and the duplicate against the handler, but it builds every
    /// verifier by encoding bytes — so a member that is not base64url text at all never reaches it, and
    /// the decode is exercised here and nowhere else. The count is driven from both sides here too,
    /// because the two say different things: too few is a person left with fewer ways back into their
    /// account than the screen told them they had, and too many is a client the server no longer agrees
    /// with about what a set is.
    /// </para>
    /// <para>
    /// <b>The account already holds a set</b>, so "nothing was written" is a claim about ten live codes
    /// surviving rather than about an empty table. A handler that deleted the previous set before
    /// validating the new one — the natural shape if the validation drifts down past the read — answers
    /// 400 exactly as this test demands while having already destroyed the codes the person is holding.
    /// That is the defect this arrangement exists to catch, and a status assertion alone cannot see it.
    /// </para>
    /// <para>
    /// That the sentences differ from each other is pinned against the handler, in
    /// <c>GenerateRecoveryCodesHandlerTests.HandleAsync_RefusesEachMalformedSetWithASentenceOfItsOwn</c>,
    /// rather than restated here: this test is about the status, the survival of the previous set, and
    /// that a sentence arrives at all.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("too few")]
    [Arguments("too many")]
    [Arguments("not base64url")]
    [Arguments("wrong width")]
    [Arguments("duplicated")]
    public async Task Generation_WithAMalformedSet_IsRefusedWithASentenceAndWritesNothing(string cause)
    {
        // Arrange — a real set first, so the refusal has something to fail to destroy.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);

        await Assert.That((await GenerateAsync(client, device, userId)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        string[] issuedHashes = await StoredHashesAsync(admin, userId);
        Guid issuedSetId = await ResolveSetCredentialIdAsync(admin, userId);

        // Act — a genuine, fresh proof and a set the server will not take.
        HttpResponseMessage response = await GenerateAsync(client, device, userId, MalformedSet(cause));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // A sentence, not an empty errors bag: a refusal keyed on a field with no message is a 400 that
        // tells a person who has already proved presence nothing they can act on.
        string[] messages = await ReadValidationMessagesAsync(response);
        await Assert.That(messages.Length).IsGreaterThan(0);
        await Assert.That(messages.All(message => !string.IsNullOrWhiteSpace(message))).IsTrue();

        // And the codes the person is holding are still the codes the account will accept.
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(issuedHashes);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, userId)).IsEqualTo(issuedSetId);
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
    }

    /// <summary>
    /// An authenticated subject with no account behind it is refused, and the refusal writes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row counts are the half of this test that carries the weight.</b> A status assertion cannot
    /// tell a route that refuses from a route that mints an account and <em>then</em> refuses — and a
    /// provider id token stays valid for up to an hour after the account it names is erased, so a
    /// <c>ProvisionsUser</c> marker arriving on this group would turn one retried POST into a
    /// resurrected, passkey-less account that the re-authentication gate in front of erasure can never
    /// remove again. This route must never gain that marker.
    /// </para>
    /// <para>
    /// <b>Counted unscoped, on the superuser connection.</b> The id an accidental marker would mint is
    /// one no assertion here could name, so a count filtered to this subject would pass over the very row
    /// it exists to catch — and <c>users</c>, <c>budgets</c> and <c>sessions</c> are policed by
    /// <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed connection reports zero rows for a row
    /// that is still there exactly as it does for one that was never written.
    /// </para>
    /// <para>
    /// The title is asserted before the counts, because it is what makes them meaningful: it says the
    /// request reached the provisioning middleware and was refused there. An unmapped path answers 404
    /// and leaves the same empty tables behind, so the counts on their own prove nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_ForAnAuthenticatedSubjectWithNoAccount_IsRefusedAndCreatesNothing()
    {
        // Arrange — an authenticated client that has deliberately never called EstablishAccountAsync.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-issuing-unprovisioned");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            RecoveryCodesPath,
            new { verifiers = Verifiers() });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(UserProvisioningMiddleware.NoAccountTitle);

        await Assert.That(await ScalarAsync(admin, "select count(*) from users")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from credentials")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from budgets")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from recovery_code_hashes")).IsEqualTo(0L);
    }

    /// <summary>
    /// A caller carrying no token at all is refused by the fallback policy, and the title says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three refusals answer 401 on this route and the status cannot tell them apart: the fallback
    /// authorization policy turning an anonymous caller away before the route is reached, the
    /// provisioning middleware finding no account for an authenticated principal, and the
    /// re-authentication gate refusing a proof. Only the middleware's carries
    /// <see cref="UserProvisioningMiddleware.NoAccountTitle" /> and only the gate's carries
    /// <see cref="PasskeyVerificationExceptionHandler.Title" />; the anonymous one is titled
    /// <c>"Unauthorized"</c> from the status code alone, because <c>UseStatusCodePages</c> writes it with
    /// no title of its own.
    /// </para>
    /// <para>
    /// Both inequalities, because each rules out a different way the door could be standing open. Without
    /// the middleware one, a route that had lost its authorization entirely still passes here — an
    /// anonymous request would walk on to the provisioning middleware, find no account for a principal it
    /// cannot even name, and be answered that middleware's 401. Without the gate one, a route reached
    /// anonymously and refused only by the ceremony would pass, and that would mean an unauthenticated
    /// caller reaching a handler at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_ByAnUnauthenticatedCaller_IsRefusedWithATitleTheGateNeverWrites()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act — no subject header, so nothing authenticates and the fallback policy decides.
        HttpResponseMessage response = await host.Factory
            .CreateClient()
            .PostAsJsonAsync(RecoveryCodesPath, new { verifiers = Verifiers() });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        string title = await ReadTitleAsync(response);
        await Assert.That(title).IsNotEqualTo(UserProvisioningMiddleware.NoAccountTitle);
        await Assert.That(title).IsNotEqualTo(PasskeyVerificationExceptionHandler.Title);
    }

    /// <summary>
    /// <paramref name="count" /> distinct verifiers of <paramref name="length" /> bytes each, base64url
    /// encoded exactly as a browser would send them.
    /// </summary>
    /// <remarks>
    /// Random rather than fixed vectors, which costs nothing here: no assertion depends on the value of a
    /// verifier — every expected hash is computed from the bytes the test itself produced — so
    /// repeatability is not at stake, and randomness is what keeps the duplicate case's collision the only
    /// one in its set.
    /// </remarks>
    private static string[] Verifiers(int count = RequiredCodeCount, int length = VerifierLength) =>
    [
        .. Enumerable.Range(0, count).Select(_ => Base64UrlText.Encode(RandomNumberGenerator.GetBytes(length))),
    ];

    /// <summary>
    /// A set that is wrong in exactly one way, and well-formed in every other.
    /// </summary>
    /// <remarks>
    /// One malformed member out of ten wherever the cause allows it, never ten, so the refusal is
    /// attributable: a check written over the first element only, or over the set's total length, is red
    /// on this arrangement and green on a uniformly malformed one.
    /// </remarks>
    private static string[] MalformedSet(string cause)
    {
        string[] verifiers = Verifiers();

        switch (cause)
        {
            case "too few":
                return Verifiers(RequiredCodeCount - 1);

            case "too many":
                return Verifiers(RequiredCodeCount + 1);

            case "not base64url":
                // Standard base64's two extra characters, which base64url replaces with '-' and '_',
                // written into a member that is otherwise a perfectly good encoding of the right width.
                // Substituted rather than re-encoded with Convert.ToBase64String, because a random value
                // encoded that way need contain neither character — its only guaranteed difference is
                // the '=' padding, and PasskeyEncoding accepts a padded value deliberately. The length
                // is untouched, so this is refused for its alphabet and not for its width, which is what
                // makes it a different case from the one below.
                char[] mangled = [.. Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength))];
                mangled[0] = '+';
                mangled[1] = '/';
                verifiers[^1] = new string(mangled);
                return verifiers;

            case "wrong width":
                verifiers[^1] = Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength - 1));
                return verifiers;

            case "duplicated":
                verifiers[^1] = verifiers[0];
                return verifiers;

            default:
                throw new InvalidOperationException($"No malformed set is defined for '{cause}'.");
        }
    }

    /// <summary>The SHA-256 of each verifier, as the rows store it, ordered.</summary>
    private static string[] ExpectedHashesOf(IEnumerable<string> verifiers) =>
    [
        .. verifiers
            .Select(verifier => Convert.ToHexString(SHA256.HashData(Base64UrlText.Decode(verifier))))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Runs the whole issuing ceremony: the re-authentication options leg, <paramref name="device" />
    /// answering the nonce it issued, and the generation post.
    /// </summary>
    /// <param name="verifiers">
    /// The set to present. Null means a well-formed one of the required size, which is what every test
    /// that is not about the validation family wants.
    /// </param>
    private static async Task<HttpResponseMessage> GenerateAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        IReadOnlyList<string>? verifiers = null)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);

        // signCount stays at zero on every ceremony in this file: see the class remarks.
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);

        return await PostGenerationAsync(client, assertion, verifiers ?? Verifiers());
    }

    /// <summary>
    /// Posts one generation with an assertion the caller built, for the tests that need a proof the
    /// ceremony above would never produce — a stale pool, a foreign device, a flipped signature.
    /// </summary>
    /// <remarks>
    /// The five assertion members are the erasure request's and the revocation request's, byte for byte,
    /// so a caller comparing the three gates learns nothing from the difference between them. The set
    /// travels beside them rather than nested, because it is a member of the same command.
    /// </remarks>
    private static Task<HttpResponseMessage> PostGenerationAsync(
        HttpClient client,
        AssertionResult assertion,
        IReadOnlyList<string> verifiers) =>
        client.PostAsJsonAsync(RecoveryCodesPath, new
        {
            verifiers,
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });

    /// <summary>
    /// The same ceremony with one bit of the signature moved, so the response is genuine in every respect
    /// except the one under test.
    /// </summary>
    private static AssertionResult WithFlippedSignature(AssertionResult result)
    {
        byte[] signature = [.. result.Signature];
        signature[^1] ^= 0xFF;

        return result with { Signature = signature };
    }

    /// <summary>
    /// Runs both authenticated legs of a registration, so the account really holds a passkey a signature
    /// answers to rather than material seeded out of band.
    /// </summary>
    /// <remarks>
    /// The account is established first, on the one route group allowed to mint one. Neither passkey leg
    /// provisions and neither does <c>/api/me/*</c>, so without this line the very first request is
    /// refused with a 401 and every test here would be red for a reason it is not about. Written out here
    /// rather than shared, because it is private to <c>CredentialRevocationTests</c> and that file makes
    /// the same choice for the same reason.
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
        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
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
    /// Writes a challenge row of a named ceremony directly and hands back its bytes, so an authenticator
    /// can sign a nonce whose age this test chose.
    /// </summary>
    /// <remarks>
    /// The bytes are returned because signing them is the whole point: everything about the resulting
    /// request is genuine except how old the nonce is. <c>created_at_utc</c> is always ten minutes back,
    /// because <c>CK_webauthn_challenges_lifetime</c> refuses a row whose expiry is at or before its
    /// creation. Written on the container superuser, like every other out-of-band statement here.
    /// </remarks>
    private static async Task<byte[]> InsertChallengeAsync(
        PostgresTestHost host,
        string ceremony,
        int expiresInMinutes)
    {
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        DateTime nowUtc = DateTime.UtcNow;

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            insert into webauthn_challenges (id, challenge, ceremony, created_at_utc, expires_at_utc)
            values (@id, @challenge, @ceremony, @created, @expires)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("challenge", challenge);
        command.Parameters.AddWithValue("ceremony", ceremony);
        command.Parameters.AddWithValue("created", nowUtc.AddMinutes(-10));
        command.Parameters.AddWithValue("expires", nowUtc.AddMinutes(expiresInMinutes));
        await command.ExecuteNonQueryAsync();

        return challenge;
    }

    /// <summary>
    /// Writes the one session row a redemption will write once that endpoint exists: opened by the set's
    /// own credential, and therefore <c>kind = 'full'</c>.
    /// </summary>
    /// <remarks>
    /// Out of band because there is no product path to a recovery-code session today — see the remarks on
    /// <see cref="Generation_WhenTheReplacedSetHasLiveSessions_DoesNotFailOnAMissingSessionDeleteGrant" />,
    /// which is the only caller. The three copied columns must agree with the credential's own, or the
    /// composite foreign key to <c>credentials(id, user_id, type)</c> refuses the row — which is exactly
    /// what makes this arrangement honest rather than a fabricated shape.
    /// </remarks>
    private static async Task InsertRecoveryCodeSessionAsync(
        NpgsqlConnection admin,
        Guid userId,
        Guid credentialId)
    {
        DateTime nowUtc = DateTime.UtcNow;

        await using NpgsqlCommand command = new(
            """
            insert into sessions
                (id, user_id, credential_id, credential_type, kind, created_at_utc, expires_at_utc)
            values (@id, @userId, @credentialId, 'recovery_codes', 'full', @created, @expires)
            """,
            admin);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("credentialId", credentialId);
        command.Parameters.AddWithValue("created", nowUtc);
        command.Parameters.AddWithValue("expires", nowUtc.AddHours(1));

        // One row, or the test that reads it is measuring an arrangement that never happened.
        await Assert.That(await command.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    /// <summary>
    /// The response body as an object, so a test can ask whether a member is <b>there</b> and not only
    /// what it deserializes to.
    /// </summary>
    /// <remarks>
    /// <b>Once per response, and the rule is why the two readers below take a document rather than a
    /// response.</b> The content stream is handed out once and then sits at its end, so a second call
    /// against the same response parses nothing and throws — a failure that names a JSON reader and not
    /// the route, in a test that looked like it was asking two questions about one payload. A test
    /// wanting two facts from one body parses it here and reads both off the result.
    /// </remarks>
    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();

    /// <summary>
    /// The whole body, with the one member that varies per <b>request</b> rather than per <b>cause</b>
    /// replaced by a fixed placeholder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>traceId</c> is a new value on every request, including two requests refused for the identical
    /// reason, so comparing it would compare the trace and not the refusal. The member is replaced rather
    /// than removed, so a <c>traceId</c> that stopped being emitted still fails and any other member
    /// appearing, disappearing or differing fails with it.
    /// </para>
    /// <para>
    /// The placeholder is written into a copy, so the caller's document is still the body that arrived.
    /// A caller holding the only parse of a response — which is every caller reading two facts from one
    /// — would otherwise find the trace doctored by whichever assertion happened to run first.
    /// </para>
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
    /// The <c>title</c> of a problem-details body, which is the only member that says which of this
    /// route's several 401s answered.
    /// </summary>
    private static string TitleOf(JsonObject body) => body["title"]!.GetValue<string>();

    /// <summary>
    /// <see cref="ComparableBodyOf" /> for a caller that needs nothing else out of the response.
    /// </summary>
    private static async Task<string> ReadComparableBodyAsync(HttpResponseMessage response) =>
        ComparableBodyOf(await ReadJsonObjectAsync(response));

    /// <summary>
    /// <see cref="TitleOf" /> for a caller that needs nothing else out of the response.
    /// </summary>
    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        TitleOf(await ReadJsonObjectAsync(response));

    /// <summary>
    /// Every message in a validation problem's <c>errors</c> bag, whatever it is keyed on.
    /// </summary>
    /// <remarks>
    /// Read without naming the key, because the key is the command member a caller can correct and this
    /// test makes no claim about which member that is — only that a sentence arrived.
    /// </remarks>
    private static async Task<string[]> ReadValidationMessagesAsync(HttpResponseMessage response)
    {
        JsonObject body = await ReadJsonObjectAsync(response);

        return body["errors"] is not JsonObject errors
            ? []
            : [.. errors.SelectMany(field => field.Value?.AsArray() ?? []).Select(message => message!.GetValue<string>())];
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

    /// <summary>
    /// The <c>credentials.id</c> of the row standing for an account's set of recovery codes.
    /// </summary>
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

    /// <summary>How many sets the account holds, which the product's own index bounds at one.</summary>
    private static async Task<long> CountSetsAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from credentials where user_id = @userId and type = 'recovery_codes'",
            admin);
        command.Parameters.AddWithValue("userId", userId);

        return await ReadCountAsync(command);
    }

    /// <summary>How many codes hang off one set's credential.</summary>
    private static async Task<long> CountCodesOfSetAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from recovery_code_hashes where credential_id = @credentialId",
            admin);
        command.Parameters.AddWithValue("credentialId", credentialId);

        return await ReadCountAsync(command);
    }

    /// <summary>
    /// The credential's sessions that nothing has revoked, on the superuser connection.
    /// </summary>
    /// <remarks>
    /// <c>sessions</c> carries <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed connection
    /// reports zero rows for a session that is still there exactly as it does for one that is gone.
    /// </remarks>
    private static async Task<long> CountLiveSessionsAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from sessions where credential_id = @credentialId and revoked_at_utc is null",
            admin);
        command.Parameters.AddWithValue("credentialId", credentialId);

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
}
