using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Domain.Sessions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// <c>POST /api/locked-session</c>: a provider token, and nothing else, turned into a locked session over
/// the account's federated credential — or into a 404 that writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The real <c>JwtBearer</c> handler holding a test signing key, on every test.</b> This route's whole
/// proof is the provider's signature, so a header handler standing in for it would measure the fixture;
/// it is the shape <c>EmailChangeEndpointTests</c> builds for its bearer-shaped cases.
/// </para>
/// <para>
/// <b>Every account here also holds a passkey</b>, because <see cref="ApiFactory.CreateSignedInClientAsync" />
/// seeds one beside the federated credential and opens its full session over it. A route that opened
/// the session over "the account's credential" rather than the one the token named has two to choose
/// from, and the passkey would open a <see cref="SessionKind.Full" /> session — the whole of what FR-109
/// refuses.
/// </para>
/// <para>
/// <b>The app role, not the admin.</b> The host connects as <c>budgetoid_app</c>, so the discovery read
/// and the session insert running with the account published are both judged by the real policies. The
/// handler publishes nobody before its discovery read; on a request carrying a live cookie, the cookie's
/// authentication has already published its own account by then. A handler that published late answers
/// 500 here with <c>22P02</c>; one that published nobody answers the same.
/// </para>
/// </remarks>
public sealed class LockedSignInEndpointTests
{
    private const string LockedSessionPath = "/api/locked-session";
    private const string AccountsPath = "/api/accounts";
    private const string SessionPath = "/api/me/session";
    private const string RevocationPath = "/api/me/session/revocation";

    private const string Subject = "google-locked-sign-in";
    private const string Email = "locked-sign-in@example.com";
    private const string StrangerSubject = "google-locked-sign-in-stranger";
    private const string StrangerEmail = "locked-sign-in-stranger@example.com";
    private const string UnregisteredSubject = "google-locked-sign-in-nobody";
    private const string UnregisteredEmail = "locked-sign-in-nobody@example.com";

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    [Test]
    public async Task LockedSignIn_ForARegisteredSubject_EstablishesALockedSession_ThatIsRefusedBudgetContent()
    {
        // Arrange — the stranger first, so the account signed into is not simply the first row.
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        _ = await factory.CreateSignedInClientAsync(StrangerSubject, StrangerEmail);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        Guid federatedId = await RepositoryTestHost.FederatedCredentialIdOnAsync(host.ConnectionString, account.UserId);
        DateTime before = DateTime.UtcNow;

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            factory.CreateClient(), ProviderToken(signingKey, Claims(Subject, Email)));
        DateTime after = DateTime.UtcNow;

        // Assert — the answer.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject body = await ReadJsonObjectAsync(response);

        // Exactly three members: no session, account or budget id, no address, no subject.
        await Assert.That(string.Join(", ", body.Select(member => member.Key).Order(StringComparer.Ordinal)))
            .IsEqualTo("erasure, expiresAtUtc, kind");
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("locked");
        await Assert.That(body.ContainsKey("erasure")).IsTrue();
        await Assert.That(body["erasure"]).IsNull();
        DateTime expiresAtUtc = ParseUtc(body["expiresAtUtc"]!.GetValue<string>());
        await Assert.That(expiresAtUtc).IsGreaterThanOrEqualTo(before + SessionLifetime - TimeSpan.FromSeconds(1));
        await Assert.That(expiresAtUtc).IsLessThanOrEqualTo(after + SessionLifetime + TimeSpan.FromSeconds(1));

        // The cookie, and a body that does not repeat it.
        string cookie = RegistrationCeremony.SessionCookieValueOf(response);
        await Assert.That((await response.Content.ReadAsStringAsync()).Contains(cookie, StringComparison.Ordinal))
            .IsFalse();

        // The stored row: locked, over the federated credential and not the passkey, on this account.
        StoredSession stored = await StoredSessionOfAsync(host, cookie);
        await Assert.That(stored.UserId).IsEqualTo(account.UserId);
        await Assert.That(stored.CredentialId).IsEqualTo(federatedId);
        await Assert.That(stored.Kind).IsEqualTo("locked");

        // Equal, not merely close: the handler cuts its clock read to the microsecond a timestamptz keeps,
        // so the answer and the stored row are one instant. In ticks, so a failure names the digit.
        await Assert.That(expiresAtUtc.Ticks).IsEqualTo(stored.ExpiresAtUtc.Ticks);

        // What the session reaches: no budget content, its own kind, and the way out.
        HttpClient locked = CookieClient(factory, cookie);
        HttpResponseMessage accounts = await locked.GetAsync(AccountsPath);
        HttpResponseMessage session = await locked.GetAsync(SessionPath);
        string sessionKind = (await ReadJsonObjectAsync(session))["kind"]!.GetValue<string>();
        HttpResponseMessage revocation = await locked.PostAsync(RevocationPath, content: null);

