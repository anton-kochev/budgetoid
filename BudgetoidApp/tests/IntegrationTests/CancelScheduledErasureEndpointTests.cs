using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Domain.Erasure;
using Domain.Sessions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// <c>POST /api/me/erasure/schedule/cancellation</c>: a full session withdraws the account's scheduled
/// erasure with a fresh passkey assertion, answered from a real request over the least-privilege role —
/// whom it refuses, what it removes, and what it leaves alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The threat is the locked session.</b> A schedule is what somebody holding the owner's provider
/// account can file; the cancellation is what the owner, holding a passkey, does about it. If a locked
/// session could reach this route, the stolen provider account could also withdraw what the owner filed
/// — so the refusal is paired with the same assertion succeeding from a full session on the same account,
/// which proves the assertion was good and the nonce unspent by the refusal.
/// </para>
/// <para>
/// <b>Sessions and passkeys are seeded through the database</b>, in the shape
/// <see cref="ErasureScheduleEndpointTests" /> seeds them, with a passkey whose public key is a
/// <see cref="SyntheticAuthenticator" />'s so a real signature verifies against it. The clock is fixed
/// before the host is built, and the challenge store reads the same clock, so a nonce minted here is
/// judged against the instant this file chose.
/// </para>
/// <para>
/// Store reads go over <see cref="RepositoryTestHost.ConnectionString" />, the superuser, because
/// <c>erasure_schedules</c> is policed on its owner and a policed read reports an absent row exactly as it
/// reports a hidden one.
/// </para>
/// </remarks>
public sealed class CancelScheduledErasureEndpointTests
{
    private const string CancellationPath = "/api/me/erasure/schedule/cancellation";
    private const string ErasurePath = "/api/me/erasure";
    private const string SessionPath = "/api/me/session";
    private const string AccountsPath = "/api/accounts";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";

    /// <summary>
    /// A locked session presenting a valid assertion is refused 403, the schedule stands to the
    /// microsecond, and the refusal is every other session-kind refusal's body; the same assertion from a
    /// full session on the same account then succeeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pairing is the test.</b> A route refusing everybody satisfies the refusal; the full-session
    /// arm, run second with the identical assertion, is what makes it a verdict on the session's kind. It
    /// also proves the refusal never reached the gate: the nonce is single use, so a locked request that
    /// had consumed it would leave the full arm a 401.
    /// </para>
    /// <para>
    /// The stored instant carries microseconds, so "stands" is a byte-equal claim about the row rather
    /// than one a rewrite rounded to the second would satisfy.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Cancel_FromALockedSession_IsRefused403_AndTheScheduleStands()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, device, fill: 0x22);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        await SeedScheduleAsync(host, owner.UserId, OwnerTakesEffectAt);
        HttpClient client = factory.CreateClient();
        AssertionResult assertion = await ReauthenticateAsync(client, full, device, owner.UserId);

        // Act
        HttpResponseMessage lockedResponse = await SendAsync(client, CancellationPath, locked, BodyOf(assertion));
        (Guid, DateTime)[] afterTheLockedRequest = await StoredInstantsAsync(host);
        HttpResponseMessage lockedRefusal = await SendAsync(client, AccountsPath, locked, body: null, HttpMethod.Get);
        HttpResponseMessage fullResponse = await SendAsync(client, CancellationPath, full, BodyOf(assertion));

