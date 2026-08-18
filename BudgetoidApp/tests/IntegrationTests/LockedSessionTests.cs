using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// What a session opened by a federated credential may reach, and what it may not: FR-109, read off
/// real requests rather than off the claim the cookie handler already publishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every refusal here is paired with the accepting arm of the same arrangement.</b> A policy that
/// refused everybody satisfies "a locked session is refused the accounts list" perfectly, and so does an
/// application whose database happens to be empty. The <see cref="SessionKind.Full" /> session seeded on
/// the same host, against the same route, over the same seeded content is what turns each refusal into a
/// verdict on the session's kind.
/// </para>
/// <para>
/// <b>The gate is unreachable from any live route today, and that is why every session here is seeded
/// through the database.</b> The only credential type that opens a locked session is
/// <see cref="CredentialType.Federated" />, and the federated path mints no session cookie — so a suite
/// that waited for a sign-in to produce one would be a suite that never exercised the gate at all. That
/// is exactly how a gate ships broken and green.
/// </para>
/// <para>
/// <b>The trap this file is written around: both refusals on this path are 403.</b>
/// <see cref="FirstPartyRequestMiddleware" /> answers 403 to a request without the client header, before
/// anything looks at a cookie. Every request below therefore carries that header, and the sweep asserts
/// on the body rather than on the status alone — a 403 whose title is
/// <see cref="FirstPartyRequestMiddleware.Title" /> is the CSRF control answering, and reading it as a
/// locked-session refusal would leave this whole file green against an application with no gate in it.
/// </para>
/// <para>
/// The cookie name and the client header are literals, for the reason
/// <see cref="SessionCookieAuthenticationTests" /> states once for all these files.
/// </para>
/// </remarks>
public sealed class LockedSessionTests
{
    private const string AccountsPath = "/api/accounts";
    private const string MePath = "/api/me";
    private const string ExportPath = "/api/me/export";
    private const string CredentialsPath = "/api/me/credentials";
    private const string ErasurePath = "/api/me/erasure";
    private const string RevocationPath = "/api/me/session/revocation";

