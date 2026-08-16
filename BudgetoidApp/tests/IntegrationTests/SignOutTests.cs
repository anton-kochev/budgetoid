using System.Net;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That signing out ends the session the caller presented, ends nothing else, and takes the cookie off
/// the client on the way out.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="SigningOut_LeavesAnotherDeviceSignedIn" /> is the test this file exists for.</b> A
/// sign-out that swept every session on the account passes every other test here — the caller's cookie
/// stops working, twice in a row is still 204, and the cookie is cleared either way — while signing the
/// person out of the phone in their pocket because they closed a tab on a laptop. The single-session
/// predicate is the whole feature and only a second live session can see it.
/// </para>
/// <para>
/// The cookie name and the client header are literals, for the reason
/// <see cref="SessionCookieAuthenticationTests" /> states once for all three files.
/// </para>
/// </remarks>
public sealed class SignOutTests
{
    private const string RevocationPath = "/api/me/session/revocation";
    private const string MePath = "/api/me";

    [Test]
    public async Task SigningOut_EndsTheCallersSession()
    {
        // Arrange — one account, one live session, and a request that works before the sign-out so
        // that the refusal afterwards is a change rather than a state.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        // The budget comes with the owner: /api/me resolves one, and an account without one is a
        // different test's subject.
        Guid userId = (await host.SeedOwnerAsync(OwnerSubject, OwnerEmail)).UserId;
        byte[] token = await SeedLiveSessionAsync(host, userId, fill: 0x11);
        HttpClient client = factory.CreateClient();
        HttpResponseMessage before = await SendAsync(client, HttpMethod.Get, MePath, token);

        // Act
        HttpResponseMessage signOut = await SendAsync(client, HttpMethod.Post, RevocationPath, token);
        HttpResponseMessage after = await SendAsync(client, HttpMethod.Get, MePath, token);

        // Assert
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(signOut.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// That signing out twice with the same cookie answers 204 both times.
    /// </summary>
    /// <remarks>
    /// A retried sign-out is the ordinary case, not an edge one: the response can be lost on the way
    /// back, and a client that saw a 401 for its second attempt would have to decide whether the first
    /// worked. There is also nothing to protect on the second call — the session is already ended — so
    /// answering anything but 204 buys the caller a decision and buys the account nothing.
    /// </remarks>
    [Test]
    public async Task SigningOutTwice_IsIdempotent()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        // The budget comes with the owner: /api/me resolves one, and an account without one is a
        // different test's subject.
        Guid userId = (await host.SeedOwnerAsync(OwnerSubject, OwnerEmail)).UserId;
        byte[] token = await SeedLiveSessionAsync(host, userId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage first = await SendAsync(client, HttpMethod.Post, RevocationPath, token);
        HttpResponseMessage second = await SendAsync(client, HttpMethod.Post, RevocationPath, token);

        // Assert
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task SigningOut_LeavesAnotherDeviceSignedIn()
    {
        // Arrange — two live sessions on ONE account, which is the arrangement: with two accounts, a
        // sweep keyed on the user would still leave the other one working and this test would pass
        // over the defect it is written for.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        // The budget comes with the owner: /api/me resolves one, and an account without one is a
        // different test's subject.
        Guid userId = (await host.SeedOwnerAsync(OwnerSubject, OwnerEmail)).UserId;
        byte[] laptop = await SeedLiveSessionAsync(host, userId, fill: 0x11);
        byte[] phone = await SeedLiveSessionAsync(host, userId, fill: 0x22);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage signOut = await SendAsync(client, HttpMethod.Post, RevocationPath, laptop);
        HttpResponseMessage laptopAfter = await SendAsync(client, HttpMethod.Get, MePath, laptop);
        HttpResponseMessage phoneAfter = await SendAsync(client, HttpMethod.Get, MePath, phone);

        // Assert — the surviving session is the content of this test. Both other assertions are here
        // to say the sign-out really happened, so that a route which did nothing at all cannot pass it.
        await Assert.That(signOut.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(laptopAfter.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(phoneAfter.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// That the response takes the cookie off the client rather than leaving it to expire.
    /// </summary>
    /// <remarks>
    /// The session row is what ends access, so a cookie left behind is not an authentication defect —
    /// it is a client that goes on believing it is signed in, sending a dead handle on every request
    /// and showing a signed-in shell to whoever is at the keyboard. Cleared means what
    /// <c>Response.Cookies.Delete</c> emits: the same name, an empty value and an expiry in the past.
    /// </remarks>
    [Test]
    public async Task SigningOut_ClearsTheCookie()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        // The budget comes with the owner: /api/me resolves one, and an account without one is a
        // different test's subject.
        Guid userId = (await host.SeedOwnerAsync(OwnerSubject, OwnerEmail)).UserId;
        byte[] token = await SeedLiveSessionAsync(host, userId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, RevocationPath, token);

        // Assert — the status first, so a header missing because the route was never reached reads as
        // the 404 it is rather than as a cookie that was not cleared.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        string clearing = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.FirstOrDefault(value => value.StartsWith(
                $"{SessionCookieAuthenticationTests.CookieName}=",
                StringComparison.Ordinal)) ?? "no Set-Cookie for the session cookie"
            : "no Set-Cookie at all";

        // Reduced to one word before comparing, so the failure message carries the header that arrived
        // instead of the boolean it was judged by. Either half is enough to clear a cookie and no
        // client needs both, which is why this reads them as alternatives rather than pinning the
        // exact string the framework happens to emit.
        bool cleared =
            clearing.StartsWith($"{SessionCookieAuthenticationTests.CookieName}=;", StringComparison.Ordinal)
            || clearing.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)
            || clearing.Contains("max-age=0", StringComparison.OrdinalIgnoreCase);
        await Assert.That(cleared ? "cleared" : clearing).IsEqualTo("cleared");
    }

    private const string OwnerSubject = "google-sign-out-owner";

    private const string OwnerEmail = "sign-out-owner@budgetoid.test";

    /// <summary>
    /// Sends one request carrying the first-party client header and the session cookie. Duplicated from
    /// <see cref="SessionCookieAuthenticationTests" /> rather than shared, so each file states the wire
    /// shape it drives without a reader having to leave it.
    /// </summary>
    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        byte[] token)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Add(FirstPartyRequestTests.ClientHeader, FirstPartyRequestTests.ClientHeaderValue);
        request.Headers.Add(
            "Cookie",
            $"{SessionCookieAuthenticationTests.CookieName}={Base64UrlText.Encode(token)}");

        return client.SendAsync(request);
    }

    /// <summary>
    /// Seeds a passkey credential, a session live for the next hour, and the handle it is presented by.
    /// </summary>
    private static async Task<byte[]> SeedLiveSessionAsync(RepositoryTestHost host, Guid userId, byte fill)
    {
        Guid credentialId = await host.SeedPasskeyAsync(userId, [.. Enumerable.Repeat(fill, 16)]);
        DateTime now = DateTime.UtcNow;

        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);
        Credential credential = await db.Credentials.SingleAsync(stored => stored.Id == credentialId);
        Session session = Session.Establish(credential, now.AddMinutes(-1), now.AddHours(1));
        byte[] token = [.. Enumerable.Repeat(fill, SessionToken.TokenLength)];
        db.Sessions.Add(session);
        db.SessionTokens.Add(SessionToken.For(session, token));
        await db.SaveChangesAsync();

        return token;
    }
}