        await Assert.That(accounts.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(session.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sessionKind).IsEqualTo("locked");
        await Assert.That(revocation.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// With a schedule filed, the sign-in answers its instant — the account's, never a stranger's.
    /// </summary>
    /// <remarks>
    /// The stranger's schedule is filed too and a day apart, so an answer read from the wrong account's
    /// row, or from "the" row, is visibly wrong. Seeded on the admin connection rather than through
    /// <c>POST /api/me/erasure/schedule</c>, so this test's subject is the sign-in's read alone.
    /// </remarks>
    [Test]
    public async Task LockedSignIn_ForARegisteredSubjectWithASchedule_AnswersItsInstant()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient stranger = await factory.CreateSignedInClientAsync(StrangerSubject, StrangerEmail);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        DateTime own = new(2026, 11, 3, 9, 10, 11, DateTimeKind.Utc);
        await SeedScheduleAsync(host, stranger.UserId, own.AddDays(1));
        await SeedScheduleAsync(host, account.UserId, own);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            factory.CreateClient(), ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("locked");
        await Assert.That(ParseUtc(body["erasure"]!["takesEffectAtUtc"]!.GetValue<string>())).IsEqualTo(own);
    }

    /// <summary>
    /// A locked sign-in on an account holding a schedule leaves the row exactly as it was — the instant to
    /// the microsecond, and one row.
    /// </summary>
    /// <remarks>
    /// <b>The sign-in reads the schedule and must never touch it.</b> A locked session is what somebody
    /// holding the owner's provider account reaches, and only a full session with a passkey may withdraw a
    /// schedule; a sign-in that removed or re-filed the row on its way past — "a fresh sign-in resets the
    /// date" — would hand that person the cancellation the passkey gate exists to refuse, or slide the
    /// owner's window. The instant carries six fractional digits so a rewrite at another precision reads
    /// back as a different value, and the whole table is compared so a second row cannot hide.
    /// </remarks>
    [Test]
    public async Task LockedSignIn_OnAnAccountHoldingASchedule_LeavesItStanding()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        DateTime filed = new DateTime(2026, 11, 3, 9, 10, 11, DateTimeKind.Utc).AddTicks(1_234_560);
        await SeedScheduleAsync(host, account.UserId, filed);
        string before = await RenderSchedulesAsync(host);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            factory.CreateClient(), ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert — the sign-in succeeded, so the comparison below is about a request that ran.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(before).IsEqualTo($"{account.UserId}={filed.Ticks}");
        await Assert.That(await RenderSchedulesAsync(host)).IsEqualTo(before);
    }

    /// <summary>
    /// A browser holding one account's locked cookie and presenting another account's provider token
    /// opens everything on the token's account and nothing on the cookie's.
    /// </summary>
    /// <remarks>
    /// The riskiest of the cases over an existing session, because a locked cookie is let through to the
    /// handler on purpose. A route that took the account from the cookie's session — or from whatever
    /// principal the request carried — would file the new row, or answer the schedule, for the wrong
    /// person. Both accounts have a schedule, a day apart, so an answer read from the cookie's account is
    /// visibly wrong; the stored row is checked for the token's account over the token's own federated
    /// credential, never the cookie account's.
    /// </remarks>
    [Test]
    public async Task LockedSignIn_WithAnotherAccountsLockedCookie_OpensEverythingOnTheTokensAccount()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient cookieAccount =
            await factory.CreateSignedInClientAsync(StrangerSubject, StrangerEmail, SessionKind.Locked);
        ApiFactory.SignedInClient tokenAccount = await factory.CreateSignedInClientAsync(Subject, Email);
        Guid tokenFederatedId =
            await RepositoryTestHost.FederatedCredentialIdOnAsync(host.ConnectionString, tokenAccount.UserId);
        DateTime own = new(2026, 11, 3, 9, 10, 11, DateTimeKind.Utc);
        await SeedScheduleAsync(host, cookieAccount.UserId, own.AddDays(1));
        await SeedScheduleAsync(host, tokenAccount.UserId, own);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            cookieAccount.Client, ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert — the answer is the token account's schedule.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("locked");
        await Assert.That(ParseUtc(body["erasure"]!["takesEffectAtUtc"]!.GetValue<string>())).IsEqualTo(own);

