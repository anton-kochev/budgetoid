using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Domain.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// <c>GET /api/me/session</c>: what a caller learns about the session it holds — its kind, its expiry and
/// the account's own pending erasure — answered from a real request over the least-privilege role.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the route exists.</b> A locked tab that reloads has to learn what it is. <c>GET /api/me</c>
/// refuses a locked session with 403, and a client reading that as "signed out" sends somebody who is
/// signed in back to the front door. This route answers both kinds, and says which.
/// </para>
/// <para>
/// <b>Every session here is seeded through the database</b>, for the reason
/// <see cref="LockedSessionTests" /> gives, and <b>the clock is fixed before the host is built</b>, for the
/// reason <see cref="ErasureScheduleEndpointTests" /> gives: each seeded window is placed around that
/// instant, so nothing here is refused for an expiry it never meant to arrange.
/// </para>
/// <para>
/// <b>The kind's wire spelling is lowercase</b> — <c>"full"</c>, <c>"locked"</c> — the column's spelling,
/// which the three establishing legs already write with <c>JsonNamingPolicy.CamelCase.ConvertName</c>.
/// The serializer's own <c>JsonStringEnumConverter</c> carries no naming policy and would write
/// <c>"Full"</c>, so the literal here is what holds the route to the product's one spelling.
/// </para>
/// <para>
/// The cookie name and the client header are literals, for the reason
/// <see cref="SessionCookieAuthenticationTests" /> states once for all these files.
/// </para>
/// </remarks>
public sealed class SessionReadEndpointTests
{
    private const string SessionPath = "/api/me/session";
    private const string SchedulePath = "/api/me/erasure/schedule";

