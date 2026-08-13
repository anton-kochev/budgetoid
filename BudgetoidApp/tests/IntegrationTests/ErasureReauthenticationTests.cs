using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Application.Passkeys.Verification;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The re-authentication gate in front of erasure, driven over real HTTP with a real authenticator.
/// </summary>
/// <remarks>
/// <para>
/// Erasure is the one action that destroys an account, and until this gate existed it was authorized
/// by nothing more than the provider bearer token every other request already carries. What these
/// tests defend is the gate itself: that only a <c>reauthentication</c> nonce authorizes it, that the
/// credential answering that nonce has to belong to the account the <b>request</b> authenticates as,
/// and that a bearer token on its own buys nothing at all.
/// </para>
/// <para>
/// Every count is read on <see cref="PostgresTestHost.ConnectionString" /> — the container superuser
/// — and never on the application role. Both isolation policies are <c>FOR ALL</c>, so a policed
/// connection reports zero rows for a row that is still there exactly as it does for one that is
/// gone: read on the app role, "the account survived" could not fail. Every count asserted zero after
/// the act is asserted non-zero before it, on the same connection and the same predicate.
/// </para>
/// </remarks>
public sealed class ErasureReauthenticationTests
{
    private const string Subject = "google-erasing";
    private const string OtherSubject = "google-bystander";

    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";
    private const string ErasurePath = "/api/me/erasure";
    private const string LegacyErasurePath = "/api/me";

    /// <summary>
    /// A domain the attacker owns outright, whose name begins with the one allowed origin. Refused by
    /// an equality comparison and accepted by a prefix one.
    /// </summary>
    private const string LookalikeOrigin = ApiFactory.PasskeyOrigin + ".attacker.example";

    /// <summary>
    /// How many refusals <see cref="EveryReachableErasureRefusal_ProducesTheIdenticalResponse" />
    /// drives. Named so that deleting one from the list is a failing test rather than a shorter and
    /// still perfectly green one.
    /// </summary>
    private const int ReachableErasureRefusals = 11;

    /// <summary>
    /// The counter a second device reports at <b>registration</b>, chosen higher than the one an
    /// ordinary assertion reports — <see cref="SyntheticAuthenticator.Authenticate" /> signs at 1
    /// unless told otherwise, and holds no state between calls.
    /// </summary>
    /// <remarks>
    /// <c>CompleteRegistrationHandler</c> opens the counter at whatever the registration response
    /// reported, so a device registered here at five and then asserting at one is a regression on its
    /// very first assertion. That is what lets the counter entry in
    /// <see cref="EveryReachableErasureRefusal_ProducesTheIdenticalResponse" /> reach step seven of
    /// the gate without an accepted assertion having to happen first — and an accepted assertion on
    /// this endpoint destroys the account, which would take every later entry down with it.
    /// </remarks>
    private const uint RegisteredCounterAboveAnyAssertion = 5;

    /// <summary>
    /// The single most valuable test in this area: an ordinary sign-in nonce, correctly signed by the
    /// account's own registered authenticator, must not destroy the account.
    /// </summary>
    /// <remarks>
    /// The assertion pool is minted from an <b>anonymous</b> endpoint, so anyone able to walk a person
    /// through one WebAuthn prompt for this relying party can obtain a valid response over a nonce
    /// they chose the moment for. A gate that checked <c>ConsumeAsync</c> for a non-null answer rather
    /// than for <c>Reauthentication</c> passes every other test in this file and fails this one.
    /// </remarks>
    [Test]
    public async Task Erasure_OnAnAssertionChallenge_IsRefusedAndErasesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await SeedWrappedAccountKeysAsync(host, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await AssertEverythingIsSeededAsync(before);

        // Act — live, unspent and correctly signed in every respect except the pool it was drawn from.
        byte[] challenge = await BeginCeremonyAsync(host.Factory.CreateClient(), AssertionOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));
        HttpResponseMessage response = await PostErasureAsync(client, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await AssertNothingMovedAsync(admin, userId, before);
    }

