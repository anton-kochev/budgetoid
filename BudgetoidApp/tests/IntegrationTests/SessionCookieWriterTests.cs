using Api.Infrastructure;
using Application.Sessions;
using Domain.Sessions;
using Domain.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// <see cref="SessionCookieWriter" /> driven directly, for the one property no endpoint test can see:
/// that displacing another account's session leaves the request published as the account that signed in.
/// </summary>
public sealed class SessionCookieWriterTests
{
    /// <summary>
    /// Writing an established session's cookie over another account's live cookie deletes that session
    /// and leaves the request's identity and budget exactly as they were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the endpoint tests cannot hold this.</b> The displacement authenticates the presented
    /// handle, and authenticating publishes the handle's owner. In a scope of its own that publication
    /// goes when the scope does. In the request's scope it replaces the new account's, and anything the
    /// request did afterwards would run as the cookie's account. Every establishing endpoint writes the
    /// cookie last and returns, so nothing observable follows, and the cross-account endpoint tests stay
    /// green either way.
    /// </para>
    /// <para>
    /// <b>The mutation it exists to catch:</b> resolving <c>DisplaceSessionHandler</c> from
    /// <c>context.RequestServices</c> instead of the child scope. The handler then publishes account A into
    /// the request's <see cref="CurrentUser" />. Measured: under that mutation this test failed on the
    /// <c>UserId</c> assertion, with the request republished as account A — while
    /// <c>LockedSignInEndpointTests.LockedSignIn_WithAnotherAccountsLockedCookie_DeletesThatSessionAndItsHandle</c>
    /// stayed green, 2/2. This test is the only one that catches it.
    /// </para>
    /// <para>
    /// <b>Arranged by hand, as the request would stand by then.</b> The request's scope is published as B,
    /// the account the handoff is for, and the request carries A's live cookie. The handoff is minted
    /// through <see cref="SessionHandle" />, the way every establishing handler mints one; the writer does
    /// not read B's row, so none is filed for it. A's row going is the control: a writer that displaced
    /// nothing would leave the identity alone too.
    /// </para>
    /// </remarks>
    [Test]
    public async Task WriteEstablishedAsync_OverAnotherAccountsCookie_LeavesTheRequestPublishedAsItWas()
    {
        // Arrange — A signed in in this browser; B is who just signed in.
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        ApiFactory.SignedInClient accountA = await host.Factory.CreateSignedInClientAsync("google-writer-a");
        ApiFactory.SignedInClient accountB = await host.Factory.CreateSignedInClientAsync("google-writer-b");
        string cookieA = PresentedCookieOf(accountA.Client);
        Guid sessionA = await SessionIdOfCookieAsync(host, cookieA);

        await using AsyncServiceScope requestScope = host.Factory.Services.CreateAsyncScope();
        CurrentUser currentUser = requestScope.ServiceProvider.GetRequiredService<CurrentUser>();
        currentUser.UserId = accountB.UserId;
        currentUser.BudgetId = accountB.BudgetId;

        DefaultHttpContext context = new() { RequestServices = requestScope.ServiceProvider };
        context.Request.Headers.Cookie = $"{SessionCookieAuthenticationTests.CookieName}={cookieA}";

        DateTime now = DateTime.UtcNow;
        Session establishedForB = Session.Establish(
            Credential.CreateFederated(accountB.UserId, Credential.GoogleProvider, "google-writer-b", now),
            now,
            now.AddDays(14));
        SessionHandoff handoff = SessionHandle.Mint().IssuedFor(establishedForB);
        SessionCookieWriter writer = host.Factory.Services.GetRequiredService<SessionCookieWriter>();

        // Act
        await writer.WriteEstablishedAsync(context, handoff, CancellationToken.None);

        // Assert — the request is still B's, in identity and budget.
        await Assert.That(currentUser.UserId).IsEqualTo(accountB.UserId);
        await Assert.That(currentUser.BudgetId).IsEqualTo(accountB.BudgetId);

        // The control: A's presented session really was displaced, and B's cookie was written.
        await Assert.That(await CountSessionsByIdAsync(host, sessionA)).IsEqualTo(0L);
        await Assert.That(context.Response.Headers.SetCookie.ToString())
            .StartsWith($"{SessionCookieAuthenticationTests.CookieName}={handoff.Token}");
    }

    /// <summary>The session handle a signed-in client presents, read back from its own header.</summary>
    private static string PresentedCookieOf(HttpClient client)
    {
        string prefix = $"{SessionCookieAuthenticationTests.CookieName}=";
        string header = client.DefaultRequestHeaders.GetValues("Cookie").Single();

        return header.StartsWith(prefix, StringComparison.Ordinal)
            ? header[prefix.Length..]
            : throw new InvalidOperationException("The client presents no session cookie.");
    }

    /// <summary>The id of the session a cookie opens, read through its handle on the admin connection.</summary>
    private static async Task<Guid> SessionIdOfCookieAsync(PostgresTestHost host, string cookie)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select session_id from session_tokens where token_hash = @digest", connection);
        command.Parameters.AddWithValue("digest", SessionToken.HashOf(Base64UrlText.Decode(cookie)));

        return await command.ExecuteScalarAsync() switch
        {
            Guid sessionId => sessionId,
            var unexpected => throw new InvalidOperationException(
                $"Expected the cookie's session id, got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<long> CountSessionsByIdAsync(PostgresTestHost host, Guid sessionId)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select count(*) from sessions where id = @id", connection);
        command.Parameters.AddWithValue("id", sessionId);

        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException($"Expected a count, got '{unexpected ?? "null"}'."),
        };
    }
}
