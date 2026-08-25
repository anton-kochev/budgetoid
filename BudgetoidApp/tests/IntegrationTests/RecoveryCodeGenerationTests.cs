using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Domain.Users;
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
/// <b>The request carries ten whole submissions, not ten bare verifiers.</b> A set is ten separate
/// secrets under a single <c>credentials</c> row, and the client derives a key-encryption key from each
/// <em>code</em> — so each code arrives with its own factor identifier and its own pair of envelopes
/// sealed under that code's key, and a generation files ten <c>wrapped_account_keys</c> rows against one
/// credential. One factor and one pair for the whole set would seal the account under whichever code
/// that pair belonged to, and nine of the ten would open nothing.
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
    /// Which code of a set the malformed cases put their one fault on.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not the first, and that is the whole reason it is named.</b> A handler that
    /// validates <c>codes[0]</c> and trusts the other nine is green on every case that only ever
    /// corrupts the first code — and it would file nine codes' worth of unjudged bytes into the
    /// account's key custody. The last code is also where <see cref="MalformedSet" /> already puts its
    /// own fault, for the same reason.
    /// </remarks>
    private const int FaultedOrdinal = RequiredCodeCount - 1;

    /// <summary>
    /// The one member of the generation response, named once so the test asserting it is present and the
    /// test reading its value cannot drift apart from each other.
    /// </summary>
    private const string SessionsEndedMember = "sessionsEnded";

    /// <summary>
    /// The member carrying the session a replacement re-established, or JSON <c>null</c> when it
    /// established none.
    /// </summary>
    /// <remarks>
    /// <b>One nullable member carrying both facts, rather than a kind and an expiry side by side.</b> Two
    /// nullable members admit "kind present, expiry absent", which is a state no handler means and every
    /// client has to branch on; nested, the question a client asks is the one it has — "was I signed back
    /// in, and until when". It carries no session id, for the reason the redemption's response states: an
    /// id would hand the client a stable handle to a session, and the likeliest way this design is broken
    /// later is somebody deciding that handle is close enough to a token to start accepting it.
    /// </remarks>
    private const string SessionMember = "session";

    /// <summary>The members of <see cref="SessionMember" />, when it is not null.</summary>
    private const string KindMember = "kind";

    private const string ExpiresAtUtcMember = "expiresAtUtc";

    /// <summary>
    /// How much of the account the re-established session reaches, as the wire spells it and as the
    /// <c>sessions.kind</c> column spells it — the same word, which is the whole point of converting at
    /// the endpoint boundary.
    /// </summary>
    /// <remarks>
    /// <c>ConfigureHttpJsonOptions</c> registers <c>JsonStringEnumConverter</c> with no naming policy, so
    /// a <c>SessionKind</c> serialized straight out of the application record would reach the wire as
    /// <c>"Full"</c> while the column, and every other spelling of it in this product, reads <c>"full"</c>.
    /// The assertion policy and the redemption response make the same conversion at the same boundary.
    /// </remarks>
    private const string FullKind = "full";

    /// <summary>The <c>credential_type</c> a recovery-code session's row carries.</summary>
    private const string RecoveryCodesCredentialType = "recovery_codes";

    /// <summary>
    /// The members of the generation response, joined exactly as
    /// <see cref="Generation_ResponseCarriesTheSessionCountAndNothingElse" /> builds them — ordered
    /// ordinal, because that is how that test orders what it read. The constant exists so that a member
    /// arriving is a comparison of two strings rather than of two numbers.
    /// </summary>
    /// <remarks>
    /// <b><see cref="SessionMember" /> is here even on a response that established nothing.</b> Nothing
    /// configures <c>DefaultIgnoreCondition</c>, so a null member is written rather than omitted — and
    /// that is the shape to keep: a member that appears only sometimes makes "the server did not tell me"
    /// and "the server told me no" the same observation for a client.
    /// </remarks>
    private const string GenerationMembers = SessionMember + ", " + SessionsEndedMember;

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
    /// The clause only the factor-identifier conflict carries, and the one thing that tells it apart from
    /// the two lost-race conflicts this route answers with the same status and the same title.
    /// </summary>
    /// <remarks>
    /// Spelled out here rather than read off the repository, and the copy is deliberate: the sentence is
    /// <c>private</c> to <c>RecoveryCodeRepository</c> and to <c>PasskeyRepository</c>, which hold it
    /// twice on purpose so that one route's wording cannot be changed by editing the other's. A test
    /// taking its expectation from the constant under test would agree with whatever that constant later
    /// said, including with the lost-race sentence.
    /// </remarks>
    private const string FactorConflictClause = "factor identifier is already registered";

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
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
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
    /// That the response carries exactly <c>sessionsEnded</c> and <c>session</c>, and no third member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Green the day it is written, and that is the point rather than an apology.</b> Nothing else in
    /// either suite goes red when a further member starts arriving here: every other test on this route
    /// reads the status, the rows behind it, or one named member, and all of them keep passing beside a
    /// <c>credentialId</c> or a <c>generatedAtUtc</c>. The defect this exists to catch is one a later
    /// reader adds the day a client wants to refresh its own view from the response.
    /// </para>
    /// <para>
    /// <b><c>session</c> joining the list is the one widening this test was written to notice, and it was
    /// argued rather than waved through.</b> A replacement that swept the caller's own session has to say
    /// so, or the client cannot tell a person who has just been signed out from one who has not — see
    /// <see cref="Generation_ResponseCarriesTheReestablishedSessionAndOtherwiseNull" />. What did not
    /// change is that no code, no verifier, no hash and no id may appear, which
    /// <see cref="Generation_ReturnsNoRecoveryCodeAndNoVerifierOnTheWire" /> holds.
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
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

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
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
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
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

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
    /// A generation carrying no factor and neither envelope is refused, and the set the person is
    /// holding — its credential, its ten codes <b>and</b> its share of the account keys — is still
    /// exactly where it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test that catches a handler validating after it has deleted.</b> The three
    /// members are validated inside <c>DecodeAndValidate</c>, past the re-authentication gate and
    /// before anything is read or removed; let that validation drift down past the read and the
    /// request answers 400 exactly as this test demands, having already taken the previous set with
    /// it. The status alone cannot see that, which is why the previous set is compared by value here
    /// rather than counted.
    /// </para>
    /// <para>
    /// <b>The wrapped keys are the half a reader will leave out, and they are the expensive half.</b>
    /// Ten deleted hash rows cost the person the codes on a card they still hold; a deleted
    /// <c>wrapped_account_keys</c> row costs them the only copy of the content key that factor could
    /// open, and nothing on this server can mint another. So the row is read back and its factor id
    /// and both envelopes compared against the values the first generation posted.
    /// </para>
    /// <para>
    /// The refusal is a 400 in the shape the <c>Verifiers</c> refusals already use, and it is past the
    /// gate for the reason
    /// <see cref="Generation_WithAMalformedSet_IsRefusedWithASentenceAndWritesNothing" /> gives: the
    /// caller has proved possession of an authenticator registered to this account, so a sentence
    /// naming what is missing enumerates nobody.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RecoveryCodeGeneration_RefusesASetCarryingNoWrappedKeys_AndLeavesThePreviousSetIntact()
    {
        // Arrange — a real first set, whose share of the account keys is a fixture this test keeps, so
        // "still there" can be a comparison of values rather than of a count.
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        WrappedKeyFixture[] issued = MintKeys(RequiredCodeCount);
        string[] verifiers = Verifiers();
        await Assert.That((await GenerateAsync(client, device, userId, verifiers, issued)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid issuedSetId = await ResolveSetCredentialIdAsync(admin, userId);

        // The set really is whole before the act, or every survival below is a claim about rows the
        // arrangement never wrote.
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));

        WrappedAccountKeysRow[] before = await WrappedAccountKeysAsync(admin, userId);
        await Assert.That(before.Any(row => row.FactorId == issued[0].Factor)).IsTrue();

        // Act — a fresh, genuine proof and a well-formed set of ten verifiers, with no factor
        // identifier and neither envelope anywhere in the body.
        HttpResponseMessage response = await GenerateWithoutWrappedKeysAsync(client, device, userId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // The account still holds the one set it held, under the same credential, with the same ten
        // codes.
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, userId)).IsEqualTo(issuedSetId);
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
        await Assert.That(await CountCodesOfSetAsync(admin, issuedSetId)).IsEqualTo((long)RequiredCodeCount);

        // And its share of the account keys is byte for byte the share it was issued with. Named by
        // the factor identifier the first generation posted, which is unique across the whole table,
        // so this is the row that generation wrote and no other. One code's row rather than all ten,
        // which is the claim this test has always made; the whole set's survival is pinned by
        // RecoveryCodeGeneration_RefusesASetRepeatingAFactorIdentifier_AndLeavesThePreviousSetIntact.
        WrappedAccountKeysRow[] surviving = [.. (await WrappedAccountKeysAsync(admin, userId))
            .Where(row => row.FactorId == issued[0].Factor)];
        await Assert.That(surviving.Length).IsEqualTo(1);
        await Assert.That(surviving[0].CredentialId).IsEqualTo(issuedSetId);
        await Assert.That(Base64UrlText.Encode(surviving[0].WrappedContentKey))
            .IsEqualTo(issued[0].WrappedContentKey);
        await Assert.That(Base64UrlText.Encode(surviving[0].WrappedIndexKey))
            .IsEqualTo(issued[0].WrappedIndexKey);
    }

    /// <summary>
    /// A genuine generation files the set's share of the account keys against the <b>set's own</b>
    /// credential, and each value lands in the column it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The credential is the assertion a reader will assume rather than check.</b> Two credentials
    /// are in play on this request: the passkey that proved presence, which is loaded and verified
    /// moments earlier and therefore the one nearest to hand, and the set's own <c>credentials</c> row,
    /// which the handler mints in the same save. Filing the row against the asserting passkey stores
    /// envelopes wrapped under the recovery code's key-encryption key beside a factor that derives a
    /// different one — nothing refuses it, and the discovery is a browser failing to open a passkey's
    /// keys on the day somebody uses that passkey.
    /// </para>
    /// <para>
    /// <b>Which envelope landed in which column is the other half, and no constraint the database holds
    /// can tell them apart.</b> Both are <see cref="WrappedAccountKeys.EnvelopeLength" /> bytes, both
    /// carry <see cref="WrappedAccountKeys.EnvelopeVersion" />, both are <c>NOT NULL</c>: a swapped
    /// pair satisfies every check. Only bytes told apart by which column they landed in can catch it,
    /// which is what <see cref="WrappedKeyFixture" /> mints.
    /// </para>
    /// <para>
    /// The row is found by the factor identifier the request posted rather than by the credential it
    /// should have been filed under — that identifier is minted by the client and unique across the
    /// whole table, so it names the row this request wrote without presupposing the very column the
    /// test is about.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RecoveryCodeGeneration_FilesTheSetsWrappedKeys_WithTheSetsOwnCredential()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        WrappedKeyFixture[] keys = MintKeys(RequiredCodeCount);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId, verifiers: null, keys);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // One code's row, which is this test's claim: the credential a share is filed against. That
        // every code gets a row of its own is the neighbouring test's claim.
        WrappedAccountKeysRow[] ofFactor = [.. (await WrappedAccountKeysAsync(admin, userId))
            .Where(row => row.FactorId == keys[0].Factor)];
        await Assert.That(ofFactor.Length).IsEqualTo(1);

        // Column by column, because a row of the right shape under the wrong credential, or with the
        // two envelopes exchanged, is a row every constraint in the schema accepts.
        WrappedAccountKeysRow row = ofFactor[0];
        await Assert.That(row.CredentialId).IsEqualTo(await ResolveSetCredentialIdAsync(admin, userId));
        await Assert.That(row.UserId).IsEqualTo(userId);
        await Assert.That(row.CredentialType).IsEqualTo(RecoveryCodesCredentialType);

        // Re-encoded and compared against the text the request carried, so the comparison is over the
        // exact bytes in their exact order.
        await Assert.That(Base64UrlText.Encode(row.WrappedContentKey)).IsEqualTo(keys[0].WrappedContentKey);
        await Assert.That(Base64UrlText.Encode(row.WrappedIndexKey)).IsEqualTo(keys[0].WrappedIndexKey);
    }

    /// <summary>
    /// A genuine generation files <b>one wrapped-key row per code</b>, each carrying the factor
    /// identifier and the two envelopes that arrived with that code and no other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test the whole shape of the request exists for.</b> A set of recovery codes is
    /// ten separate secrets under a single <c>credentials</c> row, and the client derives a
    /// key-encryption key from each <em>code</em> — so the account's two keys are wrapped ten times,
    /// under ten different keys. A route carrying one factor identifier and one pair of envelopes for
    /// the whole set seals the account under whichever code that pair belonged to: the person redeems
    /// any one of the ten, is handed a session, and nine times out of ten unlocks nothing.
    /// </para>
    /// <para>
    /// <b>Which value landed in which row, and in which column, is the entire point.</b> Every row a
    /// wrong handler writes satisfies every constraint this table holds — ten rows of the right width,
    /// the right version and the right owner, under the right credential — so nothing beneath the
    /// application can notice a handler that paired one code's verifier with another code's envelopes,
    /// or wrote one code's pair ten times under ten distinct factors. Only bytes told apart by which
    /// row and which column they landed in can catch it, which is what
    /// <see cref="MintKeys" /> mints and what <see cref="FingerprintsOf(IEnumerable{WrappedKeyFixture})" />
    /// compares.
    /// </para>
    /// <para>
    /// Read scoped to the credential type the column holds, because the passkey that proves every
    /// ceremony in this file was registered with a share of its own and that row is nobody's business
    /// here. The credential is then asserted separately, so a row filed against the asserting passkey
    /// is a failure rather than a row this read quietly skipped.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RecoveryCodeGeneration_FilesOneWrappedKeyRowPerCode_EachUnderItsOwnFactor()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        // Ten distinguishable submissions: ten factors, and twenty envelopes no two of which share a
        // byte pattern.
        WrappedKeyFixture[] keys = MintKeys(RequiredCodeCount);
        string[] verifiers = Verifiers();

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId, verifiers, keys);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        WrappedAccountKeysRow[] ofSet = [.. (await WrappedAccountKeysAsync(admin, userId))
            .Where(row => row.CredentialType == RecoveryCodesCredentialType)];

        // Ten rows, one per code. Nine of them are what a handler keyed on the credential could never
        // have written, and the count is what says so.
        await Assert.That(ofSet.Length).IsEqualTo(RequiredCodeCount);

        // All ten under the set's own credential and the account's own owner — never under the passkey
        // that proved the ceremony, whose key-encryption key derives from a PRF output and opens none
        // of these.
        Guid setId = await ResolveSetCredentialIdAsync(admin, userId);
        await Assert.That(ofSet.All(row => row.CredentialId == setId)).IsTrue();
        await Assert.That(ofSet.All(row => row.UserId == userId)).IsTrue();

        // And every row carries its OWN code's factor and its OWN code's two envelopes, content in the
        // content column and index in the index column. Compared as whole triples rather than as three
        // sets, because three matching sets are exactly what a handler that shuffled the pairing
        // between rows leaves behind.
        await Assert.That(FingerprintsOf(ofSet)).IsEquivalentTo(FingerprintsOf(keys));

        // The set's ten codes really are the ten this request presented, so the rows above belong to a
        // set the account can redeem rather than to some other write.
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
    }

    /// <summary>
    /// A second generation leaves the account with exactly one set's worth of wrapped keys — ten rows,
    /// all the second set's — and every replaced row leaves by the <b>database's own cascade</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What makes this load-bearing rather than a restatement of the replacement test.</b> The
    /// application role holds no <c>DELETE</c> on <c>wrapped_account_keys</c>, so the replaced set's
    /// rows can only leave by the cascade from <c>credentials</c>, which runs with the referencing
    /// table owner's privileges rather than this role's. A handler that ever materialised them — a
    /// "load the old envelopes so we can check them" read is the way it happens — would have EF emit
    /// its own <c>DELETE FROM wrapped_account_keys</c> and die with <c>42501</c>, having removed
    /// nothing. <c>GenerateRecoveryCodesHandler</c> already carries that rule for
    /// <c>recovery_code_hashes</c>; this is the same rule binding a second table.
    /// </para>
    /// <para>
    /// <b>It replaces the single-row version of this test rather than sitting beside it.</b> That test
    /// asserted the account was left holding exactly <em>one</em> row of type <c>recovery_codes</c>,
    /// which is now the defect: a correct replacement leaves ten. The claim is unchanged and the count
    /// moved with the contract, so keeping both would have meant keeping one that fails on a correct
    /// handler.
    /// </para>
    /// <para>
    /// <b>The absent grant is pinned elsewhere and this test is what notices the rows failing to
    /// leave.</b>
    /// <c>AppRoleGrantsTests.Database_RefusesADeleteOnAWrappedAccountKey_WhileTheCascadeFromItsCredentialStillTakesIt</c>
    /// is the permanent control for the privilege; nothing there can see a second generation leaving
    /// twenty rows behind, or the first set's envelopes outliving the card they belonged to. Do not
    /// answer a <c>42501</c> here with a grant: the SQLSTATE names a privilege and the cause is the
    /// change tracker.
    /// </para>
    /// <para>
    /// The surviving rows are compared by value, not counted. Ten rows is what a correct replacement
    /// leaves and also what a handler that replaced the credential while leaving the old envelopes
    /// under it leaves, and the person would then be holding a card whose codes derive key-encryption
    /// keys that open nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RecoveryCodeGeneration_ReplacesEveryWrappedKeyRow_ByTheDatabasesOwnCascade()
    {
        // Arrange — a first set, with ten shares of the account keys of its own.
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        WrappedKeyFixture[] first = MintKeys(RequiredCodeCount);
        await Assert.That((await GenerateAsync(client, device, userId, verifiers: null, first)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // The first set's ten rows are really there, or "exactly ten afterwards" is a claim about rows
        // the arrangement never wrote rather than about rows the replacement took.
        await Assert.That(FingerprintsOf((await WrappedAccountKeysAsync(admin, userId))
                .Where(row => row.CredentialType == RecoveryCodesCredentialType)))
            .IsEquivalentTo(FingerprintsOf(first));

        // Act — the same account, a fresh proof, a second set of ten factors' shares of the same two
        // keys.
        WrappedKeyFixture[] second = MintKeys(RequiredCodeCount);
        HttpResponseMessage response = await GenerateAsync(client, device, userId, verifiers: null, second);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        WrappedAccountKeysRow[] rows = await WrappedAccountKeysAsync(admin, userId);

        // One set's worth and no more — read by the credential type the column holds, because the
        // passkey that proves every ceremony in this file holds a share of its own and that one is
        // nobody's business here.
        WrappedAccountKeysRow[] ofSets =
            [.. rows.Where(row => row.CredentialType == RecoveryCodesCredentialType)];
        Guid newSetId = await ResolveSetCredentialIdAsync(admin, userId);
        await Assert.That(ofSets.Length).IsEqualTo(RequiredCodeCount);
        await Assert.That(ofSets.All(row => row.CredentialId == newSetId)).IsTrue();

        // And every one of them carries the SECOND generation's values, factor and both envelopes.
        await Assert.That(FingerprintsOf(ofSets)).IsEquivalentTo(FingerprintsOf(second));

        // The replaced factors are gone from the whole account, not merely outnumbered — every one of
        // the ten, so a cascade that took nine is a failure rather than a rounding.
        await Assert.That(rows.Any(row => first.Any(key => key.Factor == row.FactorId))).IsFalse();
    }

    /// <summary>
    /// A generation claiming a factor identifier the account's passkey already holds is refused as a
    /// conflict, and everything the replacement had already taken apart is put back: the set's
    /// credential, its ten codes, its share of the account keys, and the session it had opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The identifier comes from the passkey, and the alternative is not a matter of taste.</b> A
    /// second generation reusing the <em>replaced set's own</em> identifier collides with nothing: the
    /// delete of that set's credential cascades its <c>wrapped_account_keys</c> row away inside this same
    /// transaction and before the insert, so the request is answered 200 and
    /// <c>AddSetAsync</c>'s factor-id filter is never reached at all. The identifier has to belong to a
    /// factor this request does <em>not</em> replace, and the passkey that proves every ceremony in this
    /// file is the one such factor an account holds today — filed table-wide under the same
    /// <c>PK_wrapped_account_keys</c>, the primary key over <c>factor_id</c>, so the collision is real and
    /// it is across factor kinds, which is the collision that key exists for.
    /// </para>
    /// <para>
    /// <b>The sentence is asserted, because three refusals on this route are 409 and the status cannot
    /// tell them apart.</b> <c>DeleteSetAsync</c>'s lost race, <c>AddSetAsync</c>'s collision on the
    /// account's one-set index, and this one all become a <see cref="ConflictException" /> and reach the
    /// wire as the same status with the same title. Only <c>detail</c> says which filter fired — and the
    /// first two carry the lost-race sentence, which would send this caller looking for a second client
    /// they do not have. A test satisfied by the status alone would stay green over a request refused one
    /// statement earlier, having proved nothing about the clause it was written for.
    /// </para>
    /// <para>
    /// <b>What the refusal had to put back is the half worth more than the status.</b> Issuing
    /// <em>replaces</em>: by the time the insert collides, the handler has already revoked every session
    /// the previous set opened and removed that set's credential, both inside the one transaction
    /// <c>ITransactionalExecutor</c> opened. So a refusal here is only a refusal if all of it unwinds —
    /// and the failure this catches is a sweep or a delete that commits on its own, which leaves a person
    /// answered "mint a fresh identifier" while their codes are gone and they are signed out. The
    /// previous set is therefore compared by value, its wrapped keys byte for byte, and its session read
    /// back still unrevoked.
    /// </para>
    /// <para>
    /// The claimed factor's surviving row is compared against the <b>passkey's</b> envelopes rather than
    /// counted, because the refused request minted envelopes of its own under that same identifier: a row
    /// of the right shape carrying the wrong bytes is what a handler that updated instead of inserting
    /// would leave, and it would have overwritten the only copy of the content key that passkey can open.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RecoveryCodeGeneration_RefusesAFactorIdentifierAlreadyRegistered()
    {
        // Arrange — a passkey registered under an identifier this test keeps, and a real set of codes
        // filed under one of its own, so the account holds two factors and only one of them is the one
        // the act replaces.
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        WrappedKeyFixture claimed = WrappedKeyFixture.Mint();
        await RegisterPasskeyAsync(client, device, claimed);

        WrappedKeyFixture[] issued = MintKeys(RequiredCodeCount);
        string[] verifiers = Verifiers();
        await Assert.That((await GenerateAsync(client, device, userId, verifiers, issued)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid issuedSetId = await ResolveSetCredentialIdAsync(admin, userId);

        // The session the set opened, written out of band for the reason the replacement tests give:
        // there is no product path to a recovery-code session until redemption ships. Without it the
        // sweep has nothing to revoke and this test would say nothing about the half of the transaction
        // that runs before the delete.
        await InsertRecoveryCodeSessionAsync(admin, userId, issuedSetId);

        // The whole arrangement really is in place, or every survival below is a claim about rows nothing
        // wrote — the passkey's share and the set's ten, ten codes, one live session.
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
        await Assert.That((await WrappedAccountKeysAsync(admin, userId)).Length)
            .IsEqualTo(1 + RequiredCodeCount);
        await Assert.That(await CountLiveSessionsAsync(admin, issuedSetId)).IsEqualTo(1L);

        // Every live session of the account, read here so the survival below is about what the refusal
        // left rather than about how many sessions the arrangement happened to need. This account is also
        // signed in over its passkey, and no sweep aimed at a recovery-code credential touches that one.
        SessionRow[] liveBefore = await LiveSessionsAsync(admin, userId);

        // Act — a fresh, genuine proof and a well-formed set of ten new verifiers, one of whose codes
        // claims the factor identifier the passkey already holds. The envelopes under it are this
        // request's own, so a row that changed hands is visible by its bytes. The claim sits on a code
        // other than the first, so a handler that only ever looks at codes[0] cannot pass.
        WrappedKeyFixture[] claiming = MintKeys(RequiredCodeCount);
        claiming[FaultedOrdinal] = WrappedKeyFixture.MintFor(claimed.Factor);
        HttpResponseMessage response = await GenerateAsync(client, device, userId, Verifiers(), claiming);

        // Assert — the status, and the sentence that says which of this route's three conflicts answered.
        JsonObject refusal = await ReadJsonObjectAsync(response);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(DetailOf(refusal)).Contains(FactorConflictClause, StringComparison.Ordinal);

        // The account still holds the one set it held, under the same credential, with the same ten
        // codes — none of which the act's verifiers hash to.
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, userId)).IsEqualTo(issuedSetId);
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
        await Assert.That(await CountCodesOfSetAsync(admin, issuedSetId)).IsEqualTo((long)RequiredCodeCount);

        // The passkey's share and the set's ten, and no eleventh: the refused request filed nothing of
        // its own. The number moved with the contract — a set is ten rows now, not one — which is the
        // only assertion in this test the new request shape changed.
        WrappedAccountKeysRow[] rows = await WrappedAccountKeysAsync(admin, userId);
        await Assert.That(rows.Length).IsEqualTo(1 + RequiredCodeCount);

        // The set's shares are byte for byte the shares it was issued with, under the set's own
        // credential.
        WrappedAccountKeysRow[] ofSet =
            [.. rows.Where(row => row.CredentialType == RecoveryCodesCredentialType)];
        await Assert.That(ofSet.Length).IsEqualTo(RequiredCodeCount);
        await Assert.That(ofSet.All(row => row.CredentialId == issuedSetId)).IsTrue();
        await Assert.That(FingerprintsOf(ofSet)).IsEquivalentTo(FingerprintsOf(issued));

        // And the claimed identifier still names the passkey's share, carrying the passkey's envelopes
        // rather than the ones the refused request wrapped under it.
        WrappedAccountKeysRow[] ofPasskey = [.. rows.Where(row => row.FactorId == claimed.Factor)];
        await Assert.That(ofPasskey.Length).IsEqualTo(1);
        await Assert.That(Base64UrlText.Encode(ofPasskey[0].WrappedContentKey)).IsEqualTo(claimed.WrappedContentKey);
        await Assert.That(Base64UrlText.Encode(ofPasskey[0].WrappedIndexKey)).IsEqualTo(claimed.WrappedIndexKey);

        // The sweep unwound too: every live session the account had is still live and still the same row,
        // and exactly one of them is over the set the person is holding.
        SessionRow[] live = await LiveSessionsAsync(admin, userId);
        await Assert.That(live.Select(session => session.Id).Order())
            .IsEquivalentTo(liveBefore.Select(session => session.Id).Order());

        SessionRow[] sessionsOfSet = [.. live.Where(session => session.CredentialId == issuedSetId)];
        await Assert.That(sessionsOfSet.Length).IsEqualTo(1);
        await Assert.That(sessionsOfSet[0].CredentialType).IsEqualTo(RecoveryCodesCredentialType);
    }

    /// <summary>
    /// A set two of whose codes claim one factor identifier is refused with a <b>400</b>, and everything
    /// the replacement had already taken apart is put back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule has no precedent in this file and it is the one the new request shape brings with
    /// it.</b> Ten codes are ten factors, so ten identifiers, and nothing outside the request can see
    /// them as a set: each one on its own is a perfectly good uuid nobody else holds.
    /// </para>
    /// <para>
    /// <b>400 and never the 409 the database would give, which is the whole assertion.</b> Left to the
    /// schema this is a <c>PK_wrapped_account_keys</c> violation — a conflict raised on the insert,
    /// <em>after</em> the previous set's credential has already been deleted inside the same
    /// transaction, for a caller whose request was merely wrong. It is also the same shape of evidence
    /// the "every verifier must be different" rule already refuses on: a client repeating a factor
    /// identifier inside one set has randomness that is not what it claims, and the other nine
    /// identifiers are no more trustworthy than the repeated one.
    /// </para>
    /// <para>
    /// <b>The status is asserted against the conflict as well as for the refusal.</b> Three refusals on
    /// this route are 409 and a bare "not OK" would report the collision this test exists to forbid as
    /// though it were the refusal it wanted.
    /// </para>
    /// <para>
    /// <b>What the refusal had to put back is the half worth more than the status.</b> Issuing
    /// replaces: by the time an insert could collide, the handler has already revoked every session the
    /// previous set opened and removed that set's credential, both inside the one transaction. So this
    /// is only a refusal if all of it unwinds — the set's credential, its ten hash rows, its ten
    /// wrapped-key rows and the session it had opened, each read back and compared by value.
    /// </para>
    /// <para>
    /// The repeat sits on the last code rather than the first, for the reason
    /// <see cref="FaultedOrdinal" /> gives.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RecoveryCodeGeneration_RefusesASetRepeatingAFactorIdentifier_AndLeavesThePreviousSetIntact()
    {
        // Arrange — a real first set of ten codes, each with a share of the account keys of its own,
        // plus the session it opened, so the refusal has a whole account state to fail to destroy.
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        WrappedKeyFixture[] issued = MintKeys(RequiredCodeCount);
        string[] verifiers = Verifiers();
        await Assert.That((await GenerateAsync(client, device, userId, verifiers, issued)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid issuedSetId = await ResolveSetCredentialIdAsync(admin, userId);

        // Written out of band for the reason the replacement tests give: there is no product path to a
        // recovery-code session until redemption ships, and without one the sweep has nothing to revoke
        // and this test would say nothing about the half of the transaction that runs before the delete.
        await InsertRecoveryCodeSessionAsync(admin, userId, issuedSetId);

        // The whole arrangement really is in place, or every survival below is a claim about rows
        // nothing wrote.
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
        string[] before = FingerprintsOf(await WrappedAccountKeysAsync(admin, userId));
        await Assert.That(await CountLiveSessionsAsync(admin, issuedSetId)).IsEqualTo(1L);

        // Every live session of the account, read here so the survival below is about what the refusal
        // left rather than about how many sessions the arrangement happened to need. This account is also
        // signed in over its passkey, and no sweep aimed at a recovery-code credential touches that one.
        SessionRow[] liveBefore = await LiveSessionsAsync(admin, userId);

        // Act — a fresh, genuine proof and ten well-formed verifiers whose last code repeats the first
        // code's factor identifier. Every other member of every code is faultless, so the repeat is the
        // only thing the server can be refusing.
        CodeSubmission[] codes = SubmissionsOf(Verifiers());
        codes = WithFaultedCode(
            codes,
            FaultedOrdinal,
            code => code with { FactorId = codes[0].FactorId });
        HttpResponseMessage response = await GenerateWithCodesAsync(client, device, userId, codes);

        // Assert — a refusal, and specifically not the conflict the primary key would have raised.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Conflict);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // A sentence, not an empty errors bag: a caller who has already proved presence has to be told
        // something it can act on.
        string[] messages = await ReadValidationMessagesAsync(response);
        await Assert.That(messages.Length).IsGreaterThan(0);
        await Assert.That(messages.All(message => !string.IsNullOrWhiteSpace(message))).IsTrue();

        // The account still holds the one set it held, under the same credential, with the same ten
        // codes — none of which the act's verifiers hash to.
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, userId)).IsEqualTo(issuedSetId);
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
        await Assert.That(await CountCodesOfSetAsync(admin, issuedSetId)).IsEqualTo((long)RequiredCodeCount);

        // Every wrapped-key row of the account is the row it was, byte for byte and column for column:
        // the set's ten and the passkey's one, none added, none taken, none rewritten.
        await Assert.That(FingerprintsOf(await WrappedAccountKeysAsync(admin, userId))).IsEquivalentTo(before);

        // And the sweep unwound too: every live session the account had is still live and still the same
        // row, and exactly one of them is over the set the person is holding.
        SessionRow[] surviving = await LiveSessionsAsync(admin, userId);
        await Assert.That(surviving.Select(session => session.Id).Order())
            .IsEquivalentTo(liveBefore.Select(session => session.Id).Order());

        SessionRow[] ofSet = [.. surviving.Where(session => session.CredentialId == issuedSetId)];
        await Assert.That(ofSet.Length).IsEqualTo(1);
        await Assert.That(ofSet[0].CredentialType).IsEqualTo(RecoveryCodesCredentialType);
    }

    /// <summary>
    /// An envelope of any width but the one the contract defines, and text outside the alphabet it
    /// travels in, are refused — on either member of any code — and the set the person is holding is
    /// untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This route has never had this coverage, and it is the coverage the schema half made ten times
    /// as expensive to miss.</b> The sibling cases on the registration leg are
    /// <c>PasskeyCeremonyTests.PasskeyRegistration_RefusesAWrappedKeyThatIsNotBase64UrlOfExactlySixtyOneBytes</c>,
    /// whose shape this follows; nothing said the generation leg judges an envelope at all.
    /// </para>
    /// <para>
    /// Both sides of the width, because neither may be repaired: a padded or truncated envelope is a
    /// well-formed row holding bytes whose tag cannot verify, and the account looks recoverable until
    /// the day somebody needs the keys. One byte over never reaches the width: it clears the
    /// encoded-length gate — 62 bytes is 83 characters against an allowance of 84 — and is then
    /// refused by <c>PasskeyEncoding.TryDecode</c>, which measures the decoded buffer against the
    /// ceiling the caller named, here <c>PasskeyPayloadLimits.WrappedKeyBytes</c>, the same 61 bytes
    /// the width is. What the width covers instead is the short side, the 29-to-60-byte band that
    /// clears the format's floor and the ceiling alike; both sides are gone before a column sees them.
    /// </para>
    /// <para>
    /// Every case is driven against each of the two members on its own. The columns are written from
    /// two independently supplied values, so a handler that decodes one and passes the other through
    /// files whatever the client felt like sending into half of the account's key custody — and one
    /// member covering the other's gap is exactly what a shared case would hide.
    /// </para>
    /// <para>
    /// The account already holds a set, so "nothing was written" is a claim about a live card surviving
    /// rather than about an empty table: a handler that deleted the previous set before validating the
    /// new one answers 400 exactly as this test demands while having already destroyed the codes and
    /// the envelopes the person is holding.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(WrappedKeyMember.Content, MalformedEnvelope.OneByteShort)]
    [Arguments(WrappedKeyMember.Content, MalformedEnvelope.OneByteTooWide)]
    [Arguments(WrappedKeyMember.Content, MalformedEnvelope.OutsideTheAlphabet)]
    [Arguments(WrappedKeyMember.Index, MalformedEnvelope.OneByteShort)]
    [Arguments(WrappedKeyMember.Index, MalformedEnvelope.OneByteTooWide)]
    [Arguments(WrappedKeyMember.Index, MalformedEnvelope.OutsideTheAlphabet)]
    public async Task RecoveryCodeGeneration_RefusesAWrappedKeyThatIsNotBase64UrlOfExactlySixtyOneBytes(
        WrappedKeyMember member,
        MalformedEnvelope fault)
    {
        // Arrange — a real first set, so the refusal has something to fail to destroy.
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        WrappedKeyFixture[] issued = MintKeys(RequiredCodeCount);
        string[] verifiers = Verifiers();
        await Assert.That((await GenerateAsync(client, device, userId, verifiers, issued)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid issuedSetId = await ResolveSetCredentialIdAsync(admin, userId);
        string[] before = FingerprintsOf(await WrappedAccountKeysAsync(admin, userId));

        // Act — ten well-formed codes with one malformed member on one of them, and everything else
        // about the request genuine.
        string malformed = MalformedEnvelopeText(fault);
        CodeSubmission[] codes = WithFaultedCode(
            SubmissionsOf(Verifiers()),
            FaultedOrdinal,
            code => member is WrappedKeyMember.Content
                ? code with { WrappedContentKey = malformed }
                : code with { WrappedIndexKey = malformed });
        HttpResponseMessage response = await GenerateWithCodesAsync(client, device, userId, codes);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await AssertNamesTheMalformedEnvelopeAsync(response, FaultedOrdinal, member);

        // And the codes and the envelopes the person is holding are still exactly what they were.
        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, userId)).IsEqualTo(issuedSetId);
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
        await Assert.That(FingerprintsOf(await WrappedAccountKeysAsync(admin, userId))).IsEquivalentTo(before);
    }

    /// <summary>
    /// An envelope of the right width whose leading byte names a version this deployment does not
    /// implement is refused, on both members.
    /// </summary>
    /// <remarks>
    /// The two versions are the two ways the byte goes wrong and they arrive from opposite directions:
    /// the one below is what a field nobody set sends — an all-zero buffer of the legal width satisfies
    /// every other rule — and the one above is a client claiming a contract that does not exist, whose
    /// bytes no version of this system could interpret. Both are read off
    /// <see cref="WrappedAccountKeys.EnvelopeVersion" /> rather than written out, so a deployment that
    /// ever defines a successor moves these cases with it instead of leaving two literals behind.
    /// </remarks>
    [Test]
    [Arguments(WrappedKeyMember.Content, (byte)(WrappedAccountKeys.EnvelopeVersion - 1))]
    [Arguments(WrappedKeyMember.Content, (byte)(WrappedAccountKeys.EnvelopeVersion + 1))]
    [Arguments(WrappedKeyMember.Index, (byte)(WrappedAccountKeys.EnvelopeVersion - 1))]
    [Arguments(WrappedKeyMember.Index, (byte)(WrappedAccountKeys.EnvelopeVersion + 1))]
    public async Task RecoveryCodeGeneration_RefusesAWrappedKeyWhoseEnvelopeVersionIsUnknown(
        WrappedKeyMember member,
        byte version)
    {
        // Arrange — a real first set, so the refusal has something to fail to destroy.
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        WrappedKeyFixture[] issued = MintKeys(RequiredCodeCount);
        string[] verifiers = Verifiers();
        await Assert.That((await GenerateAsync(client, device, userId, verifiers, issued)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid issuedSetId = await ResolveSetCredentialIdAsync(admin, userId);
        string[] before = FingerprintsOf(await WrappedAccountKeysAsync(admin, userId));

        // Act — the exact width the contract defines, so the version byte is the only fault, and it
        // sits on a code other than the first.
        string unknownVersion = EnvelopeText(WrappedAccountKeys.EnvelopeLength, version);
        CodeSubmission[] codes = WithFaultedCode(
            SubmissionsOf(Verifiers()),
            FaultedOrdinal,
            code => member is WrappedKeyMember.Content
                ? code with { WrappedContentKey = unknownVersion }
                : code with { WrappedIndexKey = unknownVersion });
        HttpResponseMessage response = await GenerateWithCodesAsync(client, device, userId, codes);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await AssertNamesTheMalformedEnvelopeAsync(response, FaultedOrdinal, member);

        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, userId)).IsEqualTo(issuedSetId);
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
        await Assert.That(FingerprintsOf(await WrappedAccountKeysAsync(admin, userId))).IsEquivalentTo(before);
    }

    /// <summary>
    /// A code's factor identifier is one uuid in one spelling: the hyphenated 36-character form and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first three cases are a single genuine uuid written the other three ways
    /// <see cref="Guid.ToString(string)" /> can produce, and every one of them is accepted by
    /// <see cref="Guid.TryParse(string, out Guid)" /> — which is why they are here rather than assumed
    /// impossible. The value is the associated data both of that code's envelopes were sealed with, and
    /// the browser needs back the bytes it bound, so a server that accepted four spellings would be
    /// storing a value the client cannot recognise as its own.
    /// </para>
    /// <para>
    /// The all-zero uuid is refused on a different argument: it is storable, it is what an unset field
    /// sends, and it is the one value two accounts reach independently — so accepting it turns
    /// <c>PK_wrapped_account_keys</c> into a cross-account collision the second account meets as a
    /// refusal to generate. Within one set it is worse than that: ten codes would send it ten times and
    /// the set would take itself down.
    /// </para>
    /// <para>
    /// The sibling on the registration leg is
    /// <c>PasskeyCeremonyTests.PasskeyRegistration_RefusesAFactorIdentifierThatIsNotOneCanonicalUuid</c>,
    /// whose cases these are.
    /// </para>
    /// <para>
    /// The last four cases are the 36-character hyphenated form itself, written the ways
    /// <see cref="Guid.TryParseExact(string, string, out Guid)" /> also admits under <c>"D"</c>: hex in
    /// upper case, hex in mixed case, and the same uuid with a leading or a trailing space, which that
    /// overload trims before it looks at anything. Each is a different value on the wire and the same
    /// <see cref="Guid" /> in the row, and the row is what every later read hands back — rendered lower
    /// case, unspaced, once. A second client that binds its associated data to the spelling it sent
    /// therefore rebuilds associated data the stored value cannot reproduce, and <b>both</b> of that
    /// code's envelopes stop opening permanently, with nothing anywhere naming the cause. This client
    /// is safe only because its own canonical form lower-cases before sealing; the server owes the same
    /// guarantee to a client whose source it does not hold.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("0198f2c0d1e474a0b9c6e2f8a1b3c5d7")]
    [Arguments("{0198f2c0-d1e4-74a0-b9c6-e2f8a1b3c5d7}")]
    [Arguments("(0198f2c0-d1e4-74a0-b9c6-e2f8a1b3c5d7)")]
    [Arguments("not a factor identifier at all")]
    [Arguments("00000000-0000-0000-0000-000000000000")]
    [Arguments("C1D2E3F4-5A6B-7C8D-9E0F-A1B2C3D4E5F6")]
    [Arguments("C1d2E3f4-5A6b-7C8d-9E0f-A1b2C3d4E5f6")]
    [Arguments(" 0198f2c0-d1e4-74a0-b9c6-e2f8a1b3c5d7")]
    [Arguments("0198f2c0-d1e4-74a0-b9c6-e2f8a1b3c5d7 ")]
    public async Task RecoveryCodeGeneration_RefusesAFactorIdentifierThatIsNotOneCanonicalUuid(string factorId)
    {
        // Arrange — a real first set, so the refusal has something to fail to destroy.
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        WrappedKeyFixture[] issued = MintKeys(RequiredCodeCount);
        string[] verifiers = Verifiers();
        await Assert.That((await GenerateAsync(client, device, userId, verifiers, issued)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid issuedSetId = await ResolveSetCredentialIdAsync(admin, userId);
        string[] before = FingerprintsOf(await WrappedAccountKeysAsync(admin, userId));

        // Act — both envelopes of every code well-formed, so the identifier is the only fault, and it
        // sits on a code other than the first.
        CodeSubmission[] codes = WithFaultedCode(
            SubmissionsOf(Verifiers()),
            FaultedOrdinal,
            code => code with { FactorId = factorId });
        HttpResponseMessage response = await GenerateWithCodesAsync(client, device, userId, codes);

        // Assert — keyed on the code the fault sits on, so a handler that reported it against the whole
        // set, or against the wrong ordinal, is red rather than green with a sentence.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(response, CodeMemberKey(FaultedOrdinal, FactorIdMember)))
            .IsEqualTo(
                "The factor identifier must be a uuid in the lower-case 36-character hyphenated form "
                + "with no surrounding whitespace, and not the all-zero uuid.");

        await Assert.That(await CountSetsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ResolveSetCredentialIdAsync(admin, userId)).IsEqualTo(issuedSetId);
        await Assert.That(await StoredHashesAsync(admin, userId)).IsEquivalentTo(ExpectedHashesOf(verifiers));
        await Assert.That(FingerprintsOf(await WrappedAccountKeysAsync(admin, userId))).IsEquivalentTo(before);
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
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

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
    /// Replacing a set that was carrying a live session leaves the account holding exactly one live
    /// session — over the <b>new</b> set, and stored as a real <c>sessions</c> row the database accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only a real database can show this, and that is why it is here as well as in the unit suite.</b>
    /// The row has to satisfy the composite foreign key to <c>credentials(id, user_id, type)</c> — so
    /// <c>credential_id</c>, <c>user_id</c> and <c>credential_type</c> must agree with the credential
    /// inserted moments earlier in the same transaction — <c>CK_sessions_kind_matches_credential</c>, the
    /// application role's <c>INSERT</c> grant on <c>sessions</c>, and <c>user_isolation</c>, which is
    /// keyed on <c>app.current_user_id</c> and refuses the row with <c>22P02</c> if the identity is not on
    /// the connection. No fake has a grant, a constraint or a policy; every one of those five is
    /// unobservable in memory.
    /// </para>
    /// <para>
    /// <b>The defect this exists to catch is the person, not the schema.</b> Someone who lost their
    /// authenticator, redeemed a code, registered a replacement passkey and is now regenerating is signed
    /// in <em>on a session the replaced set opened</em> — so the sweep takes their own session, and before
    /// this rule they were handed ten fresh codes and thrown out of the flow in the same response.
    /// <c>sessionsEnded</c> and "no session of the replaced credential survived" are both true of that
    /// handler, which is why this test counts the account's live sessions instead.
    /// </para>
    /// <para>
    /// <b>The session under the replaced set is written out of band</b>, for the reason
    /// <see cref="Generation_WhenTheReplacedSetHasLiveSessions_DoesNotFailOnAMissingSessionDeleteGrant" />
    /// gives: a session on a recovery-code credential is written by redeeming a code, and driving a
    /// redemption here would make this test depend on that whole path to arrange one row. Delete the
    /// arrangement the day driving it costs less than explaining it.
    /// </para>
    /// <para>
    /// Counted on the container superuser, like every row in this file: <c>sessions</c> carries
    /// <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed connection reports zero rows for a
    /// session that is there exactly as it does for one that is gone.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_WhenTheReplacedSetHadALiveSession_OpensOneOverTheNewSet()
    {
        // Arrange — a real first set, and one live session hanging off it.
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        await Assert.That((await GenerateAsync(client, device, userId)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid replacedSetId = await ResolveSetCredentialIdAsync(admin, userId);
        await InsertRecoveryCodeSessionAsync(admin, userId, replacedSetId);

        // One live session before the act, or "one afterwards" is a claim about an arrangement that never
        // happened — and a set carrying none is the arrangement the mirror test drives.
        await Assert.That(await CountLiveSessionsAsync(admin, replacedSetId)).IsEqualTo(1L);

        // Every live session of the account, so what the act left is told from what it was handed. This
        // account is also signed in over its passkey, and that session is untouched by a sweep aimed at a
        // recovery-code credential.
        SessionRow[] liveBefore = await LiveSessionsAsync(admin, userId);

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Exactly one live session opened — not zero, which is the defect this test exists for.
        SessionRow[] live = await LiveSessionsOpenedSinceAsync(admin, userId, liveBefore);
        await Assert.That(live.Length).IsEqualTo(1);

        // And the replaced set's session is not live any more. Stated on its own because the count above
        // is now a difference: a handler that established a session without sweeping adds exactly one row
        // as well, and this is the half that tells the two apart.
        await Assert.That(await CountLiveSessionsAsync(admin, replacedSetId)).IsEqualTo(0L);

        // The one it opened is over the set the account is left holding, and it is a full session on a
        // recovery-code credential — the three columns the composite foreign key and the kind check are
        // about.
        Guid newSetId = await ResolveSetCredentialIdAsync(admin, userId);
        await Assert.That(newSetId).IsNotEqualTo(replacedSetId);
        await Assert.That(live[0].CredentialId).IsEqualTo(newSetId);
        await Assert.That(live[0].Kind).IsEqualTo(FullKind);
        await Assert.That(live[0].CredentialType).IsEqualTo(RecoveryCodesCredentialType);
    }

    /// <summary>
    /// A first issue writes no <c>sessions</c> row at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mirror of the test above, and what stops "always establish" being the rule.</b> Writing down
    /// a card for the first time signs nobody in: the codes have never opened a session, nothing was
    /// swept, and a full session minted here would be one nobody asked for on a credential the person has
    /// not used. The response is asserted to say so as well, so a handler reporting nothing while writing
    /// a row cannot pass on the body alone.
    /// </para>
    /// <para>
    /// Counted over the whole account rather than over the set, because the row a mistaken handler writes
    /// might hang off either credential this account holds — and unscoped over <c>user_id</c> so that a
    /// row filed under the wrong owner is caught here rather than reported as absent.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_ForAnAccountWithNoPreviousSet_WritesNoSession()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Every session in the database before the act. The account is signed in to reach the route at
        // all, so the table is not empty; what is claimed below is that this request added nothing to it,
        // and it is still read across the whole table so a row filed under the wrong owner is caught here
        // rather than reported as absent.
        IReadOnlyList<Guid> before = await AllSessionIdsAsync(admin);

        // Act
        HttpResponseMessage response = await GenerateAsync(client, device, userId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body[SessionsEndedMember]!.GetValue<int>()).IsEqualTo(0);

        // Present and null, never absent — a member that appears only sometimes makes "the server did not
        // tell me" and "the server told me no" the same observation. A JSON null reads back as a null
        // node, so the two questions are asked separately.
        await Assert.That(body.ContainsKey(SessionMember)).IsTrue();
        await Assert.That(body[SessionMember] is null).IsTrue();

        await Assert.That((await SessionsOpenedSinceAsync(admin, before)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// The response carries the re-established session as <c>{"kind": "full", "expiresAtUtc": …}</c> when
    /// there was one, and a JSON <c>null</c> when there was not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves in one test, because the claim is a shape and a shape needs both of its states.</b> A
    /// handler emitting the member only when it is populated satisfies a test that reads it on the
    /// populated response, and leaves a client unable to tell "the server did not tell me" from "the server
    /// told me no". So the first issue's <c>null</c> and the replacement's object are read off the same
    /// account, in the order a person meets them.
    /// </para>
    /// <para>
    /// <b><c>"full"</c> and never <c>"Full"</c>, and the difference is a boundary rather than a spelling
    /// preference.</b> <c>ConfigureHttpJsonOptions</c> registers <c>JsonStringEnumConverter</c> with no
    /// naming policy, so the application's <c>SessionKind</c> serialized straight through would reach the
    /// wire as <c>"Full"</c> while the column and every other spelling in this product read <c>"full"</c> —
    /// the conversion belongs at the endpoint, exactly as the assertion and redemption legs do it.
    /// </para>
    /// <para>
    /// The expiry is asserted to parse as an instant in the future rather than compared to a literal: how
    /// long a session lasts is product policy owned by the handler, and
    /// <c>GenerateRecoveryCodesHandlerTests</c> pins the interval against a fixed clock. What this route
    /// owes is that the member is there and is a date, not that this suite agrees with the handler about
    /// fourteen days.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_ResponseCarriesTheReestablishedSessionAndOtherwiseNull()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        // Act, Assert — a first issue signs nobody back in, and says so with a member that is present and
        // null rather than with a member that is absent.
        HttpResponseMessage first = await GenerateAsync(client, device, userId);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject firstBody = await ReadJsonObjectAsync(first);
        await Assert.That(firstBody.ContainsKey(SessionMember)).IsTrue();
        await Assert.That(firstBody[SessionMember] is null).IsTrue();

        // Arrange — the session that set opened, written out of band for the reason the test above gives.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await InsertRecoveryCodeSessionAsync(admin, userId, await ResolveSetCredentialIdAsync(admin, userId));

        // Act — the replacement, which sweeps that session and opens one over the new set.
        HttpResponseMessage second = await GenerateAsync(client, device, userId);

        // Assert
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject secondBody = await ReadJsonObjectAsync(second);
        await Assert.That(secondBody[SessionsEndedMember]!.GetValue<int>()).IsEqualTo(1);

        JsonObject session = secondBody[SessionMember]!.AsObject();
        await Assert.That(session[KindMember]!.GetValue<string>()).IsEqualTo(FullKind);
        await Assert.That(session[ExpiresAtUtcMember]!.GetValue<DateTime>() > DateTime.UtcNow).IsTrue();

        // The member list of the nested object, whole rather than by containment, for the reason the
        // response's own member test gives: a session id arriving here is exactly the widening this
        // product refuses, and a containment check can never fail.
        string members = string.Join(", ", session.Select(member => member.Key).Order(StringComparer.Ordinal));
        await Assert.That(members).IsEqualTo($"{ExpiresAtUtcMember}, {KindMember}");
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

        (HttpClient alice, Guid aliceId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator alicesDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(alice, alicesDevice);

        (HttpClient bob, Guid bobId, _) = await host.Factory.CreateSignedInClientAsync(OtherSubject);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(bob, bobsDevice);

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
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        HttpClient anonymous = host.Factory.CreateClient();
        HttpClient bob = (await host.Factory.CreateSignedInClientAsync(OtherSubject)).Client;
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator unregistered = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        await RegisterPasskeyAsync(bob, bobsDevice);
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
            await client.PostAsJsonAsync(RecoveryCodesPath, new { codes = SubmissionsOf(Verifiers()) })));

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
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        // Act — neither request carries a proof; one carries a set the server would accept and one
        // carries a set it would refuse with a sentence.
        HttpResponseMessage wellFormed = await client.PostAsJsonAsync(
            RecoveryCodesPath,
            new { codes = SubmissionsOf(Verifiers()) });
        HttpResponseMessage malformed = await client.PostAsJsonAsync(
            RecoveryCodesPath,
            new { codes = SubmissionsOf(Verifiers(RequiredCodeCount - 1)) });

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
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

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
    /// A caller carrying no token at all is refused by the fallback policy, and the title says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two refusals answer 401 on this route and the status cannot tell them apart: the fallback
    /// authorization policy turning an anonymous caller away before the route is reached, and the
    /// re-authentication gate refusing a proof. Only the gate's carries
    /// <see cref="PasskeyVerificationExceptionHandler.Title" />; the anonymous one is titled
    /// <c>"Unauthorized"</c> from the status code alone, because <c>UseStatusCodePages</c> writes it with
    /// no title of its own. That inequality is what rules out the door standing open: a route reached
    /// anonymously and refused only by the ceremony would pass a status-only assertion, and that would
    /// mean an unauthenticated caller reaching a handler at all.
    /// </para>
    /// <para>
    /// <b>There was a third refusal and a second inequality beside it, and both went with the
    /// provisioning middleware.</b> An authenticated principal naming no account was answered a
    /// distinctly titled 401, and the sibling test that drove it counted rows rather than reading a
    /// status: a provider token outlives the account it names by up to an hour, so a provisioning marker
    /// arriving on this group would have turned one retried POST into a resurrected, passkey-less
    /// account that the re-authentication gate in front of erasure could never remove again. There is no
    /// marker and no minting path left, and this route authenticates from a cookie only ever issued over
    /// a session row written beside the account it names, so that state cannot be entered and there is
    /// nothing left for the second inequality to rule out.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_ByAnUnauthenticatedCaller_IsRefusedWithATitleTheGateNeverWrites()
    {
        // Arrange
        await using PostgresTestHost host = await StartBearerHostAsync();

        // Act — no cookie and no token, so nothing authenticates and the fallback policy decides.
        HttpResponseMessage response = await host.Factory
            .CreateClient()
            .PostAsJsonAsync(RecoveryCodesPath, new { codes = SubmissionsOf(Verifiers()) });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response))
            .IsNotEqualTo(PasskeyVerificationExceptionHandler.Title);
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
    /// <param name="wrappedKeys">
    /// The shares of the account keys the set is to hold, one per code and paired with the verifier of
    /// the same ordinal. Null mints one fresh share per verifier, which is what every test that is not
    /// about the wrapped keys wants — and they have to be fresh, because <c>PK_wrapped_account_keys</c>
    /// is over <c>factor_id</c> and therefore unique table-wide, and half this file issues twice.
    /// </param>
    private static async Task<HttpResponseMessage> GenerateAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        IReadOnlyList<string>? verifiers = null,
        IReadOnlyList<WrappedKeyFixture>? wrappedKeys = null)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);

        // signCount stays at zero on every ceremony in this file: see the class remarks.
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);

        return await PostGenerationAsync(client, assertion, verifiers ?? Verifiers(), wrappedKeys);
    }

    /// <summary>
    /// Runs the whole issuing ceremony and posts a <c>codes</c> array the caller built itself — the one
    /// shape <see cref="PostGenerationAsync" /> cannot produce, because every submission it builds is
    /// well-formed by construction.
    /// </summary>
    /// <remarks>
    /// Typed as <see cref="object" /> rather than as a list of <see cref="CodeSubmission" />, so a test
    /// can present a code with a member missing, a member of the wrong type, or a member that is not a
    /// value the record could hold at all. The proof is genuine and fresh, so the codes are the only
    /// thing wrong with any request built here.
    /// </remarks>
    private static async Task<HttpResponseMessage> GenerateWithCodesAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        object codes)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);

        return await PostCodesAsync(client, assertion, codes);
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
        IReadOnlyList<string> verifiers,
        IReadOnlyList<WrappedKeyFixture>? wrappedKeys = null) =>
        PostCodesAsync(client, assertion, SubmissionsOf(verifiers, wrappedKeys));

    /// <summary>
    /// Posts a generation body: the <c>codes</c> the caller supplied, and the five assertion members.
    /// </summary>
    /// <remarks>
    /// <b>One member carrying ten whole submissions, and that is the shape rather than an arrangement
    /// convenience.</b> A set of recovery codes is ten separate secrets under a single
    /// <c>credentials</c> row, and the client derives a key-encryption key from each <em>code</em> — so
    /// each code arrives with its own factor identifier and its own pair of envelopes sealed under that
    /// code's key. Ten verifiers beside one factor and one pair would seal the account under whichever
    /// code that pair belonged to, and the other nine would open nothing.
    /// </remarks>
    private static Task<HttpResponseMessage> PostCodesAsync(
        HttpClient client,
        AssertionResult assertion,
        object codes) =>
        client.PostAsJsonAsync(RecoveryCodesPath, new
        {
            codes,
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });

    /// <summary>
    /// The whole issuing ceremony again, with the three key-custody members left off every code
    /// <b>entirely</b>.
    /// </summary>
    /// <remarks>
    /// Absent rather than null or empty, because absent is what a client that has not been updated
    /// sends and it is the request that reaches the handler with nothing to file. The verifiers are
    /// well-formed and there are ten of them, so the only thing wrong with this request is the one
    /// under test.
    /// </remarks>
    private static Task<HttpResponseMessage> GenerateWithoutWrappedKeysAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId) =>
        GenerateWithCodesAsync(
            client,
            device,
            userId,
            Verifiers().Select(verifier => new { verifier }).ToArray());

    /// <summary>
    /// One code's whole submission, exactly as the wire spells it.
    /// </summary>
    /// <remarks>
    /// Every member is <see cref="string" /> because every member is text on the wire, the factor
    /// identifier included — a <see cref="Guid" /> here would let a test present a spelling the
    /// contract refuses only by accident, and <c>RecoveryCodeGeneration_RefusesAFactorIdentifierThat…</c>
    /// exists to present exactly those.
    /// </remarks>
    private sealed record CodeSubmission(
        string Verifier,
        string FactorId,
        string WrappedContentKey,
        string WrappedIndexKey);

    /// <summary>
    /// One share of the account keys per verifier, paired by ordinal.
    /// </summary>
    /// <remarks>
    /// <paramref name="wrappedKeys" /> is null for every test that is not about key custody, and then
    /// a fresh share is minted for each code — fresh because <c>PK_wrapped_account_keys</c> is over
    /// <c>factor_id</c> and unique across the whole table, so a reused identifier anywhere in one
    /// database is a collision rather than a repetition.
    /// </remarks>
    private static CodeSubmission[] SubmissionsOf(
        IReadOnlyList<string> verifiers,
        IReadOnlyList<WrappedKeyFixture>? wrappedKeys = null)
    {
        IReadOnlyList<WrappedKeyFixture> keys = wrappedKeys ?? MintKeys(verifiers.Count);

        return [.. verifiers.Select((verifier, ordinal) => new CodeSubmission(
            verifier,
            keys[ordinal].FactorId,
            keys[ordinal].WrappedContentKey,
            keys[ordinal].WrappedIndexKey))];
    }

    /// <summary>
    /// <paramref name="count" /> distinguishable shares of the account keys, one per code of a set.
    /// </summary>
    /// <remarks>
    /// Distinguishable in all three values: <see cref="WrappedKeyFixture.Mint" /> mints a fresh factor
    /// and fills both envelopes with random bytes, so a handler that filed one code's envelopes against
    /// another code's factor — or filed one code's pair ten times — is visible by the bytes rather than
    /// only by a count that would be ten either way.
    /// </remarks>
    private static WrappedKeyFixture[] MintKeys(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => WrappedKeyFixture.Mint())];

    /// <summary>
    /// The two columns a wrapped account key crosses the wire in, named so a failing case says which
    /// of them stopped being judged.
    /// </summary>
    /// <remarks>
    /// Public because TUnit builds the parameterised cases from these values. Declared here rather than
    /// borrowed from <c>PasskeyCeremonyTests</c>, which spells the same two cases for the registration
    /// leg: the two routes are meant to be able to disagree and be caught disagreeing, and a shared
    /// enumeration is one edit away from moving both.
    /// </remarks>
    public enum WrappedKeyMember
    {
        Content,
        Index,
    }

    /// <summary>
    /// The ways a wrapped key member can be wrong about its width or its alphabet, each one thing at a
    /// time.
    /// </summary>
    public enum MalformedEnvelope
    {
        OneByteShort,
        OneByteTooWide,
        OutsideTheAlphabet,
    }

    /// <summary>
    /// A wrapped key member with exactly one fault in it and everything else about it right.
    /// </summary>
    /// <remarks>
    /// Both width cases carry the version byte, so the width is the only thing wrong with either. The
    /// alphabet case is a well-formed envelope's text with one character replaced by one
    /// base64url does not define — the character a client that reached for the standard encoder emits —
    /// so it is the right length and refused for its alphabet alone. <b>That character sits at index 4
    /// and not at 0</b>, because a substitution inside the leading group changes the decoded version
    /// byte, and the envelope would then be refused for its version with the alphabet tested by
    /// nothing. <c>PasskeyCeremonyTests.MalformedEnvelopeText</c> carries the arithmetic.
    /// </remarks>
    private static string MalformedEnvelopeText(MalformedEnvelope fault) => fault switch
    {
        MalformedEnvelope.OneByteShort =>
            EnvelopeText(WrappedAccountKeys.EnvelopeLength - 1, WrappedAccountKeys.EnvelopeVersion),
        MalformedEnvelope.OneByteTooWide =>
            EnvelopeText(WrappedAccountKeys.EnvelopeLength + 1, WrappedAccountKeys.EnvelopeVersion),
        MalformedEnvelope.OutsideTheAlphabet => EnvelopeText(
                WrappedAccountKeys.EnvelopeLength, WrappedAccountKeys.EnvelopeVersion)
            .Remove(4, 1)
            .Insert(4, OutsideTheBase64UrlAlphabet.ToString()),
        _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, "No text is defined for this fault."),
    };

    /// <summary>A character standard base64 defines and base64url does not.</summary>
    private const char OutsideTheBase64UrlAlphabet = '+';

    /// <summary>
    /// An envelope of exactly <paramref name="length" /> bytes whose leading byte is
    /// <paramref name="version" />, as the wire carries one.
    /// </summary>
    private static string EnvelopeText(int length, byte version)
    {
        byte[] envelope = RandomNumberGenerator.GetBytes(length);
        envelope[0] = version;

        return Base64UrlText.Encode(envelope);
    }

    /// <summary>
    /// The same set of submissions with one member of one code replaced.
    /// </summary>
    /// <remarks>
    /// The ordinal is a parameter and every caller passes <see cref="FaultedOrdinal" />: a handler that
    /// judges <c>codes[0]</c> and trusts the other nine passes every case whose fault sits on the first
    /// code, and it would file nine codes' worth of whatever the client felt like sending into the
    /// account's key custody.
    /// </remarks>
    private static CodeSubmission[] WithFaultedCode(
        CodeSubmission[] codes,
        int ordinal,
        Func<CodeSubmission, CodeSubmission> fault)
    {
        CodeSubmission[] faulted = [.. codes];
        faulted[ordinal] = fault(faulted[ordinal]);

        return faulted;
    }

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
    /// The account already exists when this runs: every caller is handed a client by
    /// <see cref="ApiFactory.CreateSignedInClientAsync" />, which seeds the whole account behind it. The
    /// few tests still reaching the route with a provider bearer establish theirs on the line above the
    /// call, where the fact that they need one is visible.
    /// </remarks>
    /// <param name="wrappedKeys">
    /// The share of the account keys the passkey is to hold. Null mints a fresh one, which is what every
    /// test that never names the passkey's own factor wants — and it has to be fresh, because
    /// <c>factor_id</c> is the table's primary key — <c>PK_wrapped_account_keys</c> — so it is unique
    /// table-wide, and this file registers a passkey on every account it establishes.
    /// </param>
    private static async Task RegisterPasskeyAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        WrappedKeyFixture? wrappedKeys = null)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        WrappedKeyFixture keys = wrappedKeys ?? WrappedKeyFixture.Mint();
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
    /// The <c>detail</c> of a problem-details body, which is the only member that says which of this
    /// route's several 409s answered — the title and the status are one value for all of them.
    /// </summary>
    private static string DetailOf(JsonObject body) => body["detail"]!.GetValue<string>();

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
    /// The one message a validation problem carries under <paramref name="key" />, or a failure naming
    /// every key it did carry.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="ReadValidationMessagesAsync" /> for a test that <b>does</b> make a
    /// claim about which member was refused. That is most of them: this route's refusals are keyed on
    /// the submission and the member, so the key is half of what a client is told and reading it away
    /// discards the half that says which of ten codes to correct.
    /// </remarks>
    private static async Task<string> ReadValidationErrorAsync(HttpResponseMessage response, string key)
    {
        JsonObject body = await ReadJsonObjectAsync(response);

        if (body["errors"] is not JsonObject errors || errors[key] is not JsonArray messages)
        {
            string present = body["errors"] is JsonObject bag
                ? string.Join(", ", bag.Select(field => field.Key))
                : "<no errors bag>";

            throw new InvalidOperationException($"The refusal carries no '{key}'. It carries: {present}.");
        }

        return messages[0]!.GetValue<string>();
    }

    /// <summary>
    /// The refusal is keyed on the code that carries the fault and on the member of it that is wrong,
    /// and its sentence names that member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two members supplied separately, judged separately, refused in sentences that differ by one
    /// word.</b> Swap the two the handler builds those sentences from and every case here stays green
    /// while a client is sent to re-derive the member that was fine — on a route where re-deriving means
    /// minting ten new codes and printing them again. "Some sentence arrived" cannot see that: it fails
    /// only if the route stops returning a validation body at all, which the status assertion beside it
    /// already implies.
    /// </para>
    /// <para>
    /// The ordinal is asserted for the same reason it is not the first code: a handler that judged
    /// <c>codes[0]</c> and filed the refusal against it would name a submission the client has no reason
    /// to touch.
    /// </para>
    /// </remarks>
    private static async Task AssertNamesTheMalformedEnvelopeAsync(
        HttpResponseMessage response,
        int ordinal,
        WrappedKeyMember member)
    {
        string message = await ReadValidationErrorAsync(response, CodeMemberKey(ordinal, KeyNameOf(member)));

        await Assert.That(message).Contains(SentenceNameOf(member));
        await Assert.That(message).DoesNotContain(SentenceNameOf(Other(member)));
        await Assert.That(message).Contains($"{WrappedAccountKeys.EnvelopeLength} bytes");
        await Assert.That(message).Contains($"version {WrappedAccountKeys.EnvelopeVersion}");
    }

    /// <summary>
    /// The key one code's member is refused under: which submission of the set, and which member of it.
    /// </summary>
    /// <remarks>
    /// Restated rather than read off the command, for the reason <see cref="FactorConflictClause" />
    /// gives about its own copy: a test taking its expectation from the type under test agrees with
    /// whatever that type later says, including with a key that stopped naming the ordinal.
    /// </remarks>
    private static string CodeMemberKey(int ordinal, string member) => $"Codes[{ordinal}].{member}";

    /// <summary>The member of the pair that this case left well formed.</summary>
    private static WrappedKeyMember Other(WrappedKeyMember member) =>
        member is WrappedKeyMember.Content ? WrappedKeyMember.Index : WrappedKeyMember.Content;

    /// <summary>The member as the errors bag keys it — the command's own spelling of the submission.</summary>
    private static string KeyNameOf(WrappedKeyMember member) => member switch
    {
        WrappedKeyMember.Content => "WrappedContentKey",
        WrappedKeyMember.Index => "WrappedIndexKey",
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "No key is defined for this member."),
    };

    /// <summary>
    /// The member as the sentence names it: English rather than an identifier, because the sentence is
    /// read by a person and the key is read by a client.
    /// </summary>
    private static string SentenceNameOf(WrappedKeyMember member) => member switch
    {
        WrappedKeyMember.Content => "wrapped content key",
        WrappedKeyMember.Index => "wrapped index key",
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "No wording is defined for this member."),
    };

    /// <summary>The member of a submission that carries its factor identifier, as the bag keys it.</summary>
    private const string FactorIdMember = "FactorId";

    /// <summary>
    /// Reads back the account filed under <paramref name="subject" />. Nothing the API returns names it,
    /// so the lookup goes through that account's federated credential.
    /// </summary>
    /// <remarks>
    /// <b>Nothing calls this any more, and it is left standing on purpose.</b> Every arrangement here now
    /// takes its account id from <see cref="ApiFactory.CreateSignedInClientAsync" />, which hands back the
    /// id of the account it just wrote. It goes with the bearer path in the commit that removes
    /// provisioning; deleting it here would put an unrelated deletion in a test-only change.
    /// </remarks>
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
                $"No account is filed under subject '{subject}', got '{unexpected ?? "null"}'."),
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

    /// <summary>
    /// One <c>wrapped_account_keys</c> row, every column of it.
    /// </summary>
    /// <remarks>
    /// <c>credential_type</c> as the column spells it rather than as the enum member it parses to, so
    /// the assertion is over the token the database actually holds. <c>created_at_utc</c> is absent:
    /// nothing here asks when a factor's share was filed, only which factor holds which bytes.
    /// </remarks>
    private readonly record struct WrappedAccountKeysRow(
        Guid CredentialId,
        Guid FactorId,
        Guid UserId,
        string CredentialType,
        byte[] WrappedContentKey,
        byte[] WrappedIndexKey);

    /// <summary>
    /// Every <c>wrapped_account_keys</c> row of one account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unfiltered by credential type, and every caller names the row it means.</b> The account holds
    /// more than the set's share: the passkey that proves every ceremony in this file was registered
    /// with a share of its own, so a read scoped to <c>recovery_codes</c> would hide a row filed against
    /// the wrong credential — which is exactly the mistake
    /// <see cref="RecoveryCodeGeneration_FilesTheSetsWrappedKeys_WithTheSetsOwnCredential" /> is about.
    /// A test names its row by the factor identifier it posted, which is unique table-wide, or by the
    /// credential type the column holds.
    /// </para>
    /// <para>
    /// On the container superuser, like every row read in this file: this table carries
    /// <c>user_isolation</c>, so a policed connection reports no row for one that is still there exactly
    /// as it does for one that is gone — and most of what is claimed here is that a row survived.
    /// </para>
    /// </remarks>
    private static async Task<WrappedAccountKeysRow[]> WrappedAccountKeysAsync(
        NpgsqlConnection admin,
        Guid userId)
    {
        await using NpgsqlCommand command = new(
            """
            select credential_id, factor_id, user_id, credential_type, wrapped_content_key, wrapped_index_key
            from wrapped_account_keys
            where user_id = @userId
            order by created_at_utc
            """,
            admin);
        command.Parameters.AddWithValue("userId", userId);

        List<WrappedAccountKeysRow> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new WrappedAccountKeysRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetFieldValue<byte[]>(4),
                reader.GetFieldValue<byte[]>(5)));
        }

        return [.. rows];
    }

    /// <summary>
    /// Every wrapped-key row of an account rendered as one comparable line: its factor, its content
    /// envelope and its index envelope, ordered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A projection rather than a count, because a count of these rows can no longer say very
    /// much.</b> A set files ten of them, all under one credential, all the same width, all carrying
    /// the same version byte and all satisfying every constraint the table holds — so "eleven rows
    /// before and eleven after" is equally true of an account nothing touched and of one whose ten
    /// envelopes were rewritten in place by a refused request.
    /// </para>
    /// <para>
    /// The two envelopes are rendered in a fixed order inside the line, so a pair exchanged between the
    /// columns changes the line. Ordered ordinal on the way out, because no read on this path promises
    /// a row order.
    /// </para>
    /// </remarks>
    private static string[] FingerprintsOf(IEnumerable<WrappedAccountKeysRow> rows) =>
    [
        .. rows
            .Select(row => $"{row.FactorId:D} {Base64UrlText.Encode(row.WrappedContentKey)} "
                + $"{Base64UrlText.Encode(row.WrappedIndexKey)}")
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The same, for the shares a set was issued with rather than for the rows it left.</summary>
    private static string[] FingerprintsOf(IEnumerable<WrappedKeyFixture> keys) =>
    [
        .. keys
            .Select(key => $"{key.Factor:D} {key.WrappedContentKey} {key.WrappedIndexKey}")
            .Order(StringComparer.Ordinal),
    ];

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

    /// <summary>
    /// Every session of one <b>account</b> that nothing has revoked, with the three columns the composite
    /// foreign key and <c>CK_sessions_kind_matches_credential</c> are about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped by <c>user_id</c> rather than by credential, because the question is what the account is
    /// left holding: a count over one credential cannot see a session opened over the wrong one, and that
    /// is exactly the row a mistaken handler writes.
    /// </para>
    /// <para>
    /// <b>Live means unrevoked, not unexpired</b> — the same reading the sweep uses. A session past its
    /// expiry and never revoked is an inert row, and pinning it here would be pinning an asymmetry the
    /// rule deliberately declines. See <c>GenerateRecoveryCodesHandlerTests</c>, which argues it.
    /// </para>
    /// <para>
    /// On the container superuser, like every row read in this file: <c>sessions</c> carries
    /// <c>user_isolation</c>, which is <c>FOR ALL</c>.
    /// </para>
    /// </remarks>
    private static async Task<SessionRow[]> LiveSessionsAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            """
            select id, credential_id, kind, credential_type
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
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
        }

        return [.. rows];
    }

    /// <summary>
    /// The account's live sessions that were not live when <paramref name="before" /> was read.
    /// </summary>
    /// <remarks>
    /// <b>A difference, and it is what an absolute count used to be.</b> The arrangements here sign the
    /// account in to reach the route at all, and that sign-in is a live session of its own which no sweep
    /// aimed at a recovery-code credential touches — so "the account holds one live session" stopped
    /// being a statement about the act. What the act owes is unchanged: it opened one, or none. Keyed on
    /// the primary key, because the columns beside it repeat.
    /// </remarks>
    private static async Task<SessionRow[]> LiveSessionsOpenedSinceAsync(
        NpgsqlConnection admin,
        Guid userId,
        IReadOnlyList<SessionRow> before)
    {
        HashSet<Guid> standing = [.. before.Select(session => session.Id)];

        return [.. (await LiveSessionsAsync(admin, userId)).Where(session => !standing.Contains(session.Id))];
    }

    /// <summary>Every <c>sessions</c> row in the database, by id and scoped to nothing.</summary>
    /// <remarks>
    /// Unscoped deliberately: a row filed under the wrong owner is exactly what the caller is looking
    /// for, and a read filtered to the expected account would report it as absent.
    /// </remarks>
    private static async Task<IReadOnlyList<Guid>> AllSessionIdsAsync(NpgsqlConnection admin)
    {
        await using NpgsqlCommand command = new("select id from sessions", admin);

        List<Guid> ids = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>
    /// Every session in the database that was not there when <paramref name="before" /> was read.
    /// </summary>
    /// <remarks>
    /// See <see cref="LiveSessionsOpenedSinceAsync" /> for why these are differences. This one stays
    /// unscoped for <see cref="AllSessionIdsAsync" />'s reason on top of it.
    /// </remarks>
    private static async Task<IReadOnlyList<Guid>> SessionsOpenedSinceAsync(
        NpgsqlConnection admin,
        IReadOnlyList<Guid> before)
    {
        HashSet<Guid> standing = [.. before];

        return [.. (await AllSessionIdsAsync(admin)).Where(id => !standing.Contains(id))];
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

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because almost every
    /// request below authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// The host the few tests that still authenticate with a provider bearer are built on.
    /// </summary>
    /// <remarks>
    /// <b>They are exactly the two about what a caller holding no account of its own is answered</b> —
    /// one authenticated principal the provisioning middleware cannot resolve, and one carrying nothing
    /// at all — which is a question only a bearer host can put. What used to keep the counting tests here
    /// as well was that every one of them read <c>sessions</c> as an absolute against an empty database,
    /// and seeding a sign-in writes a row into it; they read the table before the act now and assert what
    /// the act changed, which keeps each claim unscoped and stops it depending on an empty table.
    /// </remarks>
    private static async Task<PostgresTestHost> StartBearerHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// One <c>sessions</c> row, in the three columns that say what it is: which credential opened it, how
    /// much of the account it reaches, and what type that credential is.
    /// </summary>
    /// <remarks>
    /// The two instants are deliberately absent: nothing here asks when a session was created — the
    /// interval is product policy pinned against a fixed clock in
    /// <c>GenerateRecoveryCodesHandlerTests</c>. The id is carried for one purpose and no assertion reads
    /// its value: it is what a live session standing before an act is told from one the act opened by.
    /// The three columns beside it repeat across rows, so a difference computed without the key would
    /// report one session where there are two.
    /// </remarks>
    private sealed record SessionRow(Guid Id, Guid CredentialId, string Kind, string CredentialType);
}
