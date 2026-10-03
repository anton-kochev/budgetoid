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
/// running with nobody published and the session insert running with the account published are both
/// judged by the real policies. A handler that published late answers 500 here with <c>22P02</c>; one that
/// published nobody answers the same.
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

        // Within a microsecond rather than equal: the answer may carry the clock's 100 ns ticks, and a
        // timestamptz keeps microseconds.
        await Assert.That((stored.ExpiresAtUtc - expiresAtUtc).Duration()).IsLessThanOrEqualTo(TimeSpan.FromMicroseconds(1));

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
    /// The cookie is the trap: this route's policy names the provider scheme and nothing else, so a
    /// session cookie — full or locked — must not stand in for the provider. A route that fell back to the
    /// default scheme would admit both, and with the full cookie would hand a passkey holder a locked
    /// session nobody's provider vouched for.
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

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(SetsAnyCookie(response)).IsFalse();
        await Assert.That(await RowCountsAsync(host)).IsEqualTo(before);
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
    private static ApiFactory CreateRealBearerFactory(PostgresTestHost host, SecurityKey signingKey) =>
        host.CreateFactory(configureServices: services =>
            services.PostConfigure<JwtBearerOptions>(ProviderAuthentication.SchemeName, options =>
            {
                OpenIdConnectConfiguration configuration = new() { Issuer = LogCensusTraffic.ProviderIssuer };
                configuration.SigningKeys.Add(signingKey);
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            }));

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
