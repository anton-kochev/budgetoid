using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Domain.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// <c>POST /api/me/erasure/schedule</c>: the one act a locked session may perform, answered from a real
/// request over the least-privilege role — what it files, what it answers, whom it refuses, and what it
/// leaves alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every session here is seeded through the database</b>, for the reason <see cref="LockedSessionTests" />
/// gives: nothing in the product establishes a locked session yet, so a suite waiting for a sign-in to
/// produce one would never reach this route at all.
/// </para>
/// <para>
/// <b>The clock is fixed before the host is built</b>, so "seven days out" is an instant this file chose
/// rather than whatever the run took, and every seeded session's window is placed around that instant —
/// a session seeded against the wall clock is judged against the fake one and answers 401 for a reason
/// no assertion names (see <c>RepositoryTestHost.SeedSignedInOwnerOnAsync</c>). The seven days are a
/// literal, never <c>ErasurePolicy.Delay</c>, for the reason <c>EstablishedSessionLifetimeTests</c>
/// gives about the session lifetime.
/// </para>
/// <para>
/// <b>Both refusals on this path can be 403</b>, so every request carries the first-party client header
/// and every 403 here is checked against <see cref="FirstPartyRequestMiddleware.Title" />. The cookie
/// name and the header are literals, for the reason <see cref="SessionCookieAuthenticationTests" />
/// states once for all these files.
/// </para>
/// <para>
/// Store reads go over <see cref="RepositoryTestHost.ConnectionString" />, the superuser, because
/// <c>erasure_schedules</c> is policed on its owner and a policed read reports an absent row exactly as
/// it reports a hidden one.
/// </para>
/// </remarks>
public sealed class ErasureScheduleEndpointTests
{
    private const string SchedulePath = "/api/me/erasure/schedule";
    private const string AccountsPath = "/api/accounts";

    /// <summary>
    /// A locked session is answered 200 with an instant seven days after the request, that instant is the
    /// one row filed, and nothing else about the account moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A schedule is not an erasure.</b> The account, its budget, its content, its credentials and its
    /// sessions are all counted either side of the request, and the session that asked is still live
    /// afterwards: a route that erased at once and answered a date would satisfy every assertion about the
    /// date.
    /// </para>
    /// <para>
    /// <b>The body is exactly one member.</b> It names no account, session, credential or budget — a
    /// caller who has proved nothing but a provider sign-in learns the date and nothing else.
    /// </para>
    /// <para>
    /// <b>No <c>Set-Cookie</c>.</b> Scheduling ends nothing and begins nothing, so the cookie the browser
    /// holds is the one it keeps.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ScheduleErasure_FromALockedSession_Answers200SevenDaysOut_AndErasesNothing()
    {
        // Arrange — an account with a row of budget content, and one live locked session on it.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        await SeedAccountAsync(host, owner.BudgetId, OwnerAccountName);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();
        IReadOnlyDictionary<string, long> before = await CountAccountRowsAsync(host);

        // Act
        HttpResponseMessage response = await SendAsync(client, SchedulePath, locked);

        // Assert — the answer.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(string.Join(", ", body.Select(member => member.Key))).IsEqualTo("takesEffectAtUtc");
        await Assert.That(ParseUtc(body["takesEffectAtUtc"]!.GetValue<string>()))
            .IsEqualTo(RequestInstant.AddDays(7));
        await Assert.That(response.Headers.Contains("Set-Cookie")).IsFalse();

        // The one row it claims to report.
        await Assert.That(await StoredInstantsAsync(host)).IsEquivalentTo(new[]
        {
            (owner.UserId, RequestInstant.AddDays(7)),
        });

        // Nothing erased, nothing ended.
        IReadOnlyDictionary<string, long> after = await CountAccountRowsAsync(host);
        await Assert.That(Render(after)).IsEqualTo(Render(before));
        await Assert.That(after["sessions"]).IsGreaterThan(0);
        await Assert.That(await CountRevokedSessionsAsync(host)).IsEqualTo(0);
    }

