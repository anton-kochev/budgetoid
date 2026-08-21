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
/// How many recovery codes an account has left, read over real HTTP — the one number the settings screen
/// shows about a set it can never show the contents of.
/// </summary>
/// <remarks>
/// <para>
/// <b>This route is deliberately not gated by re-authentication, and
/// <see cref="RemainingCount_MintsNoChallengeAndAsksForNoProof" /> is the only thing that says so.</b> A
/// count is not destructive — it names no code, unlocks nothing, and tells the caller a fact about their
/// own account that the screen has to have before it can render at all. Gating it would mint a
/// re-authentication nonce on every page view, which is a WebAuthn prompt on a page nobody asked to
/// prove anything on, and a pool of live nonces issued for no ceremony anybody intends to complete.
/// </para>
/// <para>
/// <b>An account with no set answers <c>{"remaining": 0}</c> and never a 404</b>, and
/// <see cref="RemainingCount_ForAnAccountWithNoSet_IsZeroAndNeverANotFound" /> pins it because it is
/// exactly the kind of thing a later reader "corrects". "You have no codes" and "you have zero left" are
/// the same actionable fact — generate a set — and a 404 makes the client branch on a distinction it
/// cannot use, in a screen whose whole job is to say what to do next.
/// </para>
/// <para>
/// <b>Nothing that could be redeemed may leave this endpoint.</b> Not a verifier, not a stored hash, not
/// the set's credential id: an id in a response body is an id in a client log, and a hash is the value a
/// redemption is matched against. <see cref="RemainingCount_CarriesTheCountAndNothingElse" /> asserts the
/// members and <see cref="RemainingCount_NeverCarriesAVerifierAHashOrAnId" /> asserts against the payload
/// as it went over the wire, which is where a value folded into a member some future widening added
/// would show up.
/// </para>
/// <para>
/// <b><see cref="RemainingCount_ForASecondAccount_CountsThatAccountsCodesAndNotTheFirsts" /> is the most
/// important test in this file.</b> <c>recovery_code_hashes</c> is exempt from row-level security — a
/// redemption arrives anonymous and adopts the <c>user_id</c> it finds on the row, so a policy keyed on
/// the identity it has not established yet could not run — which means the owner predicate on this read
/// is scoped by the application and by nothing beneath it. No policy narrows it, no query filter narrows
/// it, and the grant is on the whole table.
/// </para>
/// <para>
/// No test here names a production type belonging to this story. They address the route over HTTP and
/// read the wire body, so while the endpoint is unmapped they fail on the status assertion against a real
/// 404 rather than failing to compile.
/// </para>
/// </remarks>
public sealed class RecoveryCodeCountEndpointTests
{
    private const string RecoveryCodesPath = "/api/me/recovery-codes";

    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";

    /// <summary>The account under test in most of the file.</summary>
    private const string Subject = "google-counting";

    /// <summary>
    /// The bystander account: the one whose codes must not be counted into the first account's answer,
    /// and whose mere existence is what makes the owner predicate on this read measurable at all.
    /// </summary>
    private const string OtherSubject = "google-counting-bystander";

    /// <summary>How many codes an issued set holds.</summary>
    private const int RequiredCodeCount = 10;

    /// <summary>The exact width of a verifier, decoded.</summary>
    private const int VerifierLength = 32;

    /// <summary>
    /// The one member of the count response, named once so the test asserting it is present and the test
    /// reading its value cannot drift apart from each other.
    /// </summary>
    private const string RemainingMember = "remaining";

    /// <summary>
    /// The members of the count response, joined exactly as
    /// <see cref="RemainingCount_CarriesTheCountAndNothingElse" /> builds them. It is
    /// <see cref="RemainingMember" /> today because the response has one member; the constant exists so
    /// that a second member arriving is a comparison of two strings rather than of two numbers.
    /// </summary>
    private const string CountMembers = RemainingMember;