        // Assert — the control: the same assertion, from a full session on the same account, is accepted.
        await Assert.That(fullResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The refusal, and the schedule exactly as it was filed.
        await Assert.That(lockedResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(afterTheLockedRequest).IsEquivalentTo(new[] { (owner.UserId, OwnerTakesEffectAt) });

        // The same answer every other refusal of a session's kind carries, and not the CSRF control's.
        await Assert.That(lockedRefusal.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string lockedBody = await ComparableBodyOfAsync(lockedResponse);
        await Assert.That(lockedBody).IsEqualTo(await ComparableBodyOfAsync(lockedRefusal));
        await Assert.That(lockedBody).DoesNotContain(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// A full session sending no assertion members at all is refused 401 with the body the immediate
    /// erasure answers the same request, and the schedule stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The session alone buys nothing.</b> A route that read "a full session" as proof enough would
    /// answer 204 here, which is the whole of what the gate exists to refuse.
    /// </para>
    /// <para>
    /// <b>Compared whole against <c>POST /api/me/erasure</c>'s answer to the same <c>{}</c></b>, so the two
    /// passkey-gated routes cannot drift apart, and the <c>refusal</c> member is checked by value: every
    /// passkey refusal in the product carries <c>"assertion"</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Cancel_WithoutAnAssertion_Is401_AndTheScheduleStands()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, device, fill: 0x22);
        await SeedScheduleAsync(host, owner.UserId, OwnerTakesEffectAt);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, CancellationPath, full, new { });
        (Guid, DateTime)[] stored = await StoredInstantsAsync(host);
        HttpResponseMessage erasureRefusal = await SendAsync(client, ErasurePath, full, new { });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(stored).IsEquivalentTo(new[] { (owner.UserId, OwnerTakesEffectAt) });

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body["title"]?.GetValue<string>()).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That(body["refusal"]?.GetValue<string>()).IsEqualTo(PasskeyVerificationExceptionHandler.Refusal);

        await Assert.That(erasureRefusal.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ComparableBodyOfAsync(response)).IsEqualTo(await ComparableBodyOfAsync(erasureRefusal));
    }

    /// <summary>
    /// A live, unspent, correctly signed nonce from the sign-in pool is refused 401 and the schedule
    /// stands.
    /// </summary>
    /// <remarks>
    /// The sign-in pool is minted by an anonymous route, so anybody able to walk the owner through one
    /// prompt holds such an assertion over a moment they chose. A gate that checked the consume for a
    /// non-null answer rather than for the re-authentication pool passes every other test here.
    /// </remarks>
    [Test]
    public async Task Cancel_OnAnAuthenticationPoolChallenge_Is401_AndTheScheduleStands()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, device, fill: 0x22);
        await SeedScheduleAsync(host, owner.UserId, OwnerTakesEffectAt);
        HttpClient client = factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(client, AssertionOptionsPath, token: null);
        AssertionResult assertion = device.Authenticate(
            challenge, ApiFactory.PasskeyOrigin, PasskeyEncoding.ToUserHandle(owner.UserId));