    /// <summary>
    /// A full session on the same account is refused 403, files nothing, and is refused with the body
    /// every other refusal of a session's kind carries; a locked session on that account then succeeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pairing is the test.</b> A gate refusing everybody satisfies the refusal; a route with no
    /// gate at all satisfies the success. One account, one host, two sessions differing only in the
    /// credential that opened them, is what makes the refusal a verdict on the session's kind — and the
    /// full arm runs first, so the locked arm's 200 is not the reason the full arm found something filed.
    /// </para>
    /// <para>
    /// <b>The refusal is compared whole against a locked session refused <c>GET /api/accounts</c></b>, the
    /// refusal <see cref="LockedSessionTests.EveryRefusalOfALockedSession_IsTheSameAnswer" /> pins. A
    /// refusal carrying a title of its own would tell a caller which gate answered, and therefore which
    /// kind of session the handle it presented opened.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ScheduleErasure_FromAFullSession_IsRefused403_WhileALockedSessionOnTheSameAccountSucceeds()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, fill: 0x22);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act — the full session first.
        HttpResponseMessage fullResponse = await SendAsync(client, SchedulePath, full);
        DateTime[] filedAfterTheFullSession = [.. (await StoredInstantsAsync(host)).Select(row => row.TakesEffectAtUtc)];
        HttpResponseMessage lockedResponse = await SendAsync(client, SchedulePath, locked);
        HttpResponseMessage lockedRefusal = await SendAsync(client, AccountsPath, locked, HttpMethod.Get);