    /// <summary>
    /// An account that has never generated a set is told it has zero codes left, with a 200.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pinned explicitly because a 404 is the plausible wrong answer</b> — there really is no
    /// <c>credentials</c> row to read, and a handler written as "find the set, then count its codes"
    /// reaches for one naturally. The client has no use for the distinction: both answers mean "generate
    /// a set", the control that offers it is the same control, and a 404 forces a settings screen to
    /// carry a branch whose two arms render the same thing.
    /// </para>
    /// <para>
    /// <b>Both halves are stated</b>, because "not 404" is not the same claim as "200 with a zero": a
    /// route answering 204, or 200 with an empty body, satisfies the first and leaves the screen with
    /// nothing to render. The status assertion comes first so a parse failure on an empty body reads as
    /// the status it is.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RemainingCount_ForAnAccountWithNoSet_IsZeroAndNeverANotFound()
    {
        // Arrange — a signed-in account that has generated nothing.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync(Subject);

        // Act
        HttpResponseMessage response = await client.GetAsync(RecoveryCodesPath);

        // Assert — both halves, so the answer this test refuses is named rather than merely excluded.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body.ContainsKey(RemainingMember)).IsTrue();
        await Assert.That(body[RemainingMember]!.GetValue<int>()).IsEqualTo(0);
    }

    /// <summary>
    /// After a generation the count is the whole set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control for every refusal and for the zero above: a route that answered zero to everybody
    /// satisfies the no-set test perfectly, and a route that was never mapped satisfies both refusals.
    /// This is the test that says the number moves.
    /// </para>
    /// <para>
    /// <b>The member's presence is asserted separately from its value.</b> An absent JSON member
    /// deserializes to <c>0</c> on an <see langword="int" />, which is the value the empty-account case
    /// produces — so asserting only the number would call a response that never carried the member
    /// correct in one of these two tests and wrong in the other, for reasons that have nothing to do with
    /// counting.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RemainingCount_AfterAGeneration_ReportsTheWholeSet()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        await GenerateSetAsync(client, device, userId);

