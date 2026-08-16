using System.Net;
using System.Text.Json.Nodes;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That a request authenticates from the first-party session cookie: the account it lands on, the
/// budget it lands in, and every shape of presented handle that must not land anywhere at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test here pairs its refusal with the accepting arm of the same arrangement.</b> Today the
/// application refuses an unauthenticated request whatever it carries, so "a revoked cookie is refused"
/// is a sentence the current code satisfies by refusing everyone — and it would go on being satisfied by
/// a cookie handler that was never reached. The valid cookie beside it is what makes each refusal a
/// statement about the handle rather than about the door being shut.
/// </para>
/// <para>
/// <b>These run over the least-privilege application connection</b>, which is the whole point of driving
/// them through a real host rather than over a handler. The authentication path reads
/// <c>session_tokens</c> — exempt from row-level security — <em>before</em> it publishes an identity, and
/// only then reads the policed <c>sessions</c> row. A transaction opened anywhere above that publication
/// configures the connection while <c>app.current_user_id</c> is still empty and every policed statement
/// inside it fails with <c>22P02</c>. Nothing but a request served by the real role can see that.
/// </para>
/// <para>
/// <b>The cookie name and the client header are written out as literals here, not read off a production
/// constant.</b> Both are wire contract: a browser sends the bytes, not the symbol. A test reading the
/// constant agrees with whatever the constant says and stays green through a rename that breaks every
/// client already holding a cookie; a test holding its own literal goes red, which is the review the
/// rename needs. The same argument covers <see cref="FirstPartyRequestTests.ClientHeader" />, which is
/// declared once over there for the same reason and used from here.
/// </para>
/// </remarks>
public sealed class SessionCookieAuthenticationTests
{
    /// <summary>
    /// The name of the cookie a request presents its session handle in. See the remarks on the class for
    /// why this is a literal rather than a production constant.
    /// </summary>
    internal const string CookieName = "__Host-budgetoid-session";

    private const string AccountsPath = "/api/accounts";
    private const string MePath = "/api/me";