        // Assert — the control: the same route answers a locked session on this account.
        await Assert.That(lockedResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The refusal, filing nothing.
        await Assert.That(fullResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(filedAfterTheFullSession).IsEmpty();

        // And it is the same answer as every other refusal of a session's kind — not the CSRF control's.
        await Assert.That(lockedRefusal.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string fullBody = await ComparableBodyOfAsync(fullResponse);
        await Assert.That(fullBody).IsEqualTo(await ComparableBodyOfAsync(lockedRefusal));
        await Assert.That(fullBody).DoesNotContain(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// A second request after the clock has moved answers the first request's instant, and the table
    /// still holds one row carrying it.
    /// </summary>
    /// <remarks>
    /// <b>A repeat never moves the date.</b> A window that slid forward on every visit would never close,
    /// which is exactly what somebody holding a stolen provider account would want. Ten minutes is
    /// inside the seeded session's hour, so the second request is refused for nothing but what is under
    /// test.
    /// </remarks>
    [Test]
    public async Task ScheduleErasure_CalledTwice_AnswersTheFirstInstant_AndStoresOneRow()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage first = await SendAsync(client, SchedulePath, locked);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(10));

        // Act
        HttpResponseMessage second = await SendAsync(client, SchedulePath, locked);

        // Assert
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        DateTime firstInstant = ParseUtc((await ReadJsonObjectAsync(first))["takesEffectAtUtc"]!.GetValue<string>());
        DateTime secondInstant = ParseUtc((await ReadJsonObjectAsync(second))["takesEffectAtUtc"]!.GetValue<string>());
        await Assert.That(firstInstant).IsEqualTo(RequestInstant.AddDays(7));
        await Assert.That(secondInstant).IsEqualTo(firstInstant);
        await Assert.That(await StoredInstantsAsync(host)).IsEquivalentTo(new[]
        {
            (owner.UserId, RequestInstant.AddDays(7)),
        });
    }

    /// <summary>
    /// A locked session that has ended is answered 401 and files nothing; a live locked session on the
    /// same account is answered 200.
    /// </summary>
    /// <remarks>
    /// <b>The route does not accept an ended session.</b> <c>AcceptsEndedSession</c> is opt-in and the
    /// sign-out route is the only one carrying it, so a revoked handle is a caller with no live session
    /// — the fallback policy's 401, before the handler runs. The live arm is what keeps this from passing
    /// on a route that answered 401 to everyone.
    /// </remarks>
    [Test]
    public async Task ScheduleErasure_FromAnEndedLockedSession_Is401()
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
            RequestInstant.AddHours(1),
            revokedAtUtc: RequestInstant.AddSeconds(-30));
        byte[] live = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act — the ended session first, so nothing the live one files can be what it found.
        HttpResponseMessage endedResponse = await SendAsync(client, SchedulePath, ended);
        int filedAfterTheEndedSession = (await StoredInstantsAsync(host)).Length;
        HttpResponseMessage liveResponse = await SendAsync(client, SchedulePath, live);

        // Assert
        await Assert.That(liveResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(endedResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(filedAfterTheEndedSession).IsEqualTo(0);
    }

    /// <summary>
    /// A request carrying no session at all is answered 401 and files nothing.
    /// </summary>
    [Test]
    public async Task ScheduleErasure_WithNoSession_Is401()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, SchedulePath, token: null);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await StoredInstantsAsync(host)).IsEmpty();
    }

    /// <summary>
    /// Scheduling one account's erasure files nothing for another, moves nothing another already holds,
    /// and erases nothing of either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The counterweight to every other test here</b>, in the role
    /// <c>AccountErasureEndpointTests.Erase_LeavesAnotherAccountUntouched</c> plays for the immediate
    /// erasure: a route that filed a schedule for every account in the table, or rewrote one it found,
    /// would satisfy every assertion about the account that asked.
    /// </para>
    /// <para>
    /// <b>The survivor already holds a schedule, three days old.</b> Its date is one the asking account's
    /// cannot equal, so a request that rewrote "the" schedule shows up as a moved instant rather than as
    /// an accident of timing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ScheduleErasure_LeavesAnotherAccountUntouched()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(RequestInstant));
        await using ApiFactory factory = CreateFactory(host, clock);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        RepositoryTestHost.SeededOwner survivor = await host.SeedOwnerAsync(SurvivorSubject, SurvivorEmail);
        await SeedAccountAsync(host, survivor.BudgetId, SurvivorAccountName);
        DateTime survivorTakesEffectAt = RequestInstant.AddDays(4);
        await SeedScheduleAsync(host, survivor.UserId, survivorTakesEffectAt);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        byte[] survivorLocked = await SeedLockedSessionAsync(host, survivor.UserId, fill: 0x44);
        HttpClient client = factory.CreateClient();
        IReadOnlyDictionary<string, long> survivorBefore = await CountRowsOfAsync(host, survivor);

        // Act
        HttpResponseMessage response = await SendAsync(client, SchedulePath, locked);

        // Assert — the asking account's row, and the survivor's exactly as it was.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StoredInstantsAsync(host)).IsEquivalentTo(new[]
        {
            (owner.UserId, RequestInstant.AddDays(7)),
            (survivor.UserId, survivorTakesEffectAt),
        });

        // Nothing of the survivor's moved, and its session still reaches the route.
        await Assert.That(Render(await CountRowsOfAsync(host, survivor))).IsEqualTo(Render(survivorBefore));
        HttpResponseMessage survivorRepeat = await SendAsync(client, SchedulePath, survivorLocked);
        await Assert.That(survivorRepeat.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ParseUtc((await ReadJsonObjectAsync(survivorRepeat))["takesEffectAtUtc"]!.GetValue<string>()))
            .IsEqualTo(survivorTakesEffectAt);
    }

    private const string OwnerSubject = "google-erasure-schedule-owner";
    private const string OwnerEmail = "erasure-schedule-owner@budgetoid.test";
    private const string OwnerAccountName = "Owners Current Account";
    private const string SurvivorSubject = "google-erasure-schedule-survivor";
    private const string SurvivorEmail = "erasure-schedule-survivor@budgetoid.test";
    private const string SurvivorAccountName = "Survivors Current Account";
    private const string TraceIdMember = "traceId";

    /// <summary>
    /// The instant every request here is served at. Whole seconds, so the column's microsecond precision
    /// rounds nothing away and an instant read back is the instant computed.
    /// </summary>
    private static readonly DateTime RequestInstant = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// The tables a schedule must leave exactly as it found them — the account and everything its
    /// existence is made of — counted whole, because each test owns its own database.
    /// </summary>
    private static readonly string[] AccountTables =
    [
        "users", "budgets", "accounts", "credentials", "sessions", "session_tokens", "factor_manifests",
    ];

    private static ApiFactory CreateFactory(RepositoryTestHost host, FakeTimeProvider clock) =>
        SessionCookieAuthenticationTests.CreateApiFactory(
            host,
            services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));

    /// <summary>
    /// Sends one request carrying the first-party client header and, optionally, the session cookie. A
    /// POST carries no body: the route takes none.
    /// </summary>
    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string path,
        byte[]? token,
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

        return client.SendAsync(request);
    }

    /// <summary>
    /// A live session opened by the account's federated credential, its window placed around
    /// <see cref="RequestInstant" />.
    /// </summary>
    private static async Task<byte[]> SeedLockedSessionAsync(RepositoryTestHost host, Guid userId, byte fill) =>
        await host.SeedSessionAsync(
            await host.FederatedCredentialIdAsync(userId),
            fill,
            SessionKind.Locked,
            RequestInstant.AddMinutes(-1),
            RequestInstant.AddHours(1));

    /// <summary>
    /// A live session opened by a freshly seeded passkey on the account, its window placed around
    /// <see cref="RequestInstant" />.
    /// </summary>
    private static async Task<byte[]> SeedFullSessionAsync(RepositoryTestHost host, Guid userId, byte fill)
    {
        Guid credentialId = await host.SeedPasskeyAsync(userId, [.. Enumerable.Repeat(fill, 16)]);
        return await host.SeedSessionAsync(
            credentialId,
            fill,
            SessionKind.Full,
            RequestInstant.AddMinutes(-1),
            RequestInstant.AddHours(1));
    }

    /// <summary>
    /// One row of budget content, in raw SQL on the superuser connection, in the shape
    /// <see cref="LockedSessionTests" /> seeds it: sealed name and blind index both, because the columns
    /// are <c>bytea</c> and <c>NOT NULL</c>.
    /// </summary>
    private static async Task SeedAccountAsync(RepositoryTestHost host, Guid budgetId, string name)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            insert into accounts (id, budget_id, name, name_key, type, opening_balance, currency_code, created_at_utc)
            values (@id, @budget_id, @name, @name_key, 'Checking', 0, 'USD', @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("budget_id", budgetId);
        command.Parameters.AddWithValue("name", SealedNarrative.Name(name).Envelope.ToArray());
        command.Parameters.AddWithValue("name_key", SealedNarrative.BlindIndex(name).ToArray());
        command.Parameters.AddWithValue("created_at_utc", RequestInstant.AddDays(-30));

        if (await command.ExecuteNonQueryAsync() is not 1)
        {
            throw new InvalidOperationException("Seeding an account wrote something other than one row.");
        }
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
        await command.ExecuteNonQueryAsync();
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

    /// <summary>The survivor's own rows, by owner, so the asking account's rows do not answer for them.</summary>
    private static async Task<IReadOnlyDictionary<string, long>> CountRowsOfAsync(
        RepositoryTestHost host,
        RepositoryTestHost.SeededOwner owner)
    {
        (string Table, string Sql)[] queries =
        [
            ("users", "select count(*) from users where id = @user_id"),
            ("budgets", "select count(*) from budgets where user_id = @user_id"),
            ("accounts", "select count(*) from accounts where budget_id = @budget_id"),
            ("credentials", "select count(*) from credentials where user_id = @user_id"),
            ("sessions", "select count(*) from sessions where user_id = @user_id and revoked_at_utc is null"),
            ("factor_manifests", "select count(*) from factor_manifests where user_id = @user_id"),
        ];

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach ((string table, string sql) in queries)
        {
            await using NpgsqlCommand command = new(sql, connection);
            command.Parameters.AddWithValue("user_id", owner.UserId);
            command.Parameters.AddWithValue("budget_id", owner.BudgetId);
            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }

        return counts;
    }

    private static async Task<long> CountRevokedSessionsAsync(RepositoryTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select count(*) from sessions where revoked_at_utc is not null", connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Counts rendered as one line, so a failure names the table that moved instead of reporting that two
    /// dictionaries differ.
    /// </summary>
    private static string Render(IReadOnlyDictionary<string, long> counts) =>
        string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

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

    /// <summary>
    /// The whole response body with the per-request <c>traceId</c> replaced, in the shape
    /// <see cref="LockedSessionTests" /> compares refusals by: replaced rather than removed, so a member
    /// appearing, disappearing or differing still fails. A body that is not JSON is returned verbatim.
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