    /// <summary>
    /// That budget content is refused to a locked session and served to a full one, on one host, from
    /// one seeded row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The accounts list is the sharpest probe of FR-109 there is</b>: it is budget content, filtered
    /// by the ambient budget the cookie published, and a locked session reaching it would be a federated
    /// sign-in reading rows whose keys the provider's holder cannot possibly hold.
    /// </para>
    /// <para>
    /// <b>Both sessions belong to the same account.</b> Two accounts would leave "refused" explainable by
    /// the tenant filter matching nothing — which is a green test over an application with no gate. One
    /// account, one budget, one seeded row means the only thing that differs between the two requests is
    /// the credential type the session was established from.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ALockedSession_IsRefusedBudgetContent()
    {
        // Arrange — one account with one budget and one row of content in it, then two live sessions on
        // that same account: one opened by its federated credential, one by a passkey.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        await SeedAccountAsync(host, owner.BudgetId, OwnerAccountName);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, fill: 0x22);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage lockedAccounts = await SendAsync(client, HttpMethod.Get, AccountsPath, locked);
        HttpResponseMessage fullAccounts = await SendAsync(client, HttpMethod.Get, AccountsPath, full);

        // Assert — the control first: the same route, the same row, a session whose credential can hold
        // the account's keys. Without this arm a policy refusing everybody passes, and asserting it
        // first is what stops a broken control being hidden behind the refusal it exists to qualify.
        await Assert.That(fullAccounts.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await fullAccounts.Content.ReadAsStringAsync()).Contains(OwnerAccountName);

        // The refusal, and that it is this gate's refusal rather than the CSRF control's.
        await Assert.That(lockedAccounts.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await TitleOfAsync(lockedAccounts))
            .IsNotEqualTo(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// That the export is refused to a locked session, and served to a full one.
    /// </summary>
    /// <remarks>
    /// <b>Its own test rather than one more row of the sweep, because the export is the one read that a
    /// budget-keyed gate would let through.</b> It reads <c>budgets</c> scoped by <c>user_id</c> — not by
    /// the ambient budget — so an implementation that refused a locked session by publishing no tenant,
    /// rather than by refusing the request, would answer this route perfectly while every other route in
    /// the sweep failed. The whole account leaving in one file is also the largest single disclosure the
    /// product has.
    /// </remarks>
    [Test]
    public async Task ALockedSession_IsRefusedTheExport()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        await SeedAccountAsync(host, owner.BudgetId, OwnerAccountName);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        byte[] full = await SeedFullSessionAsync(host, owner.UserId, fill: 0x22);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage lockedExport = await SendAsync(client, HttpMethod.Get, ExportPath, locked);
        HttpResponseMessage fullExport = await SendAsync(client, HttpMethod.Get, ExportPath, full);

        // Assert — the control first, for the reason the test above states. It does two jobs here: the
        // route answers a full session, and the bytes it answers with are the account's own, so the
        // refusal below is not the export refusing everybody over a budget set it could not read.
        await Assert.That(fullExport.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await fullExport.Content.ReadAsStringAsync()).Contains(OwnerAccountName);

        // The refusal.
        await Assert.That(lockedExport.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await TitleOfAsync(lockedExport)).IsNotEqualTo(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// That a locked session may still end itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The opt-out, and the only one argued for.</b> A person signed in with a provider has to be able
    /// to sign out; a gate that refused the sign-out would leave a locked cookie on the client with no
    /// way to shed it but waiting for expiry.
    /// </para>
    /// <para>
    /// <b>It is also the second control on "the policy does not refuse everything".</b> This one is green
    /// before the gate exists, which is the point: it is what goes red if the opt-out is forgotten, and
    /// it says nothing at all while the fallback policy still admits everybody.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ALockedSession_MaySignOut()
    {
        // Arrange
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, RevocationPath, locked);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// Every route a locked session may reach, and the argument for each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>POST /api/me/session/revocation</c> — signing out. A person who signed in with the identity
    /// provider is signed in; the gate decides what they may <em>read</em>, and shedding the cookie is not
    /// a read. Refusing it would leave a locked session with no way to end itself but waiting out its
    /// expiry, on a client that has already been told it is signed in. The route reads no budget content
    /// and no account data at all: it ends the session named by the caller's own authentication and
    /// clears the cookie.
    /// </para>
    /// <para>
    /// The method is part of the entry rather than decoration, for the reason
    /// <see cref="AcceptsEndedSessionTests" /> gives about its own set: a marker on a route group would
    /// admit a locked session to every verb over that prefix, including the ones added afterwards.
    /// </para>
    /// <para>
    /// A route added here needs its own paragraph, and the paragraph is the review. Two questions have to
    /// be answered in it: what a caller who has proved nothing but a provider sign-in learns from the
    /// route, and what the route would read if it ever reached budget content.
    /// </para>
    /// </remarks>
    private static readonly string[] RoutesALockedSessionMayReach =
    [
        $"POST {RevocationPath}",
    ];

    /// <summary>
    /// The opted-out surface is exactly the route argued for above — no more, and no fewer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-out is the direction that needs this test, far more than opt-in did.</b> A forgotten opt-in
    /// marker fails closed and loudly; a forgotten opt-out costs a 403 somebody notices in a day. This
    /// marker runs the other way: it is the only thing standing between a locked session and a route, so
    /// one added without an argument is budget content handed to a provider sign-in, and nothing else in
    /// the suite would go red for it. Reading the set whole off the route table is what makes adding one
    /// a decision somebody signs.
    /// </para>
    /// <para>
    /// <b>Structural rather than behavioural</b>, for <see cref="AnonymousSurfaceTests" />' reason: a
    /// sweep of requests could only report what happened on the routes it thought to try, while the
    /// permission is a property of the route table and has to be readable whole.
    /// </para>
    /// <para>
    /// <b>The two counts at the end are the controls.</b> The comparison is satisfied by an empty
    /// enumeration in exactly one case — an empty expectation — so the marker has to be found somewhere
    /// and the route table has to have been populated at all, or a metadata lookup coming back null for
    /// every endpoint would pass while proving nothing.
    /// </para>
    /// <para>
    /// Production, and it needs no database: reading the route table opens no connection, and the
    /// Development-only OpenAPI document carries no marker of this kind either way.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryRouteALockedSessionMayReach_IsTheOneArguedFor()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        string[] marked = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<AllowsLockedSessionAttribute>() is not null)
            .Select(endpoint =>
                $"{MethodsOf(endpoint)} {endpoint.RoutePattern.RawText ?? string.Empty}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — joined rather than compared as collections, so a failure names the route that moved
        // instead of reporting that two sets differ.
        await Assert.That(string.Join(", ", marked))
            .IsEqualTo(string.Join(", ", RoutesALockedSessionMayReach.Order(StringComparer.Ordinal)));

        // The controls: the marker exists on this route table, and the route table was really read.
        await Assert.That(marked.Length).IsGreaterThan(0);
        await Assert.That(endpoints.Length).IsGreaterThan(marked.Length);
    }

    /// <summary>
    /// The verbs an endpoint answers, spelled the way the expectation above writes them.
    /// </summary>
    /// <remarks>
    /// Ordered and joined rather than taken as "the first one": a route that gained a second verb
    /// alongside the marker is a route whose opted-out surface grew, and reporting only the first would
    /// hide it.
    /// </remarks>
    private static string MethodsOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is { HttpMethods: { Count: > 0 } methods }
            ? string.Join("|", methods.Order(StringComparer.Ordinal))
            : "ANY";

    /// <summary>
    /// That every route a locked session is refused answers the identical body, naming nothing about why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One answer, on purpose.</b> A refusal carrying a title of its own would tell a caller holding a
    /// provider token which of the two 403s on this path answered — the CSRF control or the gate — and
    /// therefore that the handle it presented authenticated at all. The five routes are swept together
    /// rather than asserted one at a time so that a route answering differently is visible as a
    /// difference rather than as five separate green tests.
    /// </para>
    /// <para>
    /// <b>Three things are asserted about the body, and none of them is implied by the others.</b> That
    /// it is the same on every route; that it names no session, credential or kind; and that it is
    /// <em>not</em> the body <see cref="FirstPartyRequestMiddleware" /> writes — which is the one 403 a
    /// broken gate would leave this test reading. The CSRF title carries the word this file most needs
    /// absent, so "names nothing" would not catch it on its own.
    /// </para>
    /// <para>
    /// The answers are joined into one string before comparison, so a failure names the route that
    /// differed instead of stopping at the first one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryRefusalOfALockedSession_IsTheSameAnswer()
    {
        // Arrange — an account with content in it, so a refusal is a refusal rather than an empty read.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        await SeedAccountAsync(host, owner.BudgetId, OwnerAccountName);
        byte[] locked = await SeedLockedSessionAsync(host, owner.UserId, fill: 0x11);
        HttpClient client = factory.CreateClient();

        // Act
        List<string> statuses = [];
        List<string> bodies = [];
        foreach ((HttpMethod method, string path) in RefusedRoutes)
        {
            HttpResponseMessage response = await SendAsync(client, method, path, locked);
            statuses.Add($"{method.Method} {path} -> {(int)response.StatusCode}");
            bodies.Add($"{method.Method} {path} -> {await ComparableBodyOfAsync(response)}");
        }

        // Assert — every route refused.
        await Assert.That(string.Join("\n", statuses)).IsEqualTo(string.Join(
            "\n",
            RefusedRoutes.Select(route => $"{route.Method.Method} {route.Path} -> 403")));

        // And every one of them with the same body. Compared against the first rather than pairwise, so
        // the failure reads as "this route differs from the others" rather than as a set mismatch.
        string first = bodies[0][(bodies[0].IndexOf("-> ", StringComparison.Ordinal) + 3)..];
        await Assert.That(string.Join("\n", bodies)).IsEqualTo(string.Join(
            "\n",
            RefusedRoutes.Select(route => $"{route.Method.Method} {route.Path} -> {first}")));

        // The body says nothing about why, and — critically — it is not the CSRF control's 403, which is
        // the other refusal on this path and the one a test reading only the status would mistake it for.
        foreach (string forbidden in WordsNoRefusalMayCarry)
        {
            await Assert.That(first.Contains(forbidden, StringComparison.OrdinalIgnoreCase)).IsFalse();
        }

        await Assert.That(first).DoesNotContain(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// The routes the sweep above refuses, spanning every shape of read the product serves a signed-in
    /// caller: budget content, the principal itself, the whole-account export, the credential list, and
    /// the one destructive route.
    /// </summary>
    private static readonly (HttpMethod Method, string Path)[] RefusedRoutes =
    [
        (HttpMethod.Get, AccountsPath),
        (HttpMethod.Get, MePath),
        (HttpMethod.Get, ExportPath),
        (HttpMethod.Get, CredentialsPath),
        (HttpMethod.Post, ErasurePath),
    ];

    /// <summary>
    /// Words a refusal may not carry. Each names something the caller would learn from seeing it: that a
    /// session exists, that it was opened by some particular credential, or that sessions come in kinds.
    /// </summary>
    private static readonly string[] WordsNoRefusalMayCarry = ["session", "credential", "kind", "locked"];

    private const string OwnerSubject = "google-locked-session-owner";
    private const string OwnerEmail = "locked-session-owner@budgetoid.test";

    /// <summary>The seeded budget content, whose presence in a body is what "reached it" means.</summary>
    private const string OwnerAccountName = "Owners Current Account";

    /// <summary>
    /// Sends one request carrying the first-party client header and, optionally, the session cookie.
    /// </summary>
    /// <remarks>
    /// The header goes on every request because the CSRF control refuses one without it before anything
    /// looks at the cookie — see <see cref="FirstPartyRequestTests" />, which owns that claim. A request
    /// here that omitted it would be answered 403 by that control, and this whole file reads 403 as its
    /// own result.
    /// </remarks>
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

        // An empty JSON object on every POST. The erasure route binds a body, and a request without one
        // is answered 400 by model binding — which happens after authorization, but only on an
        // application where authorization refused nothing, so an absent body would turn this file's one
        // destructive route into a 400 the moment the gate started working.
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return client.SendAsync(request);
    }

    /// <summary>
    /// Seeds a live session opened by the account's <b>federated</b> credential — a
    /// <see cref="SessionKind.Locked" /> one — and returns the handle the cookie carries.
    /// </summary>
    /// <remarks>
    /// The credential is the one <see cref="RepositoryTestHost.SeedUserAsync" /> already wrote, read back
    /// rather than created, because an account holds exactly one federated credential and a second would
    /// be a state provisioning cannot produce. The kind is asserted rather than assumed: if
    /// <c>Session.Establish</c> ever stopped deriving <see cref="SessionKind.Locked" /> from a federated
    /// credential, every test in this file would go on passing against a full session it never meant to
    /// seed.
    /// </remarks>
    private static async Task<byte[]> SeedLockedSessionAsync(
        RepositoryTestHost host,
        Guid userId,
        byte fill)
    {
        await using BudgetoidDbContext db = CreateDb(host);
        Credential federated = await db.Credentials.SingleAsync(stored =>
            stored.UserId == userId && stored.Type == CredentialType.Federated);

        return await SeedSessionAsync(host, federated.Id, fill, SessionKind.Locked);
    }

    /// <summary>
    /// Seeds a live session opened by a freshly registered passkey — a <see cref="SessionKind.Full" />
    /// one — and returns the handle the cookie carries.
    /// </summary>
    private static async Task<byte[]> SeedFullSessionAsync(
        RepositoryTestHost host,
        Guid userId,
        byte fill)
    {
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId(fill));
        return await SeedSessionAsync(host, credentialId, fill, SessionKind.Full);
    }

    /// <summary>
    /// Establishes one session on an existing credential, files the handle it is presented by, and
    /// returns the token itself — which exists nowhere but here and the cookie.
    /// </summary>
    /// <remarks>
    /// The session and its handle go in one <c>SaveChangesAsync</c>, which is the shape the establishing
    /// path writes them in: a handle committed without its session names nothing.
    /// <paramref name="expectedKind" /> is checked against what the domain derived, so a seeding call
    /// cannot quietly produce the opposite of the session the test asked for.
    /// </remarks>
    private static async Task<byte[]> SeedSessionAsync(
        RepositoryTestHost host,
        Guid credentialId,
        byte fill,
        SessionKind expectedKind)
    {
        DateTime now = DateTime.UtcNow;

        await using BudgetoidDbContext db = CreateDb(host);
        Credential credential = await db.Credentials.SingleAsync(stored => stored.Id == credentialId);
        Session session = Session.Establish(credential, now.AddMinutes(-1), now.AddHours(1));
        if (session.Kind != expectedKind)
        {
            throw new InvalidOperationException(
                $"Seeding asked for a {expectedKind} session and the domain derived {session.Kind}.");
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
    /// <c>session_tokens</c>, so two identical tokens would be one row and a test holding both a locked
    /// and a full handle would have nothing to choose wrongly between.
    /// </remarks>
    private static byte[] TokenBytes(byte fill) => [.. Enumerable.Repeat(fill, SessionToken.TokenLength)];

    /// <summary>
    /// The WebAuthn credential id a seeded passkey carries. Derived from the same fill byte as the token,
    /// because the column is unique.
    /// </summary>
    private static byte[] WebAuthnCredentialId(byte fill) => [.. Enumerable.Repeat(fill, 16)];

    /// <summary>
    /// Writes one row of budget content with raw SQL on the container superuser connection.
    /// </summary>
    /// <remarks>
    /// Raw SQL rather than the domain factory through EF, because <c>Account</c> carries the
    /// <c>BudgetIsolation</c> query filter and a seeding context is built without an
    /// <c>IBudgetContext</c> to satisfy it. <c>USD</c> is a currency the migration seeds, so the foreign
    /// key is met without a currency of this file's own.
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

    /// <summary>
    /// The whole response body, with the one member that varies per <b>request</b> rather than per
    /// <b>cause</b> replaced by a fixed placeholder.
    /// </summary>
    /// <remarks>
    /// <c>traceId</c> is a new value on every request, including two requests refused for the identical
    /// reason, so comparing it would compare the trace and not the refusal. The member is replaced rather
    /// than removed, so a <c>traceId</c> that stopped being emitted still fails, and any other member
    /// appearing, disappearing or differing fails with it. A body that is not JSON at all is returned
    /// verbatim, so a refusal that stopped being problem details is a difference this can still see.
    /// </remarks>
    private static async Task<string> ComparableBodyOfAsync(HttpResponseMessage response)
    {
        string raw = await response.Content.ReadAsStringAsync();
        if (JsonNode.Parse(raw) is not JsonObject body)
        {
            return raw;
        }

        if (body.ContainsKey(TraceIdMember))
        {
            body[TraceIdMember] = "<one per request>";
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// The <c>title</c> of a problem-details body, or the empty string when the body carries none.
    /// </summary>
    private static async Task<string> TitleOfAsync(HttpResponseMessage response)
    {
        string raw = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(raw) is JsonObject body && body["title"] is JsonNode title
            ? title.GetValue<string>()
            : string.Empty;
    }

    private const string TraceIdMember = "traceId";

    /// <summary>
    /// A context on the container superuser connection, with no ambient budget. Safe for what is seeded
    /// through it — neither <c>Session</c>, <c>SessionToken</c> nor <c>Credential</c> carries a budget
    /// query filter — and superuser because these rows are arranged, not measured.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);
}