    [Test]
    public async Task AValidCookie_PublishesTheAccountAndItsAmbientBudget()
    {
        // Arrange — one account with a budget, one row of budget content in it, and one live session
        // whose handle the cookie carries.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        await SeedAccountAsync(host, owner.BudgetId, OwnerAccountName);
        byte[] token = await SeedLiveSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act — a budget-content route and an account route, because the cookie has to publish two
        // separate things and either one alone leaves the other unmeasured: the identity, which
        // /api/me answers from, and the ambient budget, which the account list is filtered by.
        HttpResponseMessage accounts = await SendAsync(client, HttpMethod.Get, AccountsPath, token);
        HttpResponseMessage me = await SendAsync(client, HttpMethod.Get, MePath, token);

        // Assert
        await Assert.That(accounts.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await accounts.Content.ReadAsStringAsync()).Contains(OwnerAccountName);
        await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await EmailOfAsync(me)).IsEqualTo(OwnerEmail);
    }

    /// <summary>
    /// That each cookie reaches its own account's rows and none of the other's.
    /// </summary>
    /// <remarks>
    /// The control for the test above, and without it that one is worth very little: with one account
    /// established, a handler that ignored the presented handle entirely and published the first
    /// <c>users</c> row would answer it perfectly. It takes a second account for "the cookie decided
    /// who this is" and "somebody was signed in" to be different sentences.
    /// </remarks>
    [Test]
    public async Task ASecondAccountsCookie_ReachesOnlyItsOwnRows()
    {
        // Arrange — the first account is seeded first, so a read that takes the earliest row answers
        // the second caller with the first one's data and this test goes red.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner first = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        await SeedAccountAsync(host, first.BudgetId, OwnerAccountName);
        byte[] firstToken = await SeedLiveSessionAsync(host, first.UserId, fill: 0x11);

        RepositoryTestHost.SeededOwner second = await host.SeedOwnerAsync(StrangerSubject, StrangerEmail);
        await SeedAccountAsync(host, second.BudgetId, StrangerAccountName);
        byte[] secondToken = await SeedLiveSessionAsync(host, second.UserId, fill: 0x22);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage secondAccounts = await SendAsync(client, HttpMethod.Get, AccountsPath, secondToken);
        HttpResponseMessage secondMe = await SendAsync(client, HttpMethod.Get, MePath, secondToken);
        HttpResponseMessage firstAccounts = await SendAsync(client, HttpMethod.Get, AccountsPath, firstToken);

        // Assert — both directions of the second caller's answer, since a payload carrying everyone's
        // rows at once satisfies "mine is present" just as well as the right one does.
        await Assert.That(secondAccounts.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string secondPayload = await secondAccounts.Content.ReadAsStringAsync();
        await Assert.That(secondPayload).Contains(StrangerAccountName);
        await Assert.That(secondPayload).DoesNotContain(OwnerAccountName);
        await Assert.That(await EmailOfAsync(secondMe)).IsEqualTo(StrangerEmail);

        // And the first caller still gets its own, which is what refuses an implementation that had
        // simply been made to answer the LAST row instead of the first.
        await Assert.That(firstAccounts.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string firstPayload = await firstAccounts.Content.ReadAsStringAsync();
        await Assert.That(firstPayload).Contains(OwnerAccountName);
        await Assert.That(firstPayload).DoesNotContain(StrangerAccountName);
    }

    [Test]
    public async Task NoCookie_IsRefused()
    {
        // Arrange — a live session exists on this host, so "refused" is a verdict on the request that
        // presented nothing rather than a database with nobody in it.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] token = await SeedLiveSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage withoutCookie = await SendAsync(client, HttpMethod.Get, MePath, token: null);
        HttpResponseMessage withCookie = await SendAsync(client, HttpMethod.Get, MePath, token);

        // Assert
        await Assert.That(withoutCookie.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withCookie.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task AnUnknownToken_IsRefused()
    {
        // Arrange — the unknown handle is well-formed in every way a request can observe: 32 bytes,
        // unpadded base64url. The only thing wrong with it is that no row was ever written for it.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] known = await SeedLiveSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage unknown = await SendAsync(client, HttpMethod.Get, MePath, TokenBytes(0x99));
        HttpResponseMessage stored = await SendAsync(client, HttpMethod.Get, MePath, known);

        // Assert
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(stored.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task ARevokedSession_IsRefused()
    {
        // Arrange — one account, two sessions, identical but for the revocation instant on one of
        // them. One account rather than two is deliberate: it leaves the revocation as the only
        // difference between the two handles.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        DateTime now = DateTime.UtcNow;
        byte[] revoked = await SeedSessionAsync(
            host,
            owner.UserId,
            fill: 0x11,
            createdAtUtc: now.AddHours(-1),
            expiresAtUtc: now.AddHours(1),
            revokedAtUtc: now.AddMinutes(-1));
        byte[] live = await SeedLiveSessionAsync(host, owner.UserId, fill: 0x22);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage ended = await SendAsync(client, HttpMethod.Get, MePath, revoked);
        HttpResponseMessage standing = await SendAsync(client, HttpMethod.Get, MePath, live);

        // Assert — the second half is what says a revoked cookie was read and rejected rather than
        // every cookie on this account being rejected together.
        await Assert.That(ended.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(standing.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task AnExpiredSession_IsRefused()
    {
        // Arrange — expiry an hour in the past against a live session an hour in the future, on one
        // account. Wide margins on the real clock; the instant either side of the boundary is the
        // next test's subject and needs a clock this one deliberately does not fix.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        DateTime now = DateTime.UtcNow;
        byte[] expired = await SeedSessionAsync(
            host,
            owner.UserId,
            fill: 0x11,
            createdAtUtc: now.AddHours(-2),
            expiresAtUtc: now.AddHours(-1));
        byte[] live = await SeedLiveSessionAsync(host, owner.UserId, fill: 0x22);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage lapsed = await SendAsync(client, HttpMethod.Get, MePath, expired);
        HttpResponseMessage standing = await SendAsync(client, HttpMethod.Get, MePath, live);

        // Assert
        await Assert.That(lapsed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(standing.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// That the expiry boundary is exclusive: a session is live up to its expiry and not at it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one test in this file that fixes the clock, because it is the only claim that cannot be
    /// stated against a moving one — "in the future by a hair" and "exactly now" are the same instant
    /// to a real clock by the time the request arrives. <see cref="Session.IsActiveAt" /> answers
    /// <c>instantUtc &lt; ExpiresAtUtc</c>, and an off-by-one there is invisible at every margin but
    /// this one.
    /// </para>
    /// <para>
    /// <b>It assumes the authentication path reads the clock through the injected
    /// <see cref="TimeProvider" /></b>, as every other handler in this system does. A path that
    /// called <c>DateTime.UtcNow</c> instead would fail this test with both arms wrong, which is the
    /// correct answer rather than a false alarm: a session's lifetime would then be unfixable from a
    /// test at all.
    /// </para>
    /// <para>
    /// A second rather than a tick, because <c>timestamptz</c> stores microseconds: a tick of
    /// separation is rounded away by the column and the two sessions would arrive at the same instant.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheExpiryBoundary_IsExclusive()
    {
        // Arrange — the clock is fixed before the host is built, so the request instant is a value
        // this test chose rather than whatever the run took.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(
            host,
            services => services.Replace(
                ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(BoundaryInstant)))));
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] atTheBoundary = await SeedSessionAsync(
            host,
            owner.UserId,
            fill: 0x11,
            createdAtUtc: BoundaryInstant.AddHours(-1),
            expiresAtUtc: BoundaryInstant);
        byte[] aHairLater = await SeedSessionAsync(
            host,
            owner.UserId,
            fill: 0x22,
            createdAtUtc: BoundaryInstant.AddHours(-1),
            expiresAtUtc: BoundaryInstant.AddSeconds(1));
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage at = await SendAsync(client, HttpMethod.Get, MePath, atTheBoundary);
        HttpResponseMessage after = await SendAsync(client, HttpMethod.Get, MePath, aHairLater);

        // Assert
        await Assert.That(at.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// That a cookie value which is not unpadded base64url is refused, and refused as a refusal rather
    /// than as a fault.
    /// </summary>
    /// <remarks>
    /// The status is the whole assertion and 500 is the failure it is written against: the decode runs
    /// on a value a caller chose, so a decoder that threw would turn a hand-typed cookie into a logged
    /// server fault — and, on the anonymous surface, into an oracle that answers differently for
    /// differently-malformed input.
    /// </remarks>
    [Test]
    public async Task ATokenThatIsNotBase64Url_IsRefused()
    {
        // Arrange — padding and the two characters standard base64 uses that base64url does not, plus
        // a value that is not base64 of any dialect. Each is a different arm of a decoder.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] live = await SeedLiveSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();
        string[] malformed =
        [
            // Standard base64 of the same 32 bytes: padded, and carrying '+' and '/'.
            Convert.ToBase64String(TokenBytes(0xFB)),

            // Unpadded base64url with padding characters appended anyway.
            Base64UrlText.Encode(TokenBytes(0x11)) + "==",

            // Not the alphabet at all.
            "not a session handle",

            // Empty, which is what a cleared cookie replayed by a client looks like.
            string.Empty,
        ];

        // Act
        List<string> answers = [];
        foreach (string value in malformed)
        {
            HttpResponseMessage response = await SendCookieValueAsync(client, HttpMethod.Get, MePath, value);
            answers.Add($"'{value}' -> {(int)response.StatusCode}");
        }

        HttpResponseMessage wellFormed = await SendAsync(client, HttpMethod.Get, MePath, live);

        // Assert — joined rather than asserted one at a time, so a failure names the value that got
        // through instead of stopping at the first.
        await Assert.That(string.Join("; ", answers)).IsEqualTo(
            string.Join("; ", malformed.Select(value => $"'{value}' -> 401")));
        await Assert.That(wellFormed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task ATokenOfTheWrongWidth_IsRefused()
    {
        // Arrange — both sides of the width, because a decoder that truncated would accept the long
        // one and a decoder that padded would accept the short one, and neither is caught by the
        // other's case.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] live = await SeedLiveSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // The stored token with a byte taken off and with a byte added: the short one is a PREFIX of a
        // real handle, so a decoder that truncated to 32 would find the row for the long one and one
        // that padded would find it for the short one.
        byte[] tooShort = [.. live.AsSpan(0, SessionToken.TokenLength - 1)];
        byte[] tooLong = [.. live, 0x00];

        // Act
        HttpResponseMessage shortAnswer = await SendAsync(client, HttpMethod.Get, MePath, tooShort);
        HttpResponseMessage longAnswer = await SendAsync(client, HttpMethod.Get, MePath, tooLong);
        HttpResponseMessage exact = await SendAsync(client, HttpMethod.Get, MePath, live);

        // Assert
        await Assert.That(shortAnswer.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(longAnswer.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(exact.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// That a session on an account holding no budget fails loudly instead of answering with a tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invariant <c>IBudgetRepository.FindFirstForUserAsync</c> documents and
    /// <c>ResolveUserHandler</c> already throws on: an account resolved with no budget is a broken row,
    /// not a state to be recovered from. What this test refuses is the recovery — a request that fell
    /// back to <em>some</em> budget would answer 200 with another tenant's rows, which is the one
    /// failure mode of this whole path that nobody would notice.
    /// </para>
    /// <para>
    /// The bystander account is the arrangement. With no other budget in the database, "answered with
    /// somebody else's tenant" has nothing to be answered with, and a fallback would show up as an
    /// empty list nobody could tell from a correct refusal.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AnAccountWithNoBudget_FailsLoudly()
    {
        // Arrange — a bystander with a budget and content in it, and the subject of the test: a user
        // seeded WITHOUT a budget, holding a live session.
        await using RepositoryTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner bystander = await host.SeedOwnerAsync(StrangerSubject, StrangerEmail);
        await SeedAccountAsync(host, bystander.BudgetId, StrangerAccountName);
        byte[] bystanderToken = await SeedLiveSessionAsync(host, bystander.UserId, fill: 0x22);

        Guid budgetless = await host.SeedUserAsync(OwnerSubject, OwnerEmail);
        byte[] token = await SeedLiveSessionAsync(host, budgetless, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, AccountsPath, token);
        HttpResponseMessage bystanderResponse = await SendAsync(client, HttpMethod.Get, AccountsPath, bystanderToken);

        // Assert — not 200, and the bystander's rows nowhere in the body. The status alone would pass
        // against a 200 carrying an empty list; the body alone would pass against a 200 that answered
        // the budgetless account with nothing at all, which is the quiet success this refuses.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain(StrangerAccountName);

        // The bystander's own request succeeding is what makes the refusal above a verdict on the
        // broken account rather than on every cookie this host would ever see — which is a state an
        // application with no cookie authentication at all satisfies perfectly.
        await Assert.That(bystanderResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await bystanderResponse.Content.ReadAsStringAsync()).Contains(StrangerAccountName);
    }

    /// <summary>The subject and address of the account most tests here sign in as.</summary>
    private const string OwnerSubject = "google-cookie-owner";

    private const string OwnerEmail = "cookie-owner@budgetoid.test";

    /// <summary>A second account, present so that "my rows" and "the rows" are different answers.</summary>
    private const string StrangerSubject = "google-cookie-stranger";

    private const string StrangerEmail = "cookie-stranger@budgetoid.test";

    /// <summary>
    /// Names of the seeded budget content. Distinct strings and neither a substring of the other, so a
    /// payload check for one cannot be satisfied by the other.
    /// </summary>
    private const string OwnerAccountName = "Owners Current Account";

    private const string StrangerAccountName = "Strangers Savings Pot";

    /// <summary>
    /// The instant <see cref="TheExpiryBoundary_IsExclusive" /> fixes its clock to. Whole seconds, so
    /// nothing here is at the mercy of <c>timestamptz</c>'s microsecond rounding.
    /// </summary>
    private static readonly DateTime BoundaryInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Sends one request carrying the first-party client header and, optionally, the session cookie.
    /// </summary>
    /// <remarks>
    /// The header goes on every request because the CSRF control refuses one without it before anything
    /// looks at the cookie — see <see cref="FirstPartyRequestTests" />, which owns that claim. A test in
    /// this file that omitted it would be reading a 403 and calling it a rejected handle.
    /// </remarks>
    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        byte[]? token) =>
        SendCookieValueAsync(client, method, path, token is null ? null : Base64UrlText.Encode(token));

    /// <summary>
    /// The same request with the cookie's value supplied verbatim, for the values no encoder produces.
    /// </summary>
    private static Task<HttpResponseMessage> SendCookieValueAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? cookieValue)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Add(FirstPartyRequestTests.ClientHeader, FirstPartyRequestTests.ClientHeaderValue);
        if (cookieValue is not null)
        {
            request.Headers.Add("Cookie", $"{CookieName}={cookieValue}");
        }

        return client.SendAsync(request);
    }

    /// <summary>
    /// Seeds a passkey credential, a session that is live for the next hour, and the handle it is
    /// presented by; returns the token bytes the cookie carries.
    /// </summary>
    private static Task<byte[]> SeedLiveSessionAsync(RepositoryTestHost host, Guid userId, byte fill)
    {
        DateTime now = DateTime.UtcNow;
        return SeedSessionAsync(host, userId, fill, now.AddMinutes(-1), now.AddHours(1));
    }

    /// <summary>
    /// Seeds one sign-in: a passkey credential, the session it opened, and the stored hash of the token
    /// the cookie will present. Returns the token itself, which exists nowhere but here and the cookie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>passkey</b> credential rather than the federated one <see cref="RepositoryTestHost.SeedOwnerAsync" />
    /// writes, because <c>Session.Establish</c> derives the kind from the credential's type and a
    /// federated session is <see cref="SessionKind.Locked" /> — which by <c>Session.ReadsBudgetContent</c>
    /// may not reach the budget content half of these tests assert on. Seeding the wrong credential type
    /// would make that a refusal nobody could tell from a broken cookie.
    /// </para>
    /// <para>
    /// The session and its handle go in one <c>SaveChangesAsync</c>, which is the shape the establishing
    /// path writes them in: a handle committed without its session names nothing.
    /// <see cref="SessionToken.For" /> reads both ids off the session, so nothing here can file a handle
    /// against the wrong sign-in.
    /// </para>
    /// </remarks>
    private static async Task<byte[]> SeedSessionAsync(
        RepositoryTestHost host,
        Guid userId,
        byte fill,
        DateTime createdAtUtc,
        DateTime expiresAtUtc,
        DateTime? revokedAtUtc = null)
    {
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId(fill));

        await using BudgetoidDbContext db = CreateDb(host);
        Credential credential = await db.Credentials.SingleAsync(stored => stored.Id == credentialId);
        Session session = Session.Establish(credential, createdAtUtc, expiresAtUtc);
        if (revokedAtUtc is not null)
        {
            session.Revoke(revokedAtUtc.Value);
        }

        byte[] token = TokenBytes(fill);
        db.Sessions.Add(session);
        db.SessionTokens.Add(SessionToken.For(session, token));
        await db.SaveChangesAsync();

        return token;
    }

    /// <summary>
    /// A token of <see cref="SessionToken.TokenLength" /> bytes, every one of them <paramref name="fill" />.
    /// </summary>
    /// <remarks>
    /// The fill byte is required rather than defaulted: the digest is the primary key of
    /// <c>session_tokens</c>, so two identical tokens would be one row and every test holding two handles
    /// would have nothing to choose wrongly between.
    /// </remarks>
    private static byte[] TokenBytes(byte fill) => [.. Enumerable.Repeat(fill, SessionToken.TokenLength)];

    /// <summary>
    /// The WebAuthn credential id a seeded passkey carries. Derived from the same fill byte as the
    /// token, because the column is unique and two passkeys on one account must differ.
    /// </summary>
    private static byte[] WebAuthnCredentialId(byte fill) => [.. Enumerable.Repeat(fill, 16)];

    /// <summary>
    /// Writes one row of budget content with raw SQL on the container superuser connection.
    /// </summary>
    /// <remarks>
    /// Raw SQL rather than the domain factory through EF, because <c>Account</c> carries the
    /// <c>BudgetIsolation</c> query filter and a seeding context is built without an
    /// <c>IBudgetContext</c> to satisfy it. <c>USD</c> is a currency the migration seeds, so the foreign
    /// key is met without a currency of this test's own.
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

    private static async Task<string> EmailOfAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!["email"]!.GetValue<string>();

    /// <summary>
    /// A context on the container superuser connection, with no ambient budget. Safe for what is seeded
    /// through it — neither <c>Session</c>, <c>SessionToken</c> nor <c>Credential</c> carries a budget
    /// query filter — and superuser because these rows are arranged, not measured.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    /// <summary>
    /// The host every test here serves requests through: the application's own authentication left
    /// standing, over the least-privilege role, with the container account reserved for seeding.
    /// </summary>
    internal static ApiFactory CreateApiFactory(
        RepositoryTestHost host,
        Action<IServiceCollection>? configureServices = null) =>
        new(
            host.AppConnectionString,
            configureServices: configureServices,
            adminConnectionString: host.ConnectionString,
            usesApplicationAuthentication: true);

    internal static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