        // Act
        HttpResponseMessage response = await client.GetAsync(RecoveryCodesPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body.ContainsKey(RemainingMember)).IsTrue();
        await Assert.That(body[RemainingMember]!.GetValue<int>()).IsEqualTo(RequiredCodeCount);
    }

    /// <summary>
    /// After a regeneration the count is the new set alone, and never both sets added together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An account holds at most one set — <c>IX_credentials_user_id_recovery_codes</c> owns that rule,
    /// because two sets would be two remaining-counts with nothing saying which one binds — so twenty is
    /// not a number this endpoint can honestly report. What this catches is the two ways it could:
    /// a count that sums across sets while the replacement silently left the old codes behind, and a
    /// count that reads the account's rows without noticing which set they belong to.
    /// </para>
    /// <para>
    /// The number is the same before and after a correct regeneration, which is exactly why the test
    /// exists: nothing about the value moves, so no other test in the file can see the defect.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RemainingCount_AfterARegeneration_ReportsTheNewSetAlone()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        await GenerateSetAsync(client, device, userId);
        await GenerateSetAsync(client, device, userId);

        // Act
        HttpResponseMessage response = await client.GetAsync(RecoveryCodesPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadJsonObjectAsync(response))[RemainingMember]!.GetValue<int>())
            .IsEqualTo(RequiredCodeCount);

        // And the database really does hold one set of ten, or the number above is right about a state
        // the regeneration failed to produce.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountCodesAsync(admin, userId)).IsEqualTo((long)RequiredCodeCount);
    }

    /// <summary>
    /// Two accounts holding a set each, and neither is told about the other's codes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only thing in the codebase that would notice the owner predicate leaving this read.</b>
    /// <c>recovery_code_hashes</c> is exempt from row-level security, so with the predicate gone the
    /// query counts every unredeemed code in the table and each caller is told how many codes the whole
    /// installation holds. Nothing beneath the application would refuse that query and nothing above it
    /// would notice the answer.
    /// </para>
    /// <para>
    /// <b>Both accounts hold a set, and both are asked.</b> With one account seeded, "this account's
    /// codes" and "every code in the table" are the same ten rows and dropping the owner changes no
    /// answer anywhere. With two, an unfiltered count answers twenty to both — so either direction
    /// reddens, and a read that had been made to take the first set it found is caught by whichever
    /// account was not seeded first.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RemainingCount_ForASecondAccount_CountsThatAccountsCodesAndNotTheFirsts()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();

        (HttpClient first, Guid firstId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator firstDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(first, firstDevice);
        await GenerateSetAsync(first, firstDevice, firstId);

        (HttpClient second, Guid secondId, _) = await host.Factory.CreateSignedInClientAsync(OtherSubject);
        SyntheticAuthenticator secondDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(second, secondDevice);
        await GenerateSetAsync(second, secondDevice, secondId);

        // Twenty rows in the table, ten of them each — or "not the other account's" is a claim about rows
        // nothing wrote.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await ScalarAsync(admin, "select count(*) from recovery_code_hashes"))
            .IsEqualTo((long)(RequiredCodeCount * 2));

        // Act — the second account first, since it is the caller a read that took the first set it found
        // would answer correctly by accident.
        HttpResponseMessage secondResponse = await second.GetAsync(RecoveryCodesPath);
        HttpResponseMessage firstResponse = await first.GetAsync(RecoveryCodesPath);

        // Assert
        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadJsonObjectAsync(secondResponse))[RemainingMember]!.GetValue<int>())
            .IsEqualTo(RequiredCodeCount);

        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadJsonObjectAsync(firstResponse))[RemainingMember]!.GetValue<int>())
            .IsEqualTo(RequiredCodeCount);
    }

    /// <summary>
    /// The read asks for no proof and mints no challenge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The absence is the requirement, and it needs a control or it cannot fail.</b> A challenge
    /// count that stayed at zero because nothing in this test ever mints one would satisfy the claim
    /// perfectly, so the act is followed by a real options call: the table gains a row, which is what
    /// says the counter works and that the zero before it meant something.
    /// </para>
    /// <para>
    /// The account holds a real set, so the GET has work to do — a route that answered without reading
    /// anything would satisfy the challenge count while telling the screen nothing. The generation
    /// before it is what puts the nonces it spends into the count, which is why the baseline is taken
    /// after the arrangement and not at the start.
    /// </para>
    /// <para>
    /// <b>Why it matters that this route never grows a gate:</b> the settings screen reads the count on
    /// load, so gating it would mint a nonce on every page view — a live re-authentication nonce for a
    /// ceremony nobody intends to complete, in front of a WebAuthn prompt the person did not ask for.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RemainingCount_MintsNoChallengeAndAsksForNoProof()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        await GenerateSetAsync(client, device, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        long challengesBefore = await ScalarAsync(admin, "select count(*) from webauthn_challenges");

        // Act — a plain GET carrying nothing but the session cookie.
        HttpResponseMessage response = await client.GetAsync(RecoveryCodesPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await ScalarAsync(admin, "select count(*) from webauthn_challenges"))
            .IsEqualTo(challengesBefore);

        // The control: a route that does mint one moves this number, so the equality above is a
        // measurement rather than a table nobody writes to.
        HttpResponseMessage options = await client.PostAsync(ReauthenticationOptionsPath, content: null);
        options.EnsureSuccessStatusCode();
        await Assert.That(await ScalarAsync(admin, "select count(*) from webauthn_challenges"))
            .IsEqualTo(challengesBefore + 1);
    }

    /// <summary>
    /// That the response carries exactly <c>remaining</c>, and no second member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Green the day it is written, and that is the point rather than an apology.</b> Nothing else in
    /// this file goes red when a second member starts arriving: every other test reads <c>remaining</c>
    /// alone and keeps passing beside a <c>credentialId</c>, a <c>generatedAtUtc</c> or a <c>total</c>.
    /// The defect it exists to catch is one a later reader adds the day a screen wants to say "3 of 10".
    /// </para>
    /// <para>
    /// <b>Never <c>ContainsKey</c>.</b> A containment check over member names can never fail: every
    /// widening leaves <c>remaining</c> present and the check green. The members are joined and compared
    /// whole, joined rather than counted so a failure names the member that arrived.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RemainingCount_CarriesTheCountAndNothingElse()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        await GenerateSetAsync(client, device, userId);

        // Act
        HttpResponseMessage response = await client.GetAsync(RecoveryCodesPath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as a member list nobody would recognise as a 401.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Ordered before joining, so a second member produces the same message whichever order the
        // serializer emitted it in.
        JsonObject body = await ReadJsonObjectAsync(response);
        string members = string.Join(", ", body.Select(member => member.Key).Order(StringComparer.Ordinal));

        await Assert.That(members).IsEqualTo(CountMembers);
    }

    /// <summary>
    /// No verifier, no stored hash and no identifier appears anywhere in the response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The member pin above would catch any of them arriving as its own member; this catches them
    /// arriving <em>anywhere at all</em> — folded into a member some future widening added, or appended
    /// to one that already exists. A stored hash is the value a redemption is matched against, so
    /// publishing one turns a read anybody's settings screen makes into the whole secret; the set's
    /// credential id is the thing a revocation route would address it by, and an id in a response body is
    /// an id in a client log.
    /// </para>
    /// <para>
    /// <b>Asserted against the raw response text, as it went over the wire</b>, for the reason
    /// <c>CredentialListEndpointTests.Credentials_NeverCarryTheFederatedSubject</c> gives: re-rendering a
    /// parsed document puts the check at the mercy of whichever characters the serializer's encoder
    /// escapes, and a value hidden behind an escape sequence is a leak a search over the re-rendered text
    /// reports as absent.
    /// </para>
    /// <para>
    /// Each needle is searched for in both encodings this exchange uses — base64url, which is how a
    /// verifier crosses JSON, and hex, which is how a reader of the table would quote one — so a response
    /// that published the hashes in the other spelling cannot pass.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RemainingCount_NeverCarriesAVerifierAHashOrAnId()
    {
        // Arrange — the verifiers are kept, so the search is for values that are genuinely at risk of
        // being echoed rather than for needles nothing ever held.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        string[] verifiers = await GenerateSetAsync(client, device, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setId = await ResolveSetCredentialIdAsync(admin, userId);

        // Act
        HttpResponseMessage response = await client.GetAsync(RecoveryCodesPath);

        // Assert — the status first, so an empty 404 body cannot pass as a payload containing nothing.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        foreach (string verifier in verifiers)
        {
            byte[] decoded = Base64UrlText.Decode(verifier);
            byte[] hash = SHA256.HashData(decoded);

            await Assert.That(payload).DoesNotContain(verifier);
            await Assert.That(payload).DoesNotContain(Convert.ToHexString(decoded));
            await Assert.That(payload).DoesNotContain(Base64UrlText.Encode(hash));
            await Assert.That(payload).DoesNotContain(Convert.ToHexString(hash));
        }

        // And no identifier of the set or of the account behind it.
        await Assert.That(payload).DoesNotContain(setId.ToString());
        await Assert.That(payload).DoesNotContain(userId.ToString());
    }

    /// <summary>
    /// A caller carrying nothing is refused, which is now the whole of what this says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test used to discriminate and now barely does, and that is stated rather than
    /// hidden.</b> It carried a second assertion — that the title was not the provisioning middleware's
    /// <c>NoAccountTitle</c> — and that inequality was the interesting half: without it, a route that
    /// had lost its authorization entirely still passed, because an anonymous request would walk on to
    /// the middleware, find no account for a principal it could not even name, and be answered the
    /// middleware's own 401. There is no middleware and no second 401, so the inequality had nothing
    /// left to rule out. What remains catches exactly one thing: the fallback policy being deleted, or
    /// this group being marked <c>AllowAnonymous</c>, either of which answers this request <c>200</c>.
    /// The second of those is also caught by <c>AnonymousSurfaceTests</c>, which reads the marker off
    /// the route table; the first is caught by nothing else, because that test issues no request.
    /// </para>
    /// <para>
    /// <b>A sibling test went entirely, and what it held is worth recording.</b> An authenticated
    /// subject with no account behind it used to be refused here, and the row counts were the half that
    /// carried the weight: a provider token outlives the account it names by up to an hour, so a
    /// provisioning marker arriving on this group would have turned one settings-screen load into a
    /// resurrected, passkey-less account that the re-authentication gate in front of erasure could never
    /// remove again — and a read was the easiest place for such a marker to land by mistake, because a
    /// GET looks harmless. There is no marker and no minting path left, and this route authenticates
    /// from a cookie only ever issued over a session row written beside the account it names, so the
    /// state that test arranged cannot be entered.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RemainingCount_WithoutAuthentication_IsRefusedWithUnauthorized()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act — no cookie and no token, so nothing authenticates and the fallback policy decides.
        // GetAsync rather than GetStreamAsync: the latter throws on any non-2xx, so a route that
        // answered 200 to an anonymous caller would fail as a transport error rather than as the status
        // assertion it is.
        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync(RecoveryCodesPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Issues one set through the real route and hands back the verifiers it presented.
    /// </summary>
    /// <remarks>
    /// Through the product's own path rather than by seeding rows, so what the count reads is a set this
    /// application wrote. The verifiers are returned because
    /// <see cref="RemainingCount_NeverCarriesAVerifierAHashOrAnId" /> needs the values that are genuinely
    /// at risk of being echoed back.
    /// <para>
    /// <c>signCount</c> stays at zero, which is what an authenticator backing a synced passkey reports
    /// every time: <c>PasskeySignatureCounter.Accept</c> reads a repeated zero as no movement rather than
    /// as a clone, so one device can prove presence as many times as a test needs.
    /// </para>
    /// </remarks>
    private static async Task<string[]> GenerateSetAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId)
    {
        string[] verifiers =
        [
            .. Enumerable
                .Range(0, RequiredCodeCount)
                .Select(_ => Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength))),
        ];

        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
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

        // Fails loudly, or every count below is a number read from an arrangement that never happened.
        response.EnsureSuccessStatusCode();

        return verifiers;
    }

    /// <summary>
    /// One whole submission per verifier, as the route spells a set: the verifier, a factor of its own,
    /// and the pair of envelopes sealed under that code's key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ten submissions and never ten verifiers beside one factor and one pair.</b> A set is ten
    /// separate secrets under a single <c>credentials</c> row and the client derives a key-encryption
    /// key from each <em>code</em>, so one pair for the whole set would seal the account under whichever
    /// code that pair belonged to and nine of the ten would open nothing.
    /// </para>
    /// <para>
    /// A fresh share per code, minted per call: two codes of one set repeating a factor identifier is a
    /// refusal of its own, and <c>factor_id</c> is the table's primary key — <c>PK_wrapped_account_keys</c>
    /// — unique across the whole table rather than per account, so a shared identifier would refuse the
    /// second issue anywhere in one
    /// database. Nothing in this file is about the envelopes; what they have to be is well-formed, or
    /// the arrangement is refused and every count below reads an issue that never happened.
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
    /// The account already exists when this runs — <see cref="ApiFactory.CreateSignedInClientAsync" />
    /// seeds it whole, together with the session the client presents — so this drives the two
    /// registration legs and nothing else.
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
    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();

    /// <summary>
    /// The <c>title</c> of a problem-details body, which is the only member that says which of this
    /// route's refusals answered.
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

    /// <summary>How many unredeemed codes the account holds, counted without the read under test.</summary>
    private static async Task<long> CountCodesAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from recovery_code_hashes where user_id = @userId",
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
    /// A host whose factory leaves the application's own authentication standing, because the requests
    /// it serves authenticate from a session cookie rather than from a provider bearer.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="StartHostAsync" /> rather than replacing it: the two refusal tests below
    /// are about what answers a caller the cookie handler never sees, and moving them onto this host
    /// would change which scheme produced the 401 they read the title of.
    /// </remarks>
    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