    /// <summary>
    /// The other stale pool. A registration nonce is issued to a signed-in person, which is precisely
    /// the stolen-session adversary this gate exists to stop.
    /// </summary>
    [Test]
    public async Task Erasure_OnARegistrationChallenge_IsRefusedAndErasesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await SeedWrappedAccountKeysAsync(host, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await AssertEverythingIsSeededAsync(before);

        // Act
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));
        HttpResponseMessage response = await PostErasureAsync(client, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await AssertNothingMovedAsync(admin, userId, before);
    }

    /// <summary>
    /// The provable-fail control for both refusals above.
    /// </summary>
    /// <remarks>
    /// Without it, a gate that refused <b>every</b> erasure — or one that never reached the account at
    /// all — passes the two cross-ceremony tests perfectly. Only having all three separates a gate
    /// that distinguishes the pools from one that distinguishes nothing.
    /// </remarks>
    [Test]
    public async Task Erasure_AfterAFreshReauthentication_ReturnsNoContent()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await SeedWrappedAccountKeysAsync(host, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await AssertEverythingIsSeededAsync(before);

        // Act
        HttpResponseMessage response = await EraseAsync(client, device, userId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        IReadOnlyDictionary<string, long> after = await CountOwnedRowsAsync(admin, userId);
        foreach (string table in UserOwnedTables.Select(owned => owned.Name))
        {
            await Assert.That(after[table]).IsEqualTo(0L);
        }
    }

    /// <summary>
    /// If the options leg were anonymous, anyone could mint the nonce and the two cross-ceremony
    /// refusals above would buy nothing at all.
    /// </summary>
    [Test]
    public async Task ReauthenticationOptions_Return401WithoutAuthentication()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act — no subject header, so nothing authenticates and the fallback policy decides.
        HttpResponseMessage response = await host.Factory
            .CreateClient()
            .PostAsync(ReauthenticationOptionsPath, content: null);

        // Assert — refused by authentication rather than by the ceremony, and the title is what tells
        // the two 401s apart.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsNotEqualTo(PasskeyVerificationExceptionHandler.Title);
    }

    /// <summary>
    /// The control for the refusal above: an endpoint that answered 401 to everybody would satisfy it
    /// and issue no challenge to anyone.
    /// </summary>
    [Test]
    public async Task ReauthenticationOptions_ForAnAuthenticatedCaller_IssueAChallenge()
    {
        // Arrange — the account is established on a route that may mint one, because this leg no longer
        // does. Without that first request the challenge would be refused for having no account behind
        // it, and the refusal above would look like it held for a caller who really was authenticated.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);

        // Act
        HttpResponseMessage response = await client.PostAsync(ReauthenticationOptionsPath, content: null);
        JsonNode options = await ReadJsonAsync(response);

        // Assert — the same discoverable-credential shape the sign-in leg issues, over a nonce of the
        // length the store emits. No allowCredentials: the account is known here, so enumeration is
        // not the argument — handing this account's credential handles to whoever holds the bearer
        // token would give the stolen-session adversary something it did not have.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Base64UrlText.Decode(options["challenge"]!.GetValue<string>()).Length).IsEqualTo(32);
        await Assert.That(options["rpId"]!.GetValue<string>()).IsEqualTo(ApiFactory.PasskeyRelyingPartyId);
        await Assert.That(options.AsObject().ContainsKey("allowCredentials")).IsFalse();
    }

    /// <summary>
    /// Alice's bearer token, Bob's registered passkey, a correct signature and a live nonce Alice
    /// herself was issued — and <b>neither</b> account loses a row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "neither" in the name is the whole point. The wrong design here destroys one of each:
    /// publishing the credential's owner mid-request while its budget stays resolved moves
    /// <c>app.current_user_id</c> and not <c>app.current_budget_id</c>, which
    /// <c>SessionContextInterceptor</c> fixed when the connection opened — so the transactions delete
    /// empties <b>Alice's</b> budget while the user delete removes <b>Bob's</b> row. Two accounts
    /// damaged, neither as asked. Asserting only that the request was refused, or only that Alice
    /// survived, misses half of that.
    /// </para>
    /// <para>
    /// That pairing is no longer one edit away, which is why this test still asserts both halves rather
    /// than only Alice's: <c>CurrentUserWriter.ResolveUser</c> clears the ambient budget with every
    /// publication, so a republication on its own now kills the request at <c>IBudgetContext.BudgetId</c>
    /// instead of emptying anything. The clearing is one line in a type this gate does not own, and a
    /// <c>ResolveBudget</c> written beside a republication puts the two-account outcome straight back.
    /// </para>
    /// <para>
    /// The response carries <b>no</b> user handle, and the reason is not that the handle check would
    /// otherwise get there first — it would not. <c>PasskeyReauthentication</c> runs the owner-scoped
    /// lookup at step 4 and the handle check at step 5, so Bob's credential is already refused by the
    /// lookup whatever the handle says; an absent handle is tolerated and a present one only ever
    /// narrows further. Omitting it is what keeps this refusal attributable to a single cause: with
    /// Alice's own handle present, a lookup that had lost its owner filter would still be turned down
    /// by the check below it, and this test would stay green over a gate with no binding left.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Erasure_WithAnotherAccountsPasskey_IsRefusedAndErasesNeitherAccount()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient alice = host.Factory.CreateAuthenticatedClient(Subject);
        HttpClient bob = host.Factory.CreateAuthenticatedClient(OtherSubject);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(bob, bobsDevice);

        // Alice signs in too, so she owns rows an erasure could take.
        (await alice.GetAsync("/api/accounts")).EnsureSuccessStatusCode();
        Guid aliceId = await ResolveUserIdAsync(host, Subject);
        Guid bobId = await ResolveUserIdAsync(host, OtherSubject);

        // Bob only: Alice never registered a passkey here, so she has no factor these envelopes could
        // hang off — and no assertion below asks her wrapped-key count to be non-zero. Her zero is
        // compared against her zero like every other count.
        await SeedWrappedAccountKeysAsync(host, bobId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> aliceBefore = await CountOwnedRowsAsync(admin, aliceId);
        IReadOnlyDictionary<string, long> bobBefore = await CountOwnedRowsAsync(admin, bobId);
        await Assert.That(aliceBefore["users"]).IsGreaterThan(0L);
        await Assert.That(aliceBefore["budgets"]).IsGreaterThan(0L);
        await AssertEverythingIsSeededAsync(bobBefore);

        // Act — the nonce is issued on Alice's own authenticated options call, so nothing about the
        // challenge is stale or foreign; only the credential answering it belongs to somebody else.
        byte[] challenge = await BeginCeremonyAsync(alice, ReauthenticationOptionsPath);
        AssertionResult assertion = bobsDevice.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            userHandle: null);
        HttpResponseMessage response = await PostErasureAsync(alice, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await AssertNothingMovedAsync(admin, aliceId, aliceBefore);
        await AssertNothingMovedAsync(admin, bobId, bobBefore);
    }

    /// <summary>
    /// A session token alone buys nothing, stated directly: an authenticated request carrying no proof
    /// at all is refused.
    /// </summary>
    /// <remarks>
    /// The status is <b>401</b> rather than a framework 400, and that is a checked expectation rather
    /// than an assumption. The erasure request record declares its members non-<c>required</c>, the
    /// identical shape the sign-in leg's own <c>AssertionRequest</c> has, and <c>Program.cs</c>
    /// registers no model validation — so <c>{}</c> binds every member to <see langword="null" /> and
    /// the gate's own <c>PasskeyEncoding.TryDecode</c> is the first thing to see it. That refusal is
    /// the same one every other refusal on this endpoint produces, which is what keeps a caller from
    /// telling "you sent nothing" apart from "your proof was wrong".
    /// </remarks>
    [Test]
    public async Task Erasure_WithNoAssertionMembers_IsRefused()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        (await client.GetAsync("/api/accounts")).EnsureSuccessStatusCode();
        Guid userId = await ResolveUserIdAsync(host, Subject);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await Assert.That(before["users"]).IsGreaterThan(0L);
        await Assert.That(before["budgets"]).IsGreaterThan(0L);

        // Act — a valid bearer token and an empty object, which is every member absent.
        HttpResponseMessage response = await client.PostAsJsonAsync(ErasurePath, new { });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await AssertNothingMovedAsync(admin, userId, before);
    }

    /// <summary>
    /// The old token-only route is gone, not left beside the new one.
    /// </summary>
    /// <remarks>
    /// This is the only test that would notice <c>DELETE /api/me</c> being kept for compatibility —
    /// and keeping it would make every other test in this file describe a door that stands beside an
    /// open one.
    /// </remarks>
    [Test]
    public async Task Erasure_ByTheOldDeleteRoute_IsNotRouted()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        (await client.GetAsync("/api/accounts")).EnsureSuccessStatusCode();
        Guid userId = await ResolveUserIdAsync(host, Subject);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await Assert.That(before["users"]).IsGreaterThan(0L);

        // Act
        HttpResponseMessage response = await client.DeleteAsync(LegacyErasurePath);

        // Assert — either answer says the same thing: no handler is mapped there any more. Both are
        // accepted because which one routing produces depends on whether any verb answers that path,
        // and that is not a property this test is about.
        await Assert.That(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            .IsTrue();
        await AssertNothingMovedAsync(admin, userId, before);
    }

    [Test]
    public async Task Erasure_WithATamperedSignature_IsRefusedAndErasesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await SeedWrappedAccountKeysAsync(host, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await AssertEverythingIsSeededAsync(before);

        // Act — genuine in every respect except one bit of the signature.
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));
        HttpResponseMessage response = await PostErasureAsync(client, WithFlippedSignature(assertion));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await AssertNothingMovedAsync(admin, userId, before);
    }

    /// <summary>
    /// One nonce, one erasure — the successful spend, pinned on an account that is still there to be
    /// counted afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious shape — post the identical request twice for the same account — no longer measures
    /// single use at all. The first post returns 204 and destroys the account, so the replay arrives
    /// with a provider token that outlives the erasure by up to an hour, naming an account that no
    /// longer exists, and <c>UserProvisioningMiddleware</c> turns it away one step <b>before</b> the
    /// ceremony. That refusal is identical whether the nonce is single use or not, which makes it no
    /// evidence.
    /// </para>
    /// <para>
    /// So the spend and the replay are split across two accounts, which the nonce pool allows: a
    /// re-authentication challenge is issued bound to no user — <see cref="BeginReauthenticationHandler" />
    /// writes the pool and nothing else — and the gate only ever asks the store whether it is live.
    /// Bob's replay is therefore faultless in every respect the gate checks except one: his own
    /// registered device, his own user handle, the allowed origin, the re-authentication pool, a
    /// correct signature, and a nonce Alice's successful erasure already spent. Bob survives it, so
    /// "and erased nothing" is a countable claim here rather than an unobservable one.
    /// </para>
    /// <para>
    /// Not covered by <see cref="Erasure_WhenVerificationFails_StillConsumesTheChallenge" />. That one
    /// proves a <b>failed</b> attempt burns the nonce; this one proves a <b>successful</b> one does,
    /// which is the path every real erasure takes and the only path on which the account disappears
    /// underneath the evidence.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Erasure_ReplayingAChallengeASuccessfulErasureSpent_IsRefusedAndErasesNothing()
    {
        // Arrange — Alice, who erases, and Bob, who replays her spent nonce and must come through it
        // whole.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient alice = host.Factory.CreateAuthenticatedClient(Subject);
        HttpClient bob = host.Factory.CreateAuthenticatedClient(OtherSubject);
        SyntheticAuthenticator alicesDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(alice, alicesDevice);
        await RegisterPasskeyAsync(bob, bobsDevice);
        Guid aliceId = await ResolveUserIdAsync(host, Subject);
        Guid bobId = await ResolveUserIdAsync(host, OtherSubject);

        // Both, because both registered a passkey: Alice's row is what her successful erasure has to
        // carry away through the cascade, and Bob's is what the replay must not touch.
        await SeedWrappedAccountKeysAsync(host, aliceId);
        await SeedWrappedAccountKeysAsync(host, bobId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> bobBefore = await CountOwnedRowsAsync(admin, bobId);
        await AssertEverythingIsSeededAsync(bobBefore);

        // Act — one challenge, spent by an erasure that succeeds, then answered again by an account
        // that erasure did not touch.
        byte[] challenge = await BeginCeremonyAsync(alice, ReauthenticationOptionsPath);
        HttpResponseMessage erasure = await PostErasureAsync(
            alice,
            alicesDevice.Authenticate(challenge, ApiFactory.PasskeyOrigin, PasskeyEncoding.ToUserHandle(aliceId)));
        HttpResponseMessage replay = await PostErasureAsync(
            bob,
            bobsDevice.Authenticate(challenge, ApiFactory.PasskeyOrigin, PasskeyEncoding.ToUserHandle(bobId)));
        IReadOnlyDictionary<string, long> bobAfterReplay = await CountOwnedRowsAsync(admin, bobId);

        // The control, and it runs last so the counts above are read while Bob is still whole: the same
        // device answering a nonce of Bob's own is accepted. Without it, a replay refused for anything
        // whatever about Bob — his device, his handle, his account — reads exactly like a nonce
        // refusal. His device may report the same counter it reported a moment ago because the replay
        // was turned away at the challenge store, four steps before the counter is even read, so
        // nothing recorded that attempt.
        HttpResponseMessage withANonceOfHisOwn = await EraseAsync(bob, bobsDevice, bobId);

        // Assert
        await Assert.That(erasure.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(replay)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That(withANonceOfHisOwn.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Compared table by table rather than to "still more than zero": a gate that took some of Bob's
        // rows on the strength of Alice's nonce and left others would pass a non-zero check.
        foreach (OwnedTable table in UserOwnedTables)
        {
            await Assert.That(bobAfterReplay[table.Name]).IsEqualTo(bobBefore[table.Name]);
        }
    }

    /// <summary>
    /// A failed attempt has to burn the nonce, or one issued challenge is something an attacker can
    /// grind responses against — in front of account destruction.
    /// </summary>
    /// <remarks>
    /// The second attempt is a <b>fully valid</b> assertion over the same challenge, so only a nonce
    /// the failure already spent can refuse it. Written as an outcome: the account is still there.
    /// </remarks>
    [Test]
    public async Task Erasure_WhenVerificationFails_StillConsumesTheChallenge()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await SeedWrappedAccountKeysAsync(host, userId);
        byte[] userHandle = PasskeyEncoding.ToUserHandle(userId);
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await AssertEverythingIsSeededAsync(before);

        // Act
        AssertionResult failing = device.Authenticate(challenge, ApiFactory.PasskeyOrigin, userHandle);
        HttpResponseMessage refused = await PostErasureAsync(client, WithFlippedSignature(failing));

        AssertionResult valid = device.Authenticate(challenge, ApiFactory.PasskeyOrigin, userHandle);
        HttpResponseMessage afterFailure = await PostErasureAsync(client, valid);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(afterFailure.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await AssertNothingMovedAsync(admin, userId, before);
    }

    /// <summary>
    /// Every refusal this endpoint can reach, driven end to end and compared whole — status and body
    /// together, not the body alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same argument the sign-in leg makes, sharpened: a caller able to tell "that passkey is not
    /// yours" from "that challenge was for another ceremony" is a caller mapping which handles exist
    /// and which pool a nonce came from, while holding a stolen bearer token. Two reasons compared
    /// against each other prove only that those two agree, so this drives all of them and asserts they
    /// collapse to one value.
    /// </para>
    /// <para>
    /// <b>What this list cannot show is how deep an entry got.</b> Nothing here can observe which step
    /// refused a request — that indistinguishability is the property being asserted — so an entry that
    /// quietly began failing at an earlier step than the one it was written for stays green and stops
    /// covering the step it was added for. The counter regression is the single exception, and only
    /// because it is the one entry whose step would, if it disappeared, let the erasure through and
    /// change the body of every entry after it. Read every "refused at step N" below as what the entry
    /// was built to reach, not as something this comparison measures.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryReachableErasureRefusal_ProducesTheIdenticalResponse()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        HttpClient anonymous = host.Factory.CreateClient();
        HttpClient bob = host.Factory.CreateAuthenticatedClient(OtherSubject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // A second device of this account's own, registered at a counter none of its own assertions
        // will reach. Kept apart from the device above rather than registering that one high, so the
        // entries that predate it are still driven by exactly the device they always were.
        SyntheticAuthenticator regressedDevice =
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        await RegisterPasskeyAsync(client, regressedDevice, RegisteredCounterAboveAnyAssertion);
        await RegisterPasskeyAsync(bob, bobsDevice);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await SeedWrappedAccountKeysAsync(host, userId);
        byte[] userHandle = PasskeyEncoding.ToUserHandle(userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await AssertEverythingIsSeededAsync(before);

        // Act
        List<(string Reason, HttpResponseMessage Response)> refusals = [];

        byte[] originChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        refusals.Add((
            "untrusted origin",
            await PostErasureAsync(client, device.Authenticate(originChallenge, LookalikeOrigin, userHandle))));

        byte[] assertionChallenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        refusals.Add((
            "assertion challenge",
            await PostErasureAsync(
                client,
                device.Authenticate(assertionChallenge, ApiFactory.PasskeyOrigin, userHandle))));

        byte[] registrationChallenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        refusals.Add((
            "registration challenge",
            await PostErasureAsync(
                client,
                device.Authenticate(registrationChallenge, ApiFactory.PasskeyOrigin, userHandle))));

        // No user handle, so the owner-scoped credential lookup is the only thing that can refuse it.
        byte[] strangerChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        refusals.Add((
            "another account's passkey",
            await PostErasureAsync(
                client,
                bobsDevice.Authenticate(strangerChallenge, ApiFactory.PasskeyOrigin, userHandle: null))));

        // One challenge answered twice: the tampered attempt burns it, so the second post is a
        // faultless response refused for nothing but the nonce already being spent.
        byte[] spentChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult answered = device.Authenticate(spentChallenge, ApiFactory.PasskeyOrigin, userHandle);
        refusals.Add(("invalid signature", await PostErasureAsync(client, WithFlippedSignature(answered))));
        refusals.Add(("consumed challenge", await PostErasureAsync(client, answered)));

        refusals.Add(("no assertion members", await client.PostAsJsonAsync(ErasurePath, new { })));

        // This account's own device, its own live nonce, a correct signature — and a handle naming an
        // account that does not exist. The only entry refused AT step 5, which is not the same as the
        // only one to reach it: every other entry either omits the handle, is turned away before the
        // lookup step 5 stands behind, or carries this account's own handle and passes straight
        // through the check — the two entries below it do the last of those.
        byte[] handleChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        refusals.Add((
            "user handle mismatch",
            await PostErasureAsync(
                client,
                device.Authenticate(
                    handleChallenge,
                    ApiFactory.PasskeyOrigin,
                    PasskeyEncoding.ToUserHandle(Guid.CreateVersion7())))));

        // A webauthn.create ceremony type on the endpoint that verifies webauthn.get. The substituted
        // type is signed for real, so the refusal comes from the type check inside the verifier rather
        // than from a signature that never covered the value that was swapped.
        byte[] createTypeChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        refusals.Add((
            "webauthn.create client data type",
            await PostErasureAsync(
                client,
                device.Authenticate(
                    createTypeChallenge,
                    ApiFactory.PasskeyOrigin,
                    userHandle,
                    clientDataTypeOverride: CollectedClientData.RegistrationType))));

        // The counter regression, and the one entry here that is faultless in every respect the gate
        // checks up to step 7: live re-authentication nonce, this account's own registered device, its
        // own user handle, the allowed origin, a correct signature. Only the reported counter is wrong,
        // and it is wrong because the device registered above the value it now reports.
        //
        // It is also the only refusal on this endpoint that the gate translates itself — a domain
        // ValidationException about the counter turned into a PasskeyVerificationException — so
        // without this entry nothing anywhere proves that a regression answers 401 rather than the 500
        // an untranslated ValidationException would produce. And it is self-checking in a way the
        // others are not: were the translation or the counter check to disappear, this erasure would
        // SUCCEED, and the entries after it would then be refused by provisioning for an account that
        // no longer exists — a different body, which the comparison below reports rather than misses.
        byte[] counterChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        refusals.Add((
            "counter regression",
            await PostErasureAsync(
                client,
                regressedDevice.Authenticate(counterChallenge, ApiFactory.PasskeyOrigin, userHandle))));

        // Last, and the ordering is load-bearing: the store sweeps expired rows on every issue, so an
        // expired challenge inserted before any of the options calls above would be collected by one
        // of them and this entry would be refused for a nonce nobody ever issued instead.
        byte[] expiredChallenge = await InsertChallengeAsync(host, "reauthentication", expiresInMinutes: -5);
        refusals.Add((
            "expired challenge",
            await PostErasureAsync(
                client,
                device.Authenticate(expiredChallenge, ApiFactory.PasskeyOrigin, userHandle))));

        List<(string Reason, string Response)> observed = [];
        foreach ((string reason, HttpResponseMessage response) in refusals)
        {
            observed.Add((reason, $"{(int)response.StatusCode} {await ReadComparableBodyAsync(response)}"));
        }

        // Assert — each refusal against the first, with its own name on both sides of the comparison
        // so a failure says which one drifted rather than only that something did.
        string first = observed[0].Response;
        foreach ((string reason, string response) in observed)
        {
            await Assert.That($"{reason} => {response}").IsEqualTo($"{reason} => {first}");
        }

        await Assert.That(observed.Count).IsEqualTo(ReachableErasureRefusals);
        await Assert.That(observed.Select(entry => entry.Response).Distinct().Count()).IsEqualTo(1);
        await Assert.That(refusals[0].Response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(first.Contains(PasskeyVerificationExceptionHandler.Title, StringComparison.Ordinal))
            .IsTrue();
        await AssertNothingMovedAsync(admin, userId, before);
    }

    /// <summary>
    /// The five-minute window, measured on a challenge that is genuinely past it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The data is faked, not the clock — the pattern
    /// <c>PasskeyCeremonyTests.AssertionOptions_RemoveChallengesThatHaveExpired</c> already uses. A
    /// fake <see cref="TimeProvider" /> over HTTP would need a new package reference and a clock that
    /// also governs user provisioning and session expiry inside the same host, which is a far wider
    /// blast radius than this rule needs. The row is inserted on the container superuser with its own
    /// 32 random bytes, and the device then signs <b>those exact bytes</b>, so everything about the
    /// request is genuine except how old the nonce is.
    /// </para>
    /// <para>
    /// <c>created_at_utc</c> is ten minutes back and <c>expires_at_utc</c> five, because
    /// <c>CK_webauthn_challenges_lifetime</c> refuses a row that was never live for an instant. The
    /// insert happens <b>after</b> the registration, since the store's opportunistic sweep runs on
    /// every issue and the registration options leg is an issue.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Erasure_OnAChallengeOlderThanTheWindow_IsRefusedAndErasesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await SeedWrappedAccountKeysAsync(host, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await AssertEverythingIsSeededAsync(before);

        // Act
        byte[] challenge = await InsertChallengeAsync(host, "reauthentication", expiresInMinutes: -5);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));
        HttpResponseMessage response = await PostErasureAsync(client, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await AssertNothingMovedAsync(admin, userId, before);
    }

    /// <summary>
    /// The provable-fail control for the expiry test: the identical row, inserted the identical way,
    /// differing only in <c>expires_at_utc</c>.
    /// </summary>
    /// <remarks>
    /// Without it, a gate that refused every out-of-band-inserted challenge for some entirely
    /// unrelated reason — a ceremony value it never learned to read, say — passes the expiry test
    /// vacuously, and that test would say nothing whatever about expiry.
    /// </remarks>
    [Test]
    public async Task Erasure_OnALiveChallengeInsertedTheSameWay_ReturnsNoContent()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid userId = await ResolveUserIdAsync(host, Subject);
        await SeedWrappedAccountKeysAsync(host, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId);
        await AssertEverythingIsSeededAsync(before);

        // Act
        byte[] challenge = await InsertChallengeAsync(host, "reauthentication", expiresInMinutes: 5);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));
        HttpResponseMessage response = await PostErasureAsync(client, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        IReadOnlyDictionary<string, long> after = await CountOwnedRowsAsync(admin, userId);
        foreach (string table in UserOwnedTables.Select(owned => owned.Name))
        {
            await Assert.That(after[table]).IsEqualTo(0L);
        }
    }

    /// <summary>
    /// The elapsed check is server-side because there is nothing on the wire for a client to assert
    /// about time in the first place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Testing the absence of an input means pinning the shape, so this is a structural test rather
    /// than a behavioural one, and it is deliberately in two halves. The options response is the only
    /// thing the server hands the client during this ceremony: its members are pinned exactly, so an
    /// issued-at or expires-at instant added there — the natural first step towards a client-supplied
    /// elapsed time — moves this test. <c>timeout</c> is a duration in milliseconds rather than an
    /// instant, which is why it is not the thing being excluded.
    /// </para>
    /// <para>
    /// The other half reflects over <see cref="ReauthenticationAssertion" /> — the Application-layer
    /// record the endpoint's own request record maps straight onto, and the one of the two that is
    /// public — and asserts it declares no member of a time type. The member count is asserted beside
    /// it so that a reflection that started looking at nothing at all cannot pass by finding nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Erasure_SendsNoTimestampAndReadsNone()
    {
        // Arrange — the account is established on a route that may mint one. The options leg below no
        // longer provisions, so without this the challenge request is refused for having no account
        // behind it and the shape this test pins would never be issued.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);
        Type[] timeTypes =
        [
            typeof(DateTime), typeof(DateTime?),
            typeof(DateTimeOffset), typeof(DateTimeOffset?),
            typeof(DateOnly), typeof(DateOnly?),
            typeof(TimeOnly), typeof(TimeOnly?),
            typeof(TimeSpan), typeof(TimeSpan?),
        ];

        // Act
        JsonObject options = (await PostForJsonAsync(client, ReauthenticationOptionsPath)).AsObject();
        PropertyInfo[] members = typeof(ReauthenticationAssertion)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Assert
        await Assert.That(options.Select(member => member.Key).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(new[] { "challenge", "rpId", "timeout", "userVerification" });
        await Assert.That(members.Length).IsEqualTo(5);
        await Assert.That(members.Count(member => timeTypes.Contains(member.PropertyType))).IsEqualTo(0);
    }

    /// <summary>
    /// Which id a table files its owner under, so the enumeration below cannot guess from a column
    /// name and keep working right up until a table carries both.
    /// </summary>
    private readonly record struct OwnedTable(string Name, string OwnerColumn);

    /// <summary>
    /// The user-owned tables these tests count. Budget-owned tables are covered whole by
    /// <c>AccountErasureEndpointTests</c>; what this file needs is the identity graph, because that is
    /// what a wrongly bound gate reaches into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>wrapped_account_keys</c> belongs to that graph and is the one row on it that is worth more
    /// than the account: it holds the account's content and index keys as one recovery factor wrapped
    /// them. A gate bound to the wrong account is exactly the request that must not reach them, so the
    /// table is counted like the rest. <see cref="SeedWrappedAccountKeysAsync" /> is what puts a row
    /// there, because no endpoint writes one yet.
    /// </para>
    /// <para>
    /// <b>This list is hand-written and nothing checks it against the live schema</b>, which is how
    /// <c>wrapped_account_keys</c> could join the database and leave every test in this file green
    /// while counting one identity table less than the prose claims. The next table added here has to
    /// be added by hand, exactly as this one was.
    /// </para>
    /// </remarks>
    private static readonly OwnedTable[] UserOwnedTables =
    [
        new("users", "id"),
        new("credentials", "user_id"),
        new("budgets", "user_id"),
        new("passkey_public_keys", "user_id"),
        new("passkey_signature_counters", "user_id"),
        new("wrapped_account_keys", "user_id"),
    ];

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Runs both authenticated legs of a registration, so the account really holds a passkey the gate
    /// can verify against — rather than material seeded out of band that no signature answers to.
    /// </summary>
    /// <remarks>
    /// The account is established first, on a route that is allowed to mint one. Neither passkey leg
    /// provisions any more — only the data route groups do — so a registration is the second
    /// authenticated request an account makes, never the first. Every refusal this file drives comes
    /// from an account that exists, which is what keeps them all the ceremony's own 401 rather than
    /// provisioning's.
    /// </remarks>
    /// <param name="client">The authenticated caller the passkey is filed under.</param>
    /// <param name="device">The authenticator that registers.</param>
    /// <param name="signCount">
    /// The counter the registration response reports, which is the value the counter row opens at.
    /// Zero for every ordinary registration; the parameter exists so one test can register a device
    /// above the counter its own assertions report and reach the clone check on the first try.
    /// </param>
    private static async Task RegisterPasskeyAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        uint signCount = 0)
    {
        await ApiFactory.EstablishAccountAsync(client);

        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount,
            prfEnabled: true);
        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Runs the whole erasure ceremony: options leg, authenticator, erasure request.</summary>
    private static async Task<HttpResponseMessage> EraseAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));

        return await PostErasureAsync(client, assertion);
    }

    private static Task<HttpResponseMessage> PostErasureAsync(HttpClient client, AssertionResult result) =>
        client.PostAsJsonAsync(ErasurePath, new
        {
            credentialId = result.CredentialIdBase64Url,
            clientDataJson = result.ClientDataJsonBase64Url,
            authenticatorData = result.AuthenticatorDataBase64Url,
            signature = result.SignatureBase64Url,
            userHandle = result.UserHandleBase64Url,
        });

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        JsonNode options = await PostForJsonAsync(client, path);
        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    private static async Task<JsonNode> PostForJsonAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response);
    }

    /// <summary>
    /// The same ceremony with one bit of the signature moved, so the response is genuine in every
    /// respect except the one under test.
    /// </summary>
    private static AssertionResult WithFlippedSignature(AssertionResult result)
    {
        byte[] signature = [.. result.Signature];
        signature[^1] ^= 0xFF;
        return result with { Signature = signature };
    }

    /// <summary>
    /// Writes a challenge row of a named ceremony directly and hands back its bytes, so an
    /// authenticator can sign a nonce whose age this test chose.
    /// </summary>
    /// <remarks>
    /// The bytes are returned because signing them is the whole point — the existing
    /// <c>PasskeyCeremonyTests.InsertExpiredChallengeAsync</c> writes <c>new byte[32]</c> and hands
    /// back only the row id, which is all its sweep test needs and none of what this one does.
    /// <c>created_at_utc</c> is always ten minutes back, because
    /// <c>CK_webauthn_challenges_lifetime</c> refuses a row whose expiry is at or before its creation.
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
    /// Reads back the user provisioning minted for <paramref name="subject" />. Nothing the API
    /// returns names it, so the lookup goes through the credential the middleware resolved on.
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
    /// Files the account's two keys against the passkey a real registration just wrote, so
    /// <c>wrapped_account_keys</c> holds a row for <paramref name="userId" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written out of band on the container superuser, through the domain factory rather than raw SQL —
    /// the choice the two other erasure files make for their own seeding, and for the same reason: there
    /// is no route that writes one of these rows, and the factory is what keeps a seeded row the shape
    /// production will write. It hangs off the account's <b>real</b> passkey credential rather than a
    /// credential of its own, because that is the factor whose PRF output derives the key-encryption
    /// key, and because every caller here has already registered one.
    /// </para>
    /// <para>
    /// Only the accounts that register a passkey are seeded, and the accounts that do not are not
    /// exceptions to the non-vacuity rule: no test asserts a seeded count for one of those, so their
    /// zero before and zero after is compared like every other count.
    /// </para>
    /// </remarks>
    private static async Task SeedWrappedAccountKeysAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);

        // First rather than Single: one test registers two devices for the same account, and either
        // credential is a factor these envelopes could legitimately be wrapped under.
        Credential passkey = await db.Credentials
            .Where(credential => credential.UserId == userId
                                 && credential.Type == CredentialType.Passkey)
            .OrderBy(credential => credential.CreatedAtUtc)
            .FirstAsync();

        db.WrappedAccountKeys.Add(WrappedAccountKeys.For(
            passkey,

            // Minted here rather than derived from the owner, which is what production does: the value
            // is chosen by the client and its unique index is global. Nothing asserts on it, and a fresh
            // one per call is what keeps the two accounts of a two-account test from colliding on it.
            Guid.CreateVersion7(),
            Envelope(0xC0),
            Envelope(0x1D),
            SeedInstant));

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A well-formed wrapped-key envelope: the one version byte the contract defines, then filler.
    /// </summary>
    /// <remarks>
    /// The filler is neither a nonce nor a ciphertext, and nothing here opens either — no unlock path
    /// exists and this server holds no value that could. What the row has to satisfy is the width and
    /// the version, which <see cref="WrappedAccountKeys.For" /> and two check constraints per column
    /// both refuse to bend. The two arguments differ so the columns can be told apart by eye in a
    /// failure message.
    /// </remarks>
    private static byte[] Envelope(byte filler)
    {
        byte[] envelope = new byte[WrappedAccountKeys.EnvelopeLength];
        Array.Fill(envelope, filler);
        envelope[0] = WrappedAccountKeys.EnvelopeVersion;

        return envelope;
    }

    /// <summary>
    /// Fixed UTC instant for the out-of-band row. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static async Task<IReadOnlyDictionary<string, long>> CountOwnedRowsAsync(
        NpgsqlConnection connection,
        Guid userId)
    {
        Dictionary<string, long> counts = new(UserOwnedTables.Length, StringComparer.Ordinal);

        foreach (OwnedTable table in UserOwnedTables)
        {
            // The table and column names are compile-time constants from the private list above, not
            // anything a caller supplies; the owner id is bound as a parameter like everywhere else.
            await using NpgsqlCommand command = new(
                $"select count(*) from {table.Name} where {table.OwnerColumn} = @owner",
                connection);
            command.Parameters.AddWithValue("owner", userId);
            counts[table.Name] = await command.ExecuteScalarAsync() switch
            {
                long count => count,
                var unexpected => throw new InvalidOperationException(
                    $"Expected a count from '{table.Name}', got '{unexpected ?? "null"}'."),
            };
        }

        return counts;
    }

    /// <summary>
    /// The other half of every "nothing was erased" assertion. Without it a refusal test whose seeding
    /// silently did nothing passes with the account it was meant to protect never having existed.
    /// </summary>
    private static async Task AssertEverythingIsSeededAsync(IReadOnlyDictionary<string, long> before)
    {
        foreach (OwnedTable table in UserOwnedTables)
        {
            await Assert.That(before[table.Name]).IsGreaterThan(0L);
        }
    }

    /// <summary>
    /// Compares each table's count to what it was rather than merely to "more than zero": a gate that
    /// took some of an account's rows and left others would pass a non-zero check.
    /// </summary>
    private static async Task AssertNothingMovedAsync(
        NpgsqlConnection connection,
        Guid userId,
        IReadOnlyDictionary<string, long> before)
    {
        IReadOnlyDictionary<string, long> after = await CountOwnedRowsAsync(connection, userId);
        foreach (OwnedTable table in UserOwnedTables)
        {
            await Assert.That(after[table.Name]).IsEqualTo(before[table.Name]);
        }
    }

    /// <summary>
    /// The whole response body, with the one member that varies per <b>request</b> rather than per
    /// <b>cause</b> replaced by a fixed placeholder.
    /// </summary>
    /// <remarks>
    /// <c>traceId</c> is a new value on every request, including two requests refused for the
    /// identical reason, so comparing it would compare the trace and not the refusal. The member is
    /// replaced rather than removed, so a <c>traceId</c> that stopped being emitted still fails and
    /// any other member appearing, disappearing or differing fails with it.
    /// </remarks>
    private static async Task<string> ReadComparableBodyAsync(HttpResponseMessage response)
    {
        JsonObject body = (await ReadJsonAsync(response)).AsObject();
        if (body.ContainsKey(TraceIdMember))
        {
            body[TraceIdMember] = "<one per request>";
        }

        return body.ToJsonString();
    }

    private const string TraceIdMember = "traceId";

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response))["title"]!.GetValue<string>();
}