    [Test]
    public async Task SessionRead_ForALockedSession_AnswersLockedWithNoErasure()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11, LockedExpiresAtUtc);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, SessionPath, locked);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("locked");
        await Assert.That(ParseUtc(body["expiresAtUtc"]!.GetValue<string>())).IsEqualTo(LockedExpiresAtUtc);
        await Assert.That(body.ContainsKey("erasure")).IsTrue();
        await Assert.That(body["erasure"]).IsNull();
    }

    /// <summary>
    /// After a locked session files a schedule, the read answers the instant the schedule answered — and
    /// the one row stored.
    /// </summary>
    /// <remarks>
    /// Filed through the route rather than seeded, so the two answers a client sees for the one fact
    /// cannot drift apart: the POST's <c>takesEffectAtUtc</c> and this read's are the same instant.
    /// </remarks>
    [Test]
    public async Task SessionRead_AfterAScheduleIsFiled_AnswersItsInstant()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11, LockedExpiresAtUtc);
        HttpClient client = factory.CreateClient();
        HttpResponseMessage scheduled = await SendAsync(client, HttpMethod.Post, SchedulePath, locked);
        await Assert.That(scheduled.StatusCode).IsEqualTo(HttpStatusCode.OK);
        DateTime filed = ParseUtc((await ReadJsonObjectAsync(scheduled))["takesEffectAtUtc"]!.GetValue<string>());

        // Act
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, SessionPath, locked);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject body = await ReadJsonObjectAsync(response);
        DateTime read = ParseUtc(body["erasure"]!["takesEffectAtUtc"]!.GetValue<string>());
        await Assert.That(read).IsEqualTo(filed);
        await Assert.That(read).IsEqualTo(RequestInstant.AddDays(7));
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("locked");
    }

    /// <summary>
    /// A full session reads "full" and its own expiry, beside a locked session on the same account whose
    /// expiry differs.
    /// </summary>
    /// <remarks>
    /// The locked session is there so "its own expiry" is a claim: with one session on the account, a
    /// route answering the account's latest session — or any session at all — would pass.
    /// </remarks>
    [Test]
    public async Task SessionRead_ForAFullSession_AnswersFull()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, fill: 0x22, FullExpiresAtUtc);
        _ = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11, LockedExpiresAtUtc);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, SessionPath, full);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("full");
        await Assert.That(ParseUtc(body["expiresAtUtc"]!.GetValue<string>())).IsEqualTo(FullExpiresAtUtc);
        await Assert.That(body["erasure"]).IsNull();
    }

    /// <summary>
    /// A full session on an account holding a schedule reads that schedule's instant.
    /// </summary>
    /// <remarks>
    /// <b>The erasure is the account's, not the locked session's.</b> Only a locked session can file one,
    /// but a person who files it and then finds a passkey signs in full while the instant still stands —
    /// and that tab has to see it too. A route reading the schedule only for a locked session passes every
    /// other test in this file.
    /// </remarks>
    [Test]
    public async Task SessionRead_ForAFullSessionOnAnAccountHoldingASchedule_AnswersItsInstant()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        DateTime takesEffectAt = RequestInstant.AddDays(5);
        await SeedScheduleAsync(host, owner.UserId, takesEffectAt);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, fill: 0x22, FullExpiresAtUtc);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, SessionPath, full);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("full");
        await Assert.That(ParseUtc(body["erasure"]!["takesEffectAtUtc"]!.GetValue<string>()))
            .IsEqualTo(takesEffectAt);
    }

    /// <summary>
    /// The body is exactly three members, and the erasure object exactly one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The route is reachable by a caller who has proved nothing but a provider sign-in</b>, so what it
    /// may say is what <see cref="LockedSessionTests" /> argues for it and nothing more: no session id
    /// (a stable handle somebody will one day start accepting), no account or budget id, no address, no
    /// credential. A census of names cannot see a member it was never told about, so the set is compared
    /// whole — a member added under any name goes red here.
    /// </para>
    /// <para>
    /// Read twice, before and after a schedule is filed, because the erasure object exists only in the
    /// second read, and an extra member there would pass a check made on the first alone.
    /// </para>
    /// </remarks>
    [Test]
    public async Task SessionRead_CarriesExactlyTheDeclaredMembers()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11, LockedExpiresAtUtc);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage beforeSchedule = await SendAsync(client, HttpMethod.Get, SessionPath, locked);
        HttpResponseMessage scheduled = await SendAsync(client, HttpMethod.Post, SchedulePath, locked);
        HttpResponseMessage afterSchedule = await SendAsync(client, HttpMethod.Get, SessionPath, locked);

        // Assert — the precondition: every read answered and the schedule was filed.
        await Assert.That(beforeSchedule.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(scheduled.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(afterSchedule.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The top level, both reads. Sorted, so the assertion is about the set and not the order.
        JsonObject before = await ReadJsonObjectAsync(beforeSchedule);
        JsonObject after = await ReadJsonObjectAsync(afterSchedule);
        await Assert.That(MembersOf(before)).IsEqualTo("erasure, expiresAtUtc, kind");
        await Assert.That(MembersOf(after)).IsEqualTo("erasure, expiresAtUtc, kind");

        // The erasure object.
        JsonObject erasure = after["erasure"] as JsonObject
            ?? throw new InvalidOperationException("The erasure member is not a JSON object.");
        await Assert.That(MembersOf(erasure)).IsEqualTo("takesEffectAtUtc");

        // No Set-Cookie: reading the session neither ends nor renews it.
        await Assert.That(beforeSchedule.Headers.Contains("Set-Cookie")).IsFalse();
    }

    [Test]
    public async Task SessionRead_WithNoSession_Is401()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, SessionPath, token: null);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// An ended session is answered 401; a live one on the same account is answered 200.
    /// </summary>
    /// <remarks>
    /// <b>The route does not accept an ended session.</b> <c>AcceptsEndedSession</c> is the sign-out
    /// route's alone; a read that answered a revoked handle would tell its holder the sign-in it named was
    /// once real, and what kind it was. The live arm keeps this from passing on a route that answers 401
    /// to everyone — which, today, is every route that does not exist.
    /// </remarks>
    [Test]
    public async Task SessionRead_ForAnEndedSession_Is401()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        Guid federated = await host.FederatedCredentialIdAsync(owner.UserId);
        byte[] ended = await host.SeedSessionAsync(
            federated,
            0x33,
            SessionKind.Locked,
            RequestInstant.AddMinutes(-1),
            LockedExpiresAtUtc,
            revokedAtUtc: RequestInstant.AddSeconds(-30));
        byte[] live = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11, LockedExpiresAtUtc);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage endedResponse = await SendAsync(client, HttpMethod.Get, SessionPath, ended);
        HttpResponseMessage liveResponse = await SendAsync(client, HttpMethod.Get, SessionPath, live);

        // Assert — the control first.
        await Assert.That(liveResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(endedResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Another account's pending erasure is not this account's: A reads <c>null</c> while B, holding a
    /// schedule, reads its own instant.
    /// </summary>
    /// <remarks>
    /// <c>erasure_schedules</c> is policed on its owner, and the read is expected to go through that
    /// policy rather than around it. B's arm is the control — without it, a route answering
    /// <c>null</c> to everybody passes.
    /// </remarks>
    [Test]
    public async Task SessionRead_DoesNotAnswerAnotherAccountsSchedule()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        RepositoryTestHost.SeededOwner other = await host.SeedOwnerAsync(OtherSubject, OtherEmail);
        DateTime otherTakesEffectAt = RequestInstant.AddDays(4);
        await SeedScheduleAsync(host, other.UserId, otherTakesEffectAt);
        byte[] ownerLocked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11, LockedExpiresAtUtc);
        byte[] otherLocked = await SeedLockedSessionAsync(host, other.UserId, fill: 0x44, LockedExpiresAtUtc);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage ownerResponse = await SendAsync(client, HttpMethod.Get, SessionPath, ownerLocked);
        HttpResponseMessage otherResponse = await SendAsync(client, HttpMethod.Get, SessionPath, otherLocked);

        // Assert — the control: the other account reads its own schedule.
        await Assert.That(otherResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject otherBody = await ReadJsonObjectAsync(otherResponse);
        await Assert.That(ParseUtc(otherBody["erasure"]!["takesEffectAtUtc"]!.GetValue<string>()))
            .IsEqualTo(otherTakesEffectAt);

        // The rule.
        await Assert.That(ownerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject ownerBody = await ReadJsonObjectAsync(ownerResponse);
        await Assert.That(ownerBody.ContainsKey("erasure")).IsTrue();
        await Assert.That(ownerBody["erasure"]).IsNull();
    }

    private const string OwnerSubject = "google-session-read-owner";
    private const string OwnerEmail = "session-read-owner@budgetoid.test";
    private const string OtherSubject = "google-session-read-other";
    private const string OtherEmail = "session-read-other@budgetoid.test";

    /// <summary>
    /// The instant every request here is served at. Whole seconds, so the column's microsecond precision
    /// rounds nothing away and an instant read back is the instant computed.
    /// </summary>
    private static readonly DateTime RequestInstant = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>A locked session's expiry, distinct from the full one's so neither can answer for the other.</summary>
    private static readonly DateTime LockedExpiresAtUtc = RequestInstant.AddMinutes(47);

    /// <summary>A full session's expiry.</summary>
    private static readonly DateTime FullExpiresAtUtc = RequestInstant.AddMinutes(83);

    private static ApiFactory CreateFactory(RepositoryTestHost host, FakeTimeProvider clock) =>
        SessionCookieAuthenticationTests.CreateApiFactory(
            host,
            services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));

    /// <summary>
    /// Sends one request carrying the first-party client header and, optionally, the session cookie. No
    /// body: neither route here takes one.
    /// </summary>
    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        byte[]? token)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Add(FirstPartyRequestTests.ClientHeader, FirstPartyRequestTests.ClientHeaderValue);
        if (token is not null)
        {
            request.Headers.Add(
                "Cookie",
                $"{SessionCookieAuthenticationTests.CookieName}={Base64UrlText.Encode(token)}");
        }

        return client.SendAsync(request);
    }

    /// <summary>A live session on the account's federated credential, from a minute before the request.</summary>
    private static async Task<byte[]> SeedLockedSessionAsync(
        RepositoryTestHost host,
        Guid userId,
        byte fill,
        DateTime expiresAtUtc) =>
        await host.SeedSessionAsync(
            await host.FederatedCredentialIdAsync(userId),
            fill,
            SessionKind.Locked,
            RequestInstant.AddMinutes(-1),
            expiresAtUtc);

    /// <summary>A live session on a freshly seeded passkey, from a minute before the request.</summary>
    private static async Task<byte[]> SeedFullSessionAsync(
        RepositoryTestHost host,
        Guid userId,
        byte fill,
        DateTime expiresAtUtc)
    {
        Guid credentialId = await host.SeedPasskeyAsync(userId, [.. Enumerable.Repeat(fill, 16)]);
        return await host.SeedSessionAsync(
            credentialId, fill, SessionKind.Full, RequestInstant.AddMinutes(-1), expiresAtUtc);
    }

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

    private static string MembersOf(JsonObject body) =>
        string.Join(", ", body.Select(member => member.Key).Order(StringComparer.Ordinal));

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject
        ?? throw new InvalidOperationException("The response body is not a JSON object.");

    private static DateTime ParseUtc(string text)
    {
        DateTime parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return parsed.Kind == DateTimeKind.Utc
            ? parsed
            : throw new FormatException($"'{text}' is not a UTC instant.");
    }
}