        // Act
        HttpResponseMessage response = await SendAsync(client, CancellationPath, full, BodyOf(assertion));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await ReadJsonObjectAsync(response))["title"]?.GetValue<string>())
            .IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That(await StoredInstantsAsync(host)).IsEquivalentTo(new[] { (owner.UserId, OwnerTakesEffectAt) });
    }

    /// <summary>
    /// Alice's full session, Alice's own re-authentication nonce, and Bob's registered passkey signing it:
    /// refused 401, and neither account's schedule moves.
    /// </summary>
    /// <remarks>
    /// No user handle on the response, for the reason <c>ErasureReauthenticationTests</c> gives: with
    /// Alice's handle present, a lookup that had lost its owner filter would still be turned down by the
    /// handle check below it, and this would stay green over a gate with no account binding left. Bob holds
    /// a schedule too, a day apart, so a cancel that removed the passkey owner's row instead of the
    /// session's shows up as Bob's row missing.
    /// </remarks>
    [Test]
    public async Task Cancel_WithAnotherAccountsPasskey_Is401_AndTheScheduleStands()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner alice = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        RepositoryTestHost.SeededOwner bob = await host.SeedOwnerAsync(SurvivorSubject, SurvivorEmail);
        SyntheticAuthenticator alicesDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] aliceFull = await SeedFullSessionAsync(host, alice.UserId, alicesDevice, fill: 0x22);
        _ = await SeedFullSessionAsync(host, bob.UserId, bobsDevice, fill: 0x44);
        await SeedScheduleAsync(host, alice.UserId, OwnerTakesEffectAt);
        await SeedScheduleAsync(host, bob.UserId, SurvivorTakesEffectAt);
        HttpClient client = factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath, aliceFull);
        AssertionResult assertion = bobsDevice.Authenticate(challenge, ApiFactory.PasskeyOrigin, userHandle: null);

        // Act
        HttpResponseMessage response = await SendAsync(client, CancellationPath, aliceFull, BodyOf(assertion));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await ReadJsonObjectAsync(response))["title"]?.GetValue<string>())
            .IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That(await StoredInstantsAsync(host)).IsEquivalentTo(new[]
        {
            (alice.UserId, OwnerTakesEffectAt),
            (bob.UserId, SurvivorTakesEffectAt),
        });
    }

    /// <summary>
    /// A fresh assertion from a full session answers 204 with no body and no cookie, the row is gone, the
    /// account is otherwise untouched, and the session read then answers no erasure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The session read is the client's half of the contract.</b> <c>/release</c> and the settings
    /// screen learn whether a schedule stands from <c>GET /api/me/session</c>; a cancel that removed the
    /// row but left a cached or derived answer standing would tell the owner the account is still going.
    /// </para>
    /// <para>
    /// <b>A cancel is not an erasure</b>, so the account's tables are counted either side, and the session
    /// that asked is still live afterwards — the read is made on it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Cancel_WithAFreshAssertion_Answers204_RemovesTheRow_AndTheSessionReadAnswersNoErasure()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, device, fill: 0x22);
        await SeedScheduleAsync(host, owner.UserId, OwnerTakesEffectAt);
        HttpClient client = factory.CreateClient();
        HttpResponseMessage readBefore = await SendAsync(client, SessionPath, full, body: null, HttpMethod.Get);
        await Assert.That((await ReadJsonObjectAsync(readBefore))["erasure"]).IsNotNull();
        IReadOnlyDictionary<string, long> before = await CountAccountRowsAsync(host);
        AssertionResult assertion = await ReauthenticateAsync(client, full, device, owner.UserId);

        // Act
        HttpResponseMessage response = await SendAsync(client, CancellationPath, full, BodyOf(assertion));

        // Assert — the answer.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(string.Empty);
        await Assert.That(response.Headers.Contains("Set-Cookie")).IsFalse();

        // The row is gone, and nothing else of the account moved.
        await Assert.That(await StoredInstantsAsync(host)).IsEmpty();
        await Assert.That(Render(await CountAccountRowsAsync(host))).IsEqualTo(Render(before));

        // The session that asked still reads, and reads no erasure.
        HttpResponseMessage readAfter = await SendAsync(client, SessionPath, full, body: null, HttpMethod.Get);
        await Assert.That(readAfter.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject body = await ReadJsonObjectAsync(readAfter);
        await Assert.That(body.ContainsKey("erasure")).IsTrue();
        await Assert.That(body["erasure"]).IsNull();
    }

    /// <summary>
    /// With nothing scheduled the route still answers 204, and the nonce is spent: its row is gone and the
    /// same assertion presented again is refused.
    /// </summary>
    /// <remarks>
    /// <b>The gate runs whether or not a schedule stands.</b> A handler that read the schedule first and
    /// returned early on none would answer the same 204 and leave the nonce live. Gate first, because a
    /// 204 then only ever goes to a caller who proved a passkey, so the answer to a bad proof never depends
    /// on what the database holds; and spent either way, because a nonce left live sits in the shared
    /// re-authentication pool, spendable at <c>POST /api/me/erasure</c> for its five minutes — hygiene,
    /// not a threat boundary.
    /// </remarks>
    [Test]
    public async Task Cancel_WhenNothingIsScheduled_Answers204_AndSpendsTheNonce()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, device, fill: 0x22);
        HttpClient client = factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath, full);
        AssertionResult assertion = device.Authenticate(
            challenge, ApiFactory.PasskeyOrigin, PasskeyEncoding.ToUserHandle(owner.UserId));
        await Assert.That(await CountChallengeRowsAsync(host, challenge)).IsEqualTo(1L);

        // Act
        HttpResponseMessage response = await SendAsync(client, CancellationPath, full, BodyOf(assertion));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await CountChallengeRowsAsync(host, challenge)).IsEqualTo(0L);
        await Assert.That(await StoredInstantsAsync(host)).IsEmpty();

        HttpResponseMessage replay = await SendAsync(client, CancellationPath, full, BodyOf(assertion));
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Cancelling one account's schedule removes that row and leaves another account's exactly as it was.
    /// </summary>
    /// <remarks>
    /// The counterweight, in the role <c>ErasureScheduleEndpointTests.ScheduleErasure_LeavesAnotherAccountUntouched</c>
    /// plays for the schedule: a cancel that emptied the table satisfies every assertion about the account
    /// that asked. The survivor's instant is a day off the owner's and carries its own microseconds.
    /// </remarks>
    [Test]
    public async Task Cancel_LeavesAnotherAccountsScheduleUntouched()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        RepositoryTestHost.SeededOwner survivor = await host.SeedOwnerAsync(SurvivorSubject, SurvivorEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, device, fill: 0x22);
        await SeedScheduleAsync(host, owner.UserId, OwnerTakesEffectAt);
        await SeedScheduleAsync(host, survivor.UserId, SurvivorTakesEffectAt);
        HttpClient client = factory.CreateClient();
        AssertionResult assertion = await ReauthenticateAsync(client, full, device, owner.UserId);

        // Act
        HttpResponseMessage response = await SendAsync(client, CancellationPath, full, BodyOf(assertion));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await StoredInstantsAsync(host)).IsEquivalentTo(new[]
        {
            (survivor.UserId, SurvivorTakesEffectAt),
        });
    }

    /// <summary>
    /// A full session that has ended presents a valid assertion and is answered 401; the schedule stands,
    /// and the same assertion from a live full session then succeeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The route accepts no ended session — <c>AcceptsEndedSession</c> is the sign-out route's alone — so
    /// this is the fallback policy's 401, before the handler. The live arm is the control: the nonce is
    /// single use, so an ended request that had reached the gate would leave it a 401.
    /// </para>
    /// <para>
    /// <b>The 401 carries no <c>refusal</c> member.</b> The client reads <c>refusal: "assertion"</c> as a
    /// refused passkey and asks nothing more, and reads a 401 without it against one unmarked
    /// <c>GET /api/me</c> — the probe that lets the session interceptor end the session. A fallback 401
    /// wearing the word would tell somebody whose session ended that their passkey was refused, and leave
    /// them on a screen they can no longer use.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Cancel_FromAnEndedSession_Is401()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        Guid passkey = await host.SeedPasskeyAsync(owner.UserId, device.CredentialId, device.CoseKey, device.Algorithm);
        byte[] live = await host.SeedSessionAsync(
            passkey, 0x22, SessionKind.Full, RequestInstant.AddMinutes(-1), RequestInstant.AddHours(1));
        byte[] ended = await host.SeedSessionAsync(
            passkey,
            0x33,
            SessionKind.Full,
            RequestInstant.AddMinutes(-1),
            RequestInstant.AddHours(1),
            revokedAtUtc: RequestInstant.AddSeconds(-30));
        await SeedScheduleAsync(host, owner.UserId, OwnerTakesEffectAt);
        HttpClient client = factory.CreateClient();
        AssertionResult assertion = await ReauthenticateAsync(client, live, device, owner.UserId);

        // Act — the ended session first, so nothing the live one removes can be what it found missing.
        HttpResponseMessage endedResponse = await SendAsync(client, CancellationPath, ended, BodyOf(assertion));
        (Guid, DateTime)[] afterTheEndedSession = await StoredInstantsAsync(host);
        HttpResponseMessage liveResponse = await SendAsync(client, CancellationPath, live, BodyOf(assertion));

        // Assert
        await Assert.That(liveResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(endedResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(endedResponse)).IsNull();
        await Assert.That(afterTheEndedSession).IsEquivalentTo(new[] { (owner.UserId, OwnerTakesEffectAt) });
    }

    /// <summary>
    /// A request carrying a valid assertion and no session at all is answered 401; the schedule stands, and
    /// the same assertion from the live full session then succeeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion names the account's own passkey and carries its user handle, so a route that let the
    /// assertion stand in for a session — resolving the account from the credential — would answer 204
    /// here. The live arm proves the assertion was good and the nonce left unspent.
    /// </para>
    /// <para>
    /// <b>No <c>refusal</c> member</b>, for <see cref="Cancel_FromAnEndedSession_Is401" />'s reason: the
    /// client must read this 401 against its probe, never as a refused passkey.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Cancel_WithNoSession_Is401()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, device, fill: 0x22);
        await SeedScheduleAsync(host, owner.UserId, OwnerTakesEffectAt);
        HttpClient client = factory.CreateClient();
        AssertionResult assertion = await ReauthenticateAsync(client, full, device, owner.UserId);

        // Act
        HttpResponseMessage anonymous = await SendAsync(client, CancellationPath, token: null, BodyOf(assertion));
        (Guid, DateTime)[] afterTheAnonymousRequest = await StoredInstantsAsync(host);
        HttpResponseMessage signedIn = await SendAsync(client, CancellationPath, full, BodyOf(assertion));

        // Assert
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(anonymous)).IsNull();
        await Assert.That(afterTheAnonymousRequest).IsEquivalentTo(new[] { (owner.UserId, OwnerTakesEffectAt) });
    }

    /// <summary>
    /// The owner's full session, the owner's own re-authentication nonce, the owner's own registered
    /// passkey signing it — and a user handle naming another account: refused 401, and neither account's
    /// schedule moves; the same device answering a fresh nonce with the owner's own handle then succeeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The handle is the only thing wrong</b>, so this is the one request here the gate refuses at its
    /// user-handle check. It reaches that check only if the route forwards the body's <c>userHandle</c>: a
    /// route that dropped it (passed <c>null</c>) hands the gate an assertion with no handle, which the
    /// owner-scoped lookup accepts, and the cancel goes through on a response the authenticator signed for
    /// somebody else. <see cref="Cancel_WithAnotherAccountsPasskey_Is401_AndTheScheduleStands" /> omits the
    /// handle on purpose and so cannot see this.
    /// </para>
    /// <para>
    /// The second account is a real one holding its own schedule, so a cancel that followed the handle to
    /// its account shows up as that row missing. The control arm proves the device, the session and the
    /// route were otherwise good.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Cancel_WithAnotherAccountsUserHandle_Is401_AndTheScheduleStands()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        RepositoryTestHost.SeededOwner stranger = await host.SeedOwnerAsync(SurvivorSubject, SurvivorEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, device, fill: 0x22);
        await SeedScheduleAsync(host, owner.UserId, OwnerTakesEffectAt);
        await SeedScheduleAsync(host, stranger.UserId, SurvivorTakesEffectAt);
        HttpClient client = factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath, full);
        AssertionResult mismatched = device.Authenticate(
            challenge, ApiFactory.PasskeyOrigin, PasskeyEncoding.ToUserHandle(stranger.UserId));

        // Act
        HttpResponseMessage response = await SendAsync(client, CancellationPath, full, BodyOf(mismatched));
        (Guid, DateTime)[] afterTheMismatchedRequest = await StoredInstantsAsync(host);
        AssertionResult matched = await ReauthenticateAsync(client, full, device, owner.UserId);
        HttpResponseMessage control = await SendAsync(client, CancellationPath, full, BodyOf(matched));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await ReadJsonObjectAsync(response))["title"]?.GetValue<string>())
            .IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That(afterTheMismatchedRequest).IsEquivalentTo(new[]
        {
            (owner.UserId, OwnerTakesEffectAt),
            (stranger.UserId, SurvivorTakesEffectAt),
        });

        // The control: the same device, the owner's own handle, a fresh nonce.
        await Assert.That(control.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await StoredInstantsAsync(host)).IsEquivalentTo(new[]
        {
            (stranger.UserId, SurvivorTakesEffectAt),
        });
    }

    /// <summary>
    /// When the delete fails for a reason the repository does not read as a lost race, the route answers
    /// the catch-all 500 — never the 204 — and the schedule stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A 204 tells the client to hide the notice.</b> A lost grant or a dropped connection under the
    /// delete leaves the row in place; answered 204, the owner is told the erasure is withdrawn and the
    /// account is erased on the date anyway. The handler tests hold the handler's half; this holds that
    /// nothing between the handler and the response turns the failure into a success either.
    /// </para>
    /// <para>
    /// Staged by decorating the real repository: the read is the adapter's own, over the app role, and
    /// only the remove is replaced by a provider-shaped refusal raised before any statement is sent.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Cancel_WhenTheDeleteFails_Answers500_AndTheScheduleStands()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(
            host,
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                services.Replace(ServiceDescriptor.Scoped<IErasureScheduleRepository>(provider =>
                    new FailingRemoveRepository(
                        new ErasureScheduleRepository(provider.GetRequiredService<BudgetoidDbContext>()))));
            });
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, device, fill: 0x22);
        await SeedScheduleAsync(host, owner.UserId, OwnerTakesEffectAt);
        HttpClient client = factory.CreateClient();
        AssertionResult assertion = await ReauthenticateAsync(client, full, device, owner.UserId);

        // Act
        HttpResponseMessage response = await SendAsync(client, CancellationPath, full, BodyOf(assertion));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await ReadJsonObjectAsync(response))["title"]?.GetValue<string>())
            .IsEqualTo(CatchAllTitle);
        await Assert.That(await StoredInstantsAsync(host)).IsEquivalentTo(new[] { (owner.UserId, OwnerTakesEffectAt) });
    }

    /// <summary>
    /// The title <c>GlobalExceptionHandler</c> writes for anything no earlier handler claims, held as a
    /// literal there and so here, as <c>DataExportRefusalTests</c> holds it.
    /// </summary>
    private const string CatchAllTitle = "An unexpected error occurred.";

    /// <summary>
    /// The real repository with its remove replaced by a provider's refusal, as a lost grant or a dropped
    /// connection raises one.
    /// </summary>
    private sealed class FailingRemoveRepository(IErasureScheduleRepository inner) : IErasureScheduleRepository
    {
        public Task<ErasureSchedule?> FindAsync(Guid userId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(userId, cancellationToken);

        public Task<ErasureSchedule?> FindTrackedAsync(Guid userId, CancellationToken cancellationToken = default) =>
            inner.FindTrackedAsync(userId, cancellationToken);

        public Task<ErasureSchedule> AddAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default) =>
            inner.AddAsync(schedule, cancellationToken);

        public Task<ScheduleRemoval> RemoveAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default) =>
            Task.FromException<ScheduleRemoval>(new RefusedStatementException());
    }

    private sealed class RefusedStatementException() : DbException("The statement was refused.");

    private const string OwnerSubject = "google-erasure-cancel-owner";
    private const string OwnerEmail = "erasure-cancel-owner@budgetoid.test";
    private const string SurvivorSubject = "google-erasure-cancel-survivor";
    private const string SurvivorEmail = "erasure-cancel-survivor@budgetoid.test";
    private const string TraceIdMember = "traceId";
    private const string RefusalMember = "refusal";

    /// <summary>The instant every request here is served at, in whole seconds.</summary>
    private static readonly DateTime RequestInstant = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// The owner's filed instant: five days out, with six fractional digits, so a row rewritten at a
    /// coarser precision — or recomputed from the clock — does not read back equal.
    /// </summary>
    private static readonly DateTime OwnerTakesEffectAt = RequestInstant.AddDays(5).AddTicks(1_234_560);

    /// <summary>A second account's filed instant, a day off the owner's, with digits of its own.</summary>
    private static readonly DateTime SurvivorTakesEffectAt = RequestInstant.AddDays(6).AddTicks(6_543_210);

    /// <summary>The tables a cancel must leave exactly as it found them.</summary>
    private static readonly string[] AccountTables =
    [
        "users", "budgets", "credentials", "sessions", "session_tokens", "passkey_public_keys", "factor_manifests",
    ];

    private static ApiFactory CreateFactory(RepositoryTestHost host, FakeTimeProvider clock) =>
        SessionCookieAuthenticationTests.CreateApiFactory(
            host,
            services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));

    /// <summary>
    /// Sends one request carrying the first-party client header, optionally the session cookie, and
    /// optionally a JSON body.
    /// </summary>
    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string path,
        byte[]? token,
        object? body,
        HttpMethod? method = null)
    {
        HttpRequestMessage request = new(method ?? HttpMethod.Post, path);
        request.Headers.Add(FirstPartyRequestTests.ClientHeader, FirstPartyRequestTests.ClientHeaderValue);
        if (token is not null)
        {
            request.Headers.Add(
                "Cookie",
                $"{SessionCookieAuthenticationTests.CookieName}={Base64UrlText.Encode(token)}");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request);
    }

    /// <summary>The five members the immediate erasure's request takes, in its wire shape.</summary>
    private static object BodyOf(AssertionResult assertion) => new
    {
        credentialId = assertion.CredentialIdBase64Url,
        clientDataJson = assertion.ClientDataJsonBase64Url,
        authenticatorData = assertion.AuthenticatorDataBase64Url,
        signature = assertion.SignatureBase64Url,
        userHandle = assertion.UserHandleBase64Url,
    };

    /// <summary>Runs an options leg over <paramref name="token" />'s session, or none, and returns the challenge.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path, byte[]? token)
    {
        HttpResponseMessage response = await SendAsync(client, path, token, body: null);
        response.EnsureSuccessStatusCode();
        JsonObject options = await ReadJsonObjectAsync(response);
        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// Mints a re-authentication nonce on <paramref name="token" />'s session and has
    /// <paramref name="device" /> sign it, carrying the account's own user handle.
    /// </summary>
    private static async Task<AssertionResult> ReauthenticateAsync(
        HttpClient client,
        byte[] token,
        SyntheticAuthenticator device,
        Guid userId)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath, token);
        return device.Authenticate(challenge, ApiFactory.PasskeyOrigin, PasskeyEncoding.ToUserHandle(userId));
    }

    /// <summary>
    /// A live full session opened by a passkey whose public key is <paramref name="device" />'s, its window
    /// placed around <see cref="RequestInstant" />.
    /// </summary>
    private static async Task<byte[]> SeedFullSessionAsync(
        RepositoryTestHost host,
        Guid userId,
        SyntheticAuthenticator device,
        byte fill)
    {
        Guid credentialId = await host.SeedPasskeyAsync(userId, device.CredentialId, device.CoseKey, device.Algorithm);
        return await host.SeedSessionAsync(
            credentialId, fill, SessionKind.Full, RequestInstant.AddMinutes(-1), RequestInstant.AddHours(1));
    }

    /// <summary>A live session opened by the account's federated credential.</summary>
    private static async Task<byte[]> SeedLockedSessionAsync(RepositoryTestHost host, Guid userId, byte fill) =>
        await host.SeedSessionAsync(
            await host.FederatedCredentialIdAsync(userId),
            fill,
            SessionKind.Locked,
            RequestInstant.AddMinutes(-1),
            RequestInstant.AddHours(1));

    /// <summary>A schedule an earlier request already filed, written on the superuser connection.</summary>
    private static async Task SeedScheduleAsync(RepositoryTestHost host, Guid userId, DateTime takesEffectAtUtc)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "insert into erasure_schedules (user_id, takes_effect_at_utc) values (@user_id, @takes_effect_at_utc)",
            connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("takes_effect_at_utc", takesEffectAtUtc);

        if (await command.ExecuteNonQueryAsync() is not 1)
        {
            throw new InvalidOperationException("Seeding a schedule wrote something other than one row.");
        }
    }

    /// <summary>Every schedule in the table, as (account, instant), read on the superuser connection.</summary>
    private static async Task<(Guid UserId, DateTime TakesEffectAtUtc)[]> StoredInstantsAsync(RepositoryTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select user_id, takes_effect_at_utc from erasure_schedules order by user_id", connection);

        List<(Guid, DateTime)> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetFieldValue<DateTime>(1)));
        }

        return [.. rows];
    }

    /// <summary>How many challenge rows hold exactly <paramref name="challenge" />.</summary>
    private static async Task<long> CountChallengeRowsAsync(RepositoryTestHost host, byte[] challenge)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select count(*) from webauthn_challenges where challenge = @challenge", connection);
        command.Parameters.AddWithValue("challenge", challenge);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Whole-table row counts of <see cref="AccountTables" />.</summary>
    private static async Task<IReadOnlyDictionary<string, long>> CountAccountRowsAsync(RepositoryTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach (string table in AccountTables)
        {
            await using NpgsqlCommand command = new($"select count(*) from {table}", connection);
            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }

        return counts;
    }

    /// <summary>Counts rendered as one line, so a failure names the table that moved.</summary>
    private static string Render(IReadOnlyDictionary<string, long> counts) =>
        string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

    /// <summary>
    /// The body's <c>refusal</c> member as raw JSON, or <see langword="null" /> when the body carries none —
    /// including an empty body, or one that is not a JSON object, neither of which the client can read a
    /// member off. Returned rather than tested, so a failure names the value that leaked.
    /// </summary>
    private static async Task<string?> RefusalOfAsync(HttpResponseMessage response)
    {
        string raw = await response.Content.ReadAsStringAsync();
        if (raw.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw) is JsonObject body && body.ContainsKey(RefusalMember)
                ? body[RefusalMember]?.ToJsonString() ?? "null"
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject
        ?? throw new InvalidOperationException(
            $"The {(int)response.StatusCode} response body is not a JSON object.");

    /// <summary>
    /// The whole response body with the per-request <c>traceId</c> replaced, in the shape
    /// <see cref="LockedSessionTests" /> compares refusals by. A body that is not JSON is returned verbatim.
    /// </summary>
    private static async Task<string> ComparableBodyOfAsync(HttpResponseMessage response)
    {
        string raw = await response.Content.ReadAsStringAsync();
        if (raw.Length == 0 || JsonNode.Parse(raw) is not JsonObject body)
        {
            return raw;
        }

        if (body.ContainsKey(TraceIdMember))
        {
            body[TraceIdMember] = "<one per request>";
        }

        return body.ToJsonString();
    }
}