        // The row is the token account's, over its own federated credential.
        string cookie = RegistrationCeremony.SessionCookieValueOf(response);
        StoredSession stored = await StoredSessionOfAsync(host, cookie);
        await Assert.That(stored.UserId).IsEqualTo(tokenAccount.UserId);
        await Assert.That(stored.CredentialId).IsEqualTo(tokenFederatedId);
        await Assert.That(stored.Kind).IsEqualTo("locked");
    }

    /// <summary>
    /// A locked sign-in over a live full session is a 409 naming <c>full_session</c>, and nothing moves:
    /// no row, no cookie, and the full cookie still opens what it opened.
    /// </summary>
    /// <remarks>
    /// Two cases: the token names the session's own account, and it names another. Replacing the cookie
    /// in the first downgrades somebody who is already signed in with a passkey; in the second it swaps
    /// the browser to a stranger's account. The body must not repeat the subject or the address the token
    /// carried. Counted on every table the route could write, and the full cookie is read back through
    /// <c>GET /api/me/session</c> so a route that revoked it and answered 409 anyway is red.
    /// </remarks>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LockedSignIn_OverALiveFullSession_IsRefused409FullSession_AndWritesNothing(
        bool tokenNamesAnotherAccount)
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        _ = await factory.CreateSignedInClientAsync(StrangerSubject, StrangerEmail);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        (string tokenSubject, string tokenEmail) = tokenNamesAnotherAccount
            ? (StrangerSubject, StrangerEmail)
            : (Subject, Email);
        RowCounts before = await RowCountsAsync(host);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            account.Client, ProviderToken(signingKey, Claims(tokenSubject, tokenEmail)));

        // Assert — the refusal. The token is transcribed, never read from ConflictKindSpelling.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await MemberOfAsync(response, "conflictKind")).IsEqualTo("full_session");

        string raw = await response.Content.ReadAsStringAsync();
        await Assert.That(raw.Contains(tokenSubject, StringComparison.Ordinal)).IsFalse();
        await Assert.That(raw.Contains(tokenEmail, StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(SetsAnyCookie(response)).IsFalse();
        await Assert.That(await RowCountsAsync(host)).IsEqualTo(before);

        // The full cookie still answers as itself.
        HttpResponseMessage session = await account.Client.GetAsync(SessionPath);
        await Assert.That(session.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadJsonObjectAsync(session))["kind"]!.GetValue<string>()).IsEqualTo("full");
    }

    /// <summary>
    /// A locked sign-in over a live locked session replaces it with a new one.
    /// </summary>
    /// <remarks>
    /// The other side of the 409 above: only a full session is refused. A gate that refused any live
    /// session would send somebody whose locked cookie is still good back with nowhere to go.
    /// </remarks>
    [Test]
    public async Task LockedSignIn_OverALiveLockedSession_ReplacesIt()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email, SessionKind.Locked);
        string oldCookie = CookieValueOf(account.Client);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            account.Client, ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string newCookie = RegistrationCeremony.SessionCookieValueOf(response);
        await Assert.That(newCookie).IsNotEqualTo(oldCookie);

        StoredSession stored = await StoredSessionOfAsync(host, newCookie);
        await Assert.That(stored.UserId).IsEqualTo(account.UserId);
        await Assert.That(stored.Kind).IsEqualTo("locked");
    }

    /// <summary>
    /// A full cookie whose session has ended is no session at all, so the sign-in succeeds over it.
    /// </summary>
    /// <remarks>
    /// The session is ended through the real revocation route. A gate that judged the cookie's stored
    /// kind without asking whether the session is still live would refuse this browser 409 forever —
    /// the one person this route exists for, holding a cookie that opens nothing.
    /// </remarks>
    [Test]
    public async Task LockedSignIn_OverAnEndedSession_Succeeds()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        HttpResponseMessage revocation = await account.Client.PostAsync(RevocationPath, content: null);
        await Assert.That(revocation.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            account.Client, ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        StoredSession stored = await StoredSessionOfAsync(host, RegistrationCeremony.SessionCookieValueOf(response));
        await Assert.That(stored.UserId).IsEqualTo(account.UserId);
        await Assert.That(stored.Kind).IsEqualTo("locked");
    }

    /// <summary>
    /// A locked sign-in over the account's own ended session deletes that session and its handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The path where the cookie's own session is the one swept.</b> This route reads the presented
    /// cookie — that is how it tells a full session from an ended one — and never clears the change
    /// tracker before it establishes. So the handle it looked up is still in the context when the sweep
    /// removes the ended session. Tracked, EF deletes that handle itself, on a table the role holds no
    /// <c>DELETE</c> on, and the sign-in answers 500 with <c>42501</c>. Untracked, the database cascade
    /// takes it. <c>SessionRepositoryTests.FindByTokenHashAsync_ReturnsAnUntrackedEntity</c> pins the
    /// cause; this pins what a person sees.
    /// </para>
    /// <para>
    /// Measured: with <c>AsNoTracking</c> taken out of that lookup, this sign-in answered 500 with
    /// <c>42501</c> on <c>session_tokens</c>.
    /// </para>
    /// <para>
    /// <b>The counts here do not hold the sweep.</b> The request carries the ended cookie, so displacement
    /// deletes the same row even with no sweep. What holds the locked path's sweep is
    /// <see cref="SignedOutSession_StaysObservableUntilTheNextSignIn" />, which signs in on a fresh client
    /// carrying no cookie.
    /// </para>
    /// </remarks>
    [Test]
    public async Task LockedSignIn_OverItsOwnAccountsEndedSession_DeletesThatSessionAndItsHandle()
    {
        // Arrange — a full session, ended through the real sign-out route.
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        string endedCookie = CookieValueOf(account.Client);
        Guid endedSessionId = await SessionIdOfAsync(host, endedCookie);
        HttpResponseMessage revocation = await account.Client.PostAsync(RevocationPath, content: null);
        await Assert.That(revocation.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Act — on the same client, so the ended session's cookie rides along.
        HttpResponseMessage response = await PostLockedSessionAsync(
            account.Client, ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await CountSessionsAsync(host, endedSessionId)).IsEqualTo(0L);
        await Assert.That(await CountHandlesAsync(host, endedCookie)).IsEqualTo(0L);
    }

    /// <summary>
    /// A signed-out session stays on record, and its cookie still reads as ended, until the account's
    /// next sign-in deletes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Revocation is still observable — until the next sign-in, and no longer.</b> Signing out stamps
    /// the row rather than deleting it, so a second sign-out with the same cookie is the 204 an ended
    /// session gets. The next sign-in anywhere on the account deletes the row and its handle, and from
    /// then on the old cookie names nothing: the sign-out route answers it 401, as it does after an
    /// erasure.
    /// </para>
    /// <para>
    /// <b>The sign-in happens on another client</b>, carrying no cookie, so nothing but the sweep can
    /// reach the old session. A sign-in that ran no sweep leaves the row, and the last sign-out answers
    /// 204 instead of 401 [reasoned]. One that deleted at sign-out instead fails the first half: the
    /// second sign-out answers 401 [reasoned].
    /// </para>
    /// </remarks>
    [Test]
    public async Task SignedOutSession_StaysObservableUntilTheNextSignIn()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        string oldCookie = CookieValueOf(account.Client);
        Guid oldSessionId = await SessionIdOfAsync(host, oldCookie);

        // Act — sign out, and sign out again with the same cookie.
        HttpResponseMessage signOut = await account.Client.PostAsync(RevocationPath, content: null);
        HttpResponseMessage secondSignOut = await account.Client.PostAsync(RevocationPath, content: null);

        // Assert — the row is still there, revoked, and the cookie still reads as an ended session.
        await Assert.That(signOut.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(secondSignOut.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await CountSessionsAsync(host, oldSessionId)).IsEqualTo(1L);

        // Act — the next sign-in, on another client.
        HttpResponseMessage signIn = await PostLockedSessionAsync(
            factory.CreateClient(), ProviderToken(signingKey, Claims(Subject, Email)));
        HttpResponseMessage lateSignOut = await account.Client.PostAsync(RevocationPath, content: null);

        // Assert — the old session is gone, so its cookie names nothing at all.
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(lateSignOut.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await CountSessionsAsync(host, oldSessionId)).IsEqualTo(0L);
    }

    /// <summary>
    /// A locked sign-in from a browser holding another account's live locked cookie deletes the session
    /// that cookie named, and its handle, and leaves that account's other sessions alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cross-account case is the one the sweep cannot reach.</b> The new session's save runs as
    /// the token's account, and <c>user_isolation</c> hides the cookie account's rows from it, so a
    /// delete there matches nothing. Without displacement, the overwritten cookie's session stays live
    /// for its whole lifetime with no browser holding it — a record that this browser was signed in to
    /// that account.
    /// </para>
    /// <para>
    /// <b>The cookie account's other device is the control.</b> A displacement that deleted every
    /// session of the cookie's account passes the first half and signs a stranger out of their phone
    /// [reasoned].
    /// </para>
    /// <para>
    /// <b>What reddens it, measured.</b> Deleting in the request scope, with the session id the cookie
    /// scheme put on the request, fails here on the cookie's session count. The cause is reasoned, not
    /// measured: by then that scope runs as the token's account, so the policy hides the row. The
    /// same-account case,
    /// <see cref="LockedSignIn_OverALiveLockedSession_DeletesTheReplacedSession" />, stays green under it,
    /// which is why this case exists beside it.
    /// </para>
    /// <para>
    /// <b>What it does not catch.</b> Resolving the displacement handler from the request's services
    /// instead of a scope of its own is a different mutation: the handler re-authenticates the cookie and
    /// publishes its account before deleting, so the row is found and goes. Measured: this test stays
    /// green under it. <c>SessionCookieWriterTests</c> catches it, on the request's identity.
    /// </para>
    /// </remarks>
    [Test]
    public async Task LockedSignIn_WithAnotherAccountsLockedCookie_DeletesThatSessionAndItsHandle()
    {
        // Arrange — the cookie account signed in twice, once in this browser and once elsewhere.
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient cookieAccount =
            await factory.CreateSignedInClientAsync(StrangerSubject, StrangerEmail, SessionKind.Locked);
        string presentedCookie = CookieValueOf(cookieAccount.Client);
        Guid presentedSessionId = await SessionIdOfAsync(host, presentedCookie);
        Guid otherDeviceSessionId = await SeedAnotherDevicesLockedSessionAsync(host, cookieAccount.UserId);
        ApiFactory.SignedInClient tokenAccount = await factory.CreateSignedInClientAsync(Subject, Email);

        // Act — the token account's sign-in, from the browser holding the cookie account's session.
        HttpResponseMessage response = await PostLockedSessionAsync(
            cookieAccount.Client, ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert — the token's account is signed in.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        StoredSession stored = await StoredSessionOfAsync(host, RegistrationCeremony.SessionCookieValueOf(response));
        await Assert.That(stored.UserId).IsEqualTo(tokenAccount.UserId);

        // The session the cookie named is gone, with its handle; the cookie account's other device stays.
        await Assert.That(await CountSessionsAsync(host, presentedSessionId)).IsEqualTo(0L);
        await Assert.That(await CountHandlesAsync(host, presentedCookie)).IsEqualTo(0L);
        await Assert.That(await CountSessionsAsync(host, otherDeviceSessionId)).IsEqualTo(1L);
    }

    /// <summary>
    /// A locked sign-in over the account's own live locked session deletes the session it replaced, and
    /// keeps the account's session on another device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sweep takes ended rows only, and this one is live.</b> The cookie is overwritten, so nothing
    /// will ever present it again, yet the row would stay live until its expiry. Displacement deletes it
    /// directly rather than revoking it, because a revoked row would sit until the account's next
    /// sign-in.
    /// </para>
    /// <para>
    /// The other device is the control: a displacement keyed on the account rather than on the cookie's
    /// session would take it too [reasoned].
    /// </para>
    /// <para>
    /// <b>Same-account only, so it cannot tell where the delete runs.</b> Deleting in the request scope
    /// with the cookie scheme's session id passes here, measured; it is
    /// <see cref="LockedSignIn_WithAnotherAccountsLockedCookie_DeletesThatSessionAndItsHandle" /> that
    /// catches it. What this holds is the account's own case: a sign-in that left the replaced session
    /// live fails on its count.
    /// </para>
    /// </remarks>
    [Test]
    public async Task LockedSignIn_OverALiveLockedSession_DeletesTheReplacedSession()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email, SessionKind.Locked);
        string replacedCookie = CookieValueOf(account.Client);
        Guid replacedSessionId = await SessionIdOfAsync(host, replacedCookie);
        Guid otherDeviceSessionId = await SeedAnotherDevicesLockedSessionAsync(host, account.UserId);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            account.Client, ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string newCookie = RegistrationCeremony.SessionCookieValueOf(response);
        await Assert.That((await StoredSessionOfAsync(host, newCookie)).UserId).IsEqualTo(account.UserId);

        await Assert.That(await CountSessionsAsync(host, replacedSessionId)).IsEqualTo(0L);
        await Assert.That(await CountHandlesAsync(host, replacedCookie)).IsEqualTo(0L);
        await Assert.That(await CountSessionsAsync(host, otherDeviceSessionId)).IsEqualTo(1L);
    }

    /// <summary>
    /// A locked sign-in from a browser whose cookie names no session at all establishes, sets the cookie,
    /// and the new cookie opens a live session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ordinary case after any displacement.</b> A browser that lost an establishing response, or
    /// whose session was removed elsewhere, still presents a handle whose row is gone. Displacement
    /// authenticates that handle, finds nothing, and has nothing to delete.
    /// </para>
    /// <para>
    /// <b>The mutation it exists to catch:</b> removing <c>DisplaceSessionHandler</c>'s
    /// <c>if (presented is null) return false;</c>. That throws a null reference after the new session
    /// committed, so every sign-in from a browser holding a dead cookie answers 500. Measured: with the
    /// guard removed this test failed with "Expected to be equal to OK but received
    /// InternalServerError", from a <c>NullReferenceException</c> in the handler.
    /// </para>
    /// </remarks>
    [Test]
    public async Task LockedSignIn_WithACookieNamingNoSession_EstablishesALiveSession()
    {
        // Arrange — a well-formed handle no row was ever filed for.
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        HttpClient browser = CookieClient(
            factory, Base64UrlText.Encode(RandomNumberGenerator.GetBytes(SessionToken.TokenLength)));

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            browser, ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert — established, with a cookie, and the cookie opens a live session on the account.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string newCookie = RegistrationCeremony.SessionCookieValueOf(response);
        await Assert.That((await StoredSessionOfAsync(host, newCookie)).UserId).IsEqualTo(account.UserId);
        HttpResponseMessage session = await CookieClient(factory, newCookie).GetAsync(SessionPath);
        await Assert.That(session.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// A locked sign-in presenting a non-canonical copy of a live full session's handle — the handle
    /// with one byte appended — establishes, and leaves that full session live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The session scheme refuses the cookie</b>, because a handle is exactly
    /// <see cref="SessionToken.TokenLength" /> bytes and this one is one longer. So the request carries no
    /// session, the 409 for a live full session does not fire, and the sign-in establishes.
    /// </para>
    /// <para>
    /// <b>The mutation it exists to catch:</b> a cookie writer with a lenient decoder of its own that
    /// truncates to 32 bytes. It would find the full session's row through the appended copy and delete
    /// it — the very session the 409 rule exists to protect — while the scheme, reading strictly, never
    /// saw it. Measured: with that decoder in the writer, the 200 held and the full session's row count
    /// came back 0 instead of 1.
    /// </para>
    /// </remarks>
    [Test]
    public async Task LockedSignIn_WithANonCanonicalCopyOfALiveFullCookie_LeavesThatSessionLive()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        string fullCookie = CookieValueOf(account.Client);
        Guid fullSessionId = await SessionIdOfAsync(host, fullCookie);
        byte[] appended = [.. Base64UrlText.Decode(fullCookie), 0x00];
        HttpClient browser = CookieClient(factory, Base64UrlText.Encode(appended));

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            browser, ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert — the sign-in went through, and the full session still answers as itself.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await CountSessionsAsync(host, fullSessionId)).IsEqualTo(1L);
        HttpResponseMessage session = await account.Client.GetAsync(SessionPath);
        await Assert.That(session.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadJsonObjectAsync(session))["kind"]!.GetValue<string>()).IsEqualTo("full");
    }

    /// <summary>
    /// Over a live full session, a token whose address the provider does not vouch for is the claim
    /// gate's 401, not the conflict.
    /// </summary>
    /// <remarks>
    /// The order is the point: the 409 says something true about the browser's session, and saying it to
    /// a caller the provider never vouched for is an answer nobody earned. A conflict check placed ahead
    /// of the claim gate answers 409 here.
    /// </remarks>
    [Test]
    public async Task LockedSignIn_OverAFullSession_WithAnUnverifiedEmail_Is401NotTheConflict()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        RowCounts before = await RowCountsAsync(host);
        Dictionary<string, object> claims = Claims(Subject, Email);
        claims["email_verified"] = false;

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            account.Client, ProviderToken(signingKey, claims));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await MemberOfAsync(response, "title")).IsEqualTo(RegistrationClaimGate.UnverifiedEmailTitle);
        await Assert.That(await MemberOfAsync(response, "conflictKind")).IsNull();
        await Assert.That(SetsAnyCookie(response)).IsFalse();
        await Assert.That(await RowCountsAsync(host)).IsEqualTo(before);
    }

    /// <summary>
    /// A subject nobody registered is a 404 naming <c>no_account</c>, and nothing is written.
    /// </summary>
    /// <remarks>
    /// Two cases. A subject and an address nobody holds; and a subject nobody holds carrying the address
    /// an existing account <em>does</em> hold — so a lookup keyed on the address, or on either, opens a
    /// locked session on the account the provider never named. Counted on every table this route could
    /// write, including <c>users</c> and <c>credentials</c>: this route creates no account.
    /// </remarks>
    [Test]
    [Arguments(UnregisteredEmail)]
    [Arguments(Email)]
    public async Task LockedSignIn_ForAnUnregisteredSubject_Answers404NoAccount_AndWritesNothing(string email)
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        _ = await factory.CreateSignedInClientAsync(Subject, Email);
        RowCounts before = await RowCountsAsync(host);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            factory.CreateClient(), ProviderToken(signingKey, Claims(UnregisteredSubject, email)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await MemberOfAsync(response, "refusal")).IsEqualTo("no_account");

        // The refusal repeats neither half of what the caller sent.
        string raw = await response.Content.ReadAsStringAsync();
        await Assert.That(raw.Contains(UnregisteredSubject, StringComparison.Ordinal)).IsFalse();
        await Assert.That(raw.Contains(email, StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(SetsAnyCookie(response)).IsFalse();
        await Assert.That(await RowCountsAsync(host)).IsEqualTo(before);
    }

    /// <summary>
    /// No provider token is a 401 however the browser is otherwise signed in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cookie is the trap: this route's policy names the provider scheme and nothing else, so a
    /// session cookie — full or locked — must not stand in for the provider. A route that fell back to the
    /// default scheme would admit both, and with the full cookie would hand a passkey holder a locked
    /// session nobody's provider vouched for.
    /// </para>
    /// <para>
    /// <b>The 401 alone does not tell those apart.</b> A policy naming the cookie's scheme beside the
    /// provider's admits the cookie principal, and the claim gate then refuses it 401 for carrying no
    /// provider email — the same status, from the wrong layer. So the challenge is asserted too: the
    /// provider scheme's own <c>Bearer</c> challenge, and no claim-gate title in the body.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(SessionKind.Full)]
    [Arguments(SessionKind.Locked)]
    public async Task LockedSignIn_WithNoProviderToken_IsRefused401_EvenWithASessionCookie(SessionKind kind)
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email, kind);
        RowCounts before = await RowCountsAsync(host);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(account.Client, token: null);

        // Assert — refused by the provider scheme's challenge, not by the claim gate.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(string.Join(", ", response.Headers.WwwAuthenticate.Select(challenge => challenge.Scheme)))
            .Contains("Bearer");
        await Assert.That((await response.Content.ReadAsStringAsync())
                .Contains(RegistrationClaimGate.MissingClaimsTitle, StringComparison.Ordinal))
            .IsFalse();
        await Assert.That(SetsAnyCookie(response)).IsFalse();
        await Assert.That(await RowCountsAsync(host)).IsEqualTo(before);
    }

    /// <summary>
    /// The cookie a locked sign-in sets is the product's one session cookie, with the attributes the
    /// <c>__Host-</c> prefix requires, and it dies with the session the body reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is a literal rather than <c>SessionCookie.Name</c>, for the reason
    /// <see cref="SessionCookieIssuanceTests" /> gives: a browser sends the bytes, not the symbol.
    /// <c>Domain</c> is asserted absent on the raw header, because a parser reports an unset attribute and
    /// one it failed to read the same way.
    /// </para>
    /// <para>
    /// The expiry is compared to the body's <c>expiresAtUtc</c>, never to an interval, with a second of
    /// tolerance because the header carries whole seconds. A cookie appended without the shared attributes,
    /// or one outliving the session it names, is red here.
    /// </para>
    /// </remarks>
    [Test]
    public async Task LockedSignIn_SetsTheHostPrefixedCookie_ExpiringWithTheSession()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        _ = await factory.CreateSignedInClientAsync(Subject, Email);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            factory.CreateClient(), ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        DateTime expiresAtUtc = ParseUtc((await ReadJsonObjectAsync(response))["expiresAtUtc"]!.GetValue<string>());

        string[] headers = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? [.. values]
            : [];
        string raw = headers.SingleOrDefault(header =>
                         header.StartsWith("__Host-budgetoid-session=", StringComparison.Ordinal))
                     ?? throw new InvalidOperationException(
                         "The response set no '__Host-budgetoid-session' cookie. Set-Cookie: "
                         + (headers.Length == 0 ? "<none>" : string.Join(" | ", headers)));
        Microsoft.Net.Http.Headers.SetCookieHeaderValue issued =
            Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(raw);

        await Assert.That(issued.HttpOnly).IsTrue();
        await Assert.That(issued.Secure).IsTrue();
        await Assert.That(issued.SameSite).IsEqualTo(Microsoft.Net.Http.Headers.SameSiteMode.Lax);
        await Assert.That(issued.Path.ToString()).IsEqualTo("/");
        await Assert.That(raw.Contains("domain", StringComparison.OrdinalIgnoreCase)).IsFalse();

        DateTimeOffset cookieExpiry = issued.Expires
                                      ?? throw new InvalidOperationException("The session cookie carries no expiry.");
        await Assert.That((cookieExpiry.UtcDateTime - expiresAtUtc).Duration()).IsLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Judging the browser's cookie inside the route costs no second token lookup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The route asks the cookie scheme for its result by name, after the default scheme has already
    /// authenticated the same cookie for the request. The delegate's comment says the handler's cached
    /// result answers that second ask, so the discovery read runs once. This counts it.
    /// </para>
    /// <para>
    /// <b>Decoration, not substitution.</b> The real repository still answers on the real connection, so
    /// the 409 below is the route's own verdict and the counter only watches. The count is taken as a
    /// difference around the one request, so whatever the arrangement did is not in it. Over a live full
    /// session, because that is the request on which the cookie's result decides the answer.
    /// </para>
    /// </remarks>
    [Test]
    public async Task LockedSignIn_OverALiveFullSession_LooksTheSessionTokenUpOnce()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        TokenLookupCounter lookups = new();
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(
            host, signingKey, services => CountSessionTokenLookups(services, lookups));
        ApiFactory.SignedInClient account = await factory.CreateSignedInClientAsync(Subject, Email);
        int before = lookups.Count;

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            account.Client, ProviderToken(signingKey, Claims(Subject, Email)));
        int during = lookups.Count - before;

        // Assert — the route reached its verdict on the cookie, and asked the store once to do it.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(during).IsEqualTo(1);
    }

    /// <summary>
    /// A token the bearer handler validates, whose address the provider does not vouch for, is refused
    /// with registration's own sentence.
    /// </summary>
    /// <remarks>
    /// The subject is a registered one, so the refusal is the claim gate's and nothing later: without the
    /// gate, this request opens a session.
    /// </remarks>
    [Test]
    public async Task LockedSignIn_WithAnUnverifiedEmail_IsRefused401_AndWritesNothing()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        _ = await factory.CreateSignedInClientAsync(Subject, Email);
        RowCounts before = await RowCountsAsync(host);
        Dictionary<string, object> claims = Claims(Subject, Email);
        claims["email_verified"] = false;

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            factory.CreateClient(), ProviderToken(signingKey, claims));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await MemberOfAsync(response, "title")).IsEqualTo(RegistrationClaimGate.UnverifiedEmailTitle);
        await Assert.That(SetsAnyCookie(response)).IsFalse();
        await Assert.That(await RowCountsAsync(host)).IsEqualTo(before);
    }

    /// <summary>
    /// A validly signed token carrying no address is refused with registration's missing-claims title.
    /// </summary>
    /// <remarks>
    /// The subject is a registered one and the handler reads nothing but the subject, so nothing past the
    /// claim gate would refuse this: without the gate, this request opens a session.
    /// </remarks>
    [Test]
    public async Task LockedSignIn_WithNoEmailClaim_IsRefused401_AndWritesNothing()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        _ = await factory.CreateSignedInClientAsync(Subject, Email);
        RowCounts before = await RowCountsAsync(host);
        Dictionary<string, object> claims = Claims(Subject, Email);
        claims.Remove("email");

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            factory.CreateClient(), ProviderToken(signingKey, claims));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await MemberOfAsync(response, "title")).IsEqualTo(RegistrationClaimGate.MissingClaimsTitle);
        await Assert.That(SetsAnyCookie(response)).IsFalse();
        await Assert.That(await RowCountsAsync(host)).IsEqualTo(before);
    }

    /// <summary>
    /// A validly signed token carrying no subject is refused with the same title as a missing address.
    /// </summary>
    /// <remarks>
    /// Not a claims transformation, as registration's test needs: the real bearer handler validates a
    /// token with no <c>sub</c>, so the principal arrives authenticated and subject-less, which is the
    /// state the gate is about. Without the gate the handler looks up an empty subject and answers 404 —
    /// a different status and no title, so the assertion below tells the two apart.
    /// </remarks>
    [Test]
    public async Task LockedSignIn_WithNoSubjectClaim_IsRefused401_AndWritesNothing()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        _ = await factory.CreateSignedInClientAsync(Subject, Email);
        RowCounts before = await RowCountsAsync(host);
        Dictionary<string, object> claims = Claims(Subject, Email);
        claims.Remove("sub");

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            factory.CreateClient(), ProviderToken(signingKey, claims));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await MemberOfAsync(response, "title")).IsEqualTo(RegistrationClaimGate.MissingClaimsTitle);
        await Assert.That(SetsAnyCookie(response)).IsFalse();
        await Assert.That(await RowCountsAsync(host)).IsEqualTo(before);
    }

    /// <summary>
    /// The control for the refusals above: the same host and key, and a forged signature, is a 401 too —
    /// while the faultless token beside it is not.
    /// </summary>
    [Test]
    public async Task LockedSignIn_WithAForgedProviderToken_IsRefused401_WhileTheValidOneSucceeds()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        _ = await factory.CreateSignedInClientAsync(Subject, Email);
        string valid = ProviderToken(signingKey, Claims(Subject, Email));
        string forged = $"{valid[..valid.LastIndexOf('.')]}.{Base64UrlText.Encode(RandomNumberGenerator.GetBytes(256))}";
        RowCounts before = await RowCountsAsync(host);

        // Act
        HttpResponseMessage refused = await PostLockedSessionAsync(factory.CreateClient(), forged);
        RowCounts afterRefusal = await RowCountsAsync(host);
        HttpResponseMessage admitted = await PostLockedSessionAsync(factory.CreateClient(), valid);

        // Assert
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(SetsAnyCookie(refused)).IsFalse();
        await Assert.That(afterRefusal).IsEqualTo(before);
    }

    [Test]
    public async Task LockedSignIn_WithoutTheClientHeader_IsRefused403_AndWritesNothing()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "locked-sign-in" };
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        _ = await factory.CreateSignedInClientAsync(Subject, Email);
        RowCounts before = await RowCountsAsync(host);
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove(FirstPartyRequestTests.ClientHeader);

        // Act
        HttpResponseMessage response = await PostLockedSessionAsync(
            client, ProviderToken(signingKey, Claims(Subject, Email)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await MemberOfAsync(response, "title")).IsEqualTo(FirstPartyRequestMiddleware.Title);
        await Assert.That(SetsAnyCookie(response)).IsFalse();
        await Assert.That(await RowCountsAsync(host)).IsEqualTo(before);
    }

    /// <summary>The session a handle opens, read on the admin connection by the handle's digest.</summary>
    private sealed record StoredSession(Guid UserId, Guid CredentialId, string Kind, DateTime ExpiresAtUtc);

    /// <summary>Every table this route could write, counted; compared by value.</summary>
    private sealed record RowCounts(long Users, long Credentials, long Sessions, long SessionTokens, long Schedules);

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// A factory whose provider scheme is the real bearer handler holding <paramref name="signingKey" />
    /// and no metadata address, the shape <c>EmailChangeEndpointTests</c> builds.
    /// </summary>
    private static ApiFactory CreateRealBearerFactory(
        PostgresTestHost host,
        SecurityKey signingKey,
        Action<IServiceCollection>? configureServices = null) =>
        host.CreateFactory(configureServices: services =>
        {
            services.PostConfigure<JwtBearerOptions>(ProviderAuthentication.SchemeName, options =>
            {
                OpenIdConnectConfiguration configuration = new() { Issuer = LogCensusTraffic.ProviderIssuer };
                configuration.SigningKeys.Add(signingKey);
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
            configureServices?.Invoke(services);
        });

    /// <summary>
    /// Wraps whatever <see cref="ISessionTokenRepository" /> the application registered so each discovery
    /// read is counted, leaving the registration's lifetime alone.
    /// </summary>
    /// <remarks>
    /// The implementation type is rebuilt from the descriptor the application registered rather than
    /// named here — the shape <c>AccountRegistrationTests</c> uses — so this cannot start decorating a
    /// different implementation than the one the application resolves.
    /// </remarks>
    private static void CountSessionTokenLookups(IServiceCollection services, TokenLookupCounter lookups)
    {
        // Last, not single: the last registration for a service type is the one that resolves.
        ServiceDescriptor registered =
            services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(ISessionTokenRepository))
            ?? throw new InvalidOperationException(
                $"Nothing registered {nameof(ISessionTokenRepository)}, so there is nothing to count.");

        services.Remove(registered);
        services.Add(ServiceDescriptor.Describe(
            typeof(ISessionTokenRepository),
            provider => new CountingSessionTokenRepository(Undecorated(provider, registered), lookups),
            registered.Lifetime));
    }

    private static ISessionTokenRepository Undecorated(IServiceProvider provider, ServiceDescriptor registered) =>
        registered switch
        {
            { ImplementationType: { } type } =>
                (ISessionTokenRepository)ActivatorUtilities.CreateInstance(provider, type),
            { ImplementationFactory: { } factory } => (ISessionTokenRepository)factory(provider),
            { ImplementationInstance: ISessionTokenRepository instance } => instance,
            _ => throw new InvalidOperationException(
                $"The {nameof(ISessionTokenRepository)} registration has no shape this helper can rebuild."),
        };

    /// <summary>
    /// How many discovery reads ran. Owned by the test rather than the decorator, because the decorator is
    /// rebuilt per scope and a field on it would leave with the request.
    /// </summary>
    private sealed class TokenLookupCounter
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Record() => Interlocked.Increment(ref count);
    }

    /// <summary>Counts each lookup and forwards it to the real repository.</summary>
    private sealed class CountingSessionTokenRepository(ISessionTokenRepository inner, TokenLookupCounter lookups)
        : ISessionTokenRepository
    {
        public Task<SessionToken?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default)
        {
            lookups.Record();
            return inner.FindByTokenHashAsync(tokenHash, cancellationToken);
        }
    }

    private static Dictionary<string, object> Claims(string subject, string email) => new(StringComparer.Ordinal)
    {
        ["sub"] = subject,
        ["email"] = email,
        ["email_verified"] = true,
    };

    private static string ProviderToken(SecurityKey key, IDictionary<string, object> claims)
    {
        DateTime issued = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LogCensusTraffic.ProviderIssuer,
            Audience = LogCensusTraffic.ProviderAudience,
            Claims = claims,
            IssuedAt = issued,
            NotBefore = issued,
            Expires = issued.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    private static async Task<HttpResponseMessage> PostLockedSessionAsync(HttpClient client, string? token)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, LockedSessionPath);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();

        return response;
    }

    private static HttpClient CookieClient(ApiFactory factory, string cookie)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookieAuthenticationTests.CookieName}={cookie}");

        return client;
    }

    /// <summary>The session handle a seeded client presents, read back from its own header.</summary>
    private static string CookieValueOf(HttpClient client)
    {
        string prefix = $"{SessionCookieAuthenticationTests.CookieName}=";
        string header = client.DefaultRequestHeaders.GetValues("Cookie").Single();

        return header.StartsWith(prefix, StringComparison.Ordinal)
            ? header[prefix.Length..]
            : throw new InvalidOperationException("The client presents no session cookie.");
    }

    private static bool SetsAnyCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) && values.Any();

    private static async Task SeedScheduleAsync(PostgresTestHost host, Guid userId, DateTime takesEffectAtUtc)
    {
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new(
            "insert into erasure_schedules (user_id, takes_effect_at_utc) values (@user, @at)", admin);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("at", takesEffectAtUtc);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Every schedule in the table as one line of <c>user=ticks</c>, read on the admin connection, so a
    /// moved instant, a missing row or an extra one all fail naming what changed.
    /// </summary>
    private static async Task<string> RenderSchedulesAsync(PostgresTestHost host)
    {
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new(
            "select user_id, takes_effect_at_utc from erasure_schedules order by user_id", admin);

        List<string> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetGuid(0)}={reader.GetFieldValue<DateTime>(1).Ticks}");
        }

        return string.Join(", ", rows);
    }

    private static async Task<StoredSession> StoredSessionOfAsync(PostgresTestHost host, string cookie)
    {
        byte[] digest = SessionToken.HashOf(Base64UrlText.Decode(cookie));

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new(
            "select s.user_id, s.credential_id, s.kind, s.expires_at_utc from sessions s "
            + "join session_tokens t on t.session_id = s.id where t.token_hash = @digest",
            admin);
        command.Parameters.AddWithValue("digest", digest);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The cookie this response set opens no stored session.");
        }

        StoredSession stored = new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));

        return await reader.ReadAsync()
            ? throw new InvalidOperationException("The cookie this response set matches more than one session.")
            : stored;
    }

    /// <summary>The id of the session a cookie opens, read through its handle on the admin connection.</summary>
    private static async Task<Guid> SessionIdOfAsync(PostgresTestHost host, string cookie)
    {
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new(
            "select session_id from session_tokens where token_hash = @digest", admin);
        command.Parameters.AddWithValue("digest", SessionToken.HashOf(Base64UrlText.Decode(cookie)));

        return await command.ExecuteScalarAsync() switch
        {
            Guid sessionId => sessionId,
            var unexpected => throw new InvalidOperationException(
                $"Expected the cookie's session id, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Opens one more live locked session on the account's federated credential — the account signed in
    /// on another device — and returns its id.
    /// </summary>
    /// <remarks>
    /// Seeded on the admin connection through <c>Session.Establish</c>, so the row is one the product could
    /// write, with a random handle no client in the test presents.
    /// </remarks>
    private static async Task<Guid> SeedAnotherDevicesLockedSessionAsync(PostgresTestHost host, Guid userId)
    {
        Guid federatedId = await RepositoryTestHost.FederatedCredentialIdOnAsync(host.ConnectionString, userId);
        byte[] token = RandomNumberGenerator.GetBytes(SessionToken.TokenLength);
        DateTime now = DateTime.UtcNow;
        await RepositoryTestHost.SeedSessionOnAsync(
            host.ConnectionString, federatedId, token, SessionKind.Locked, now.AddMinutes(-1), now.AddHours(1));

        return await SessionIdOfAsync(host, Base64UrlText.Encode(token));
    }

    /// <summary>How many <c>sessions</c> rows carry this id, on the admin connection.</summary>
    private static Task<long> CountSessionsAsync(PostgresTestHost host, Guid sessionId) =>
        CountWhereAsync(host, "select count(*) from sessions where id = @value", sessionId);

    /// <summary>How many <c>session_tokens</c> rows a cookie's digest matches, on the admin connection.</summary>
    private static Task<long> CountHandlesAsync(PostgresTestHost host, string cookie) =>
        CountWhereAsync(
            host,
            "select count(*) from session_tokens where token_hash = @value",
            SessionToken.HashOf(Base64UrlText.Decode(cookie)));

    private static async Task<long> CountWhereAsync(PostgresTestHost host, string sql, object value)
    {
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new(sql, admin);
        command.Parameters.AddWithValue("value", value);

        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{sql}', got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<RowCounts> RowCountsAsync(PostgresTestHost host)
    {
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        async Task<long> CountAsync(string table)
        {
            await using NpgsqlCommand command = new($"select count(*) from {table}", admin);
            return await command.ExecuteScalarAsync() switch
            {
                long count => count,
                var unexpected => throw new InvalidOperationException(
                    $"Expected a count from '{table}', got '{unexpected ?? "null"}'."),
            };
        }

        return new RowCounts(
            await CountAsync("users"),
            await CountAsync("credentials"),
            await CountAsync("sessions"),
            await CountAsync("session_tokens"),
            await CountAsync("erasure_schedules"));
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject
        ?? throw new InvalidOperationException(
            $"The {(int)response.StatusCode} response body is not a JSON object.");

    /// <summary>A string member of the body, or null when the body is empty, not JSON, or has none.</summary>
    private static async Task<string?> MemberOfAsync(HttpResponseMessage response, string member)
    {
        string raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw) is JsonObject body && body[member] is JsonValue value
                ? value.GetValue<string>()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static DateTime ParseUtc(string text)
    {
        DateTime parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return parsed.Kind == DateTimeKind.Utc
            ? parsed
            : throw new FormatException($"'{text}' is not a UTC instant.");
    }
}
