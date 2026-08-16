using System.Net;
using Api.Infrastructure;
using Application.Users.EnsureUser;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Which routes may be reached with a handle whose session has already ended, and how little that
/// permission grants on the one route that has it.
/// </summary>
/// <remarks>
/// <para>
/// <b>An opt-in marker that admits a dead credential needs its blast radius pinned in both
/// directions.</b> One direction is the set itself, read off the route table the way
/// <see cref="AnonymousSurfaceTests" /> reads <c>IAllowAnonymous</c>: a marker added tomorrow goes red
/// until somebody argues for it here. The other is what the marker does <em>not</em> relax — it says
/// nothing about whether a token matched, and it publishes no tenant — because both are properties a
/// reader will assume rather than check.
/// </para>
/// <para>
/// <b>The no-tenant half is the one nothing else in the suite can see.</b> The marked route reads no
/// budget content, so an ambient budget published beside a dead session changes no answer today and
/// every test in this file would stay green with one. What it changes is the day a budget-scoped
/// statement joins that route: with no budget published, the first such statement meets an unresolved
/// budget and throws — a red test. With one published, it is scoped to a tenant reached by a session
/// that has already ended, and <c>budget_isolation</c> is <c>FOR ALL</c>, so it matches nothing and
/// reports success. That is the failure nobody files a report about, which is why the publication is
/// asserted directly rather than through anything the route answers.
/// </para>
/// <para>
/// The cookie name and the client header are literals, for the reason
/// <see cref="SessionCookieAuthenticationTests" /> states once for all these files.
/// </para>
/// </remarks>
public sealed class AcceptsEndedSessionTests
{
    private const string RevocationPath = "/api/me/session/revocation";

    /// <summary>
    /// Every route that accepts a handle whose session has ended, and the argument for each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>POST /api/me/session/revocation</c> — signing out, which has to be idempotent. A sign-out
    /// response can be lost on the way back and the client retries; a person can have two tabs open and
    /// close both. Under the ordinary rule the second attempt presents a handle whose session is already
    /// revoked, is refused 401, and leaves the client holding a dead cookie forever with a signed-in
    /// shell on screen — the exact state <c>SessionCookie.Clear</c> exists to prevent. There is also
    /// nothing left to protect on that request: the session it names has already ended, and the route
    /// reads nothing but the caller's own session id off their own authentication.
    /// </para>
    /// <para>
    /// The method is part of the entry rather than decoration. A marker on a route group would admit a
    /// dead handle to every verb over that prefix, including the ones added afterwards, and "the marked
    /// set" would then be a shape nobody chose.
    /// </para>
    /// <para>
    /// A route added here needs its own paragraph, and the paragraph is the review. Two questions have
    /// to be answered in it: what the route still does for a caller whose session has ended, and what it
    /// would read if it ever reached tenant data.
    /// </para>
    /// </remarks>
    private static readonly string[] RoutesAcceptingAnEndedSession =
    [
        $"POST {RevocationPath}",
    ];

    /// <summary>
    /// The marked surface is exactly the route argued for above — no more, and no fewer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Structural rather than behavioural</b>, for <see cref="AnonymousSurfaceTests" />' reason: a
    /// sweep of requests carrying dead handles could only report what happened on the routes it thought
    /// to try, while the permission is a property of the route table and has to be readable whole.
    /// </para>
    /// <para>
    /// <b>The two counts at the end are the controls.</b> This assertion is satisfied by an empty
    /// enumeration in exactly one case — an empty expectation — so the marker has to be found somewhere
    /// and the route table has to have been populated at all, or a metadata lookup that came back null
    /// for every endpoint would pass while proving nothing.
    /// </para>
    /// <para>
    /// Production, and it needs no database: reading the route table opens no connection, and the
    /// Development-only OpenAPI document has no marker of this kind either way.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryRouteAcceptingAnEndedSession_IsTheOneArguedFor()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Act
        RouteEndpoint[] endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();
        string[] marked = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<AcceptsEndedSessionAttribute>() is not null)
            .Select(endpoint =>
                $"{MethodsOf(endpoint)} {endpoint.RoutePattern.RawText ?? string.Empty}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — joined rather than compared as collections, so a failure names the route that moved
        // instead of reporting that two sets differ.
        await Assert.That(string.Join(", ", marked))
            .IsEqualTo(string.Join(", ", RoutesAcceptingAnEndedSession.Order(StringComparer.Ordinal)));

        // The controls: the marker exists on this route table, and the route table was really read.
        await Assert.That(marked.Length).IsGreaterThan(0);
        await Assert.That(endpoints.Length).IsGreaterThan(marked.Length);
    }

    /// <summary>
    /// That a dead handle on the marked route publishes the account it named and no tenant at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves are the test.</b> Publishing nothing would be its own defect — the route ends the
    /// caller's session by the id on their own authentication, and a request naming nobody cannot do
    /// that — so "no budget" has to be asserted next to "the right user", or an implementation that
    /// published neither would satisfy it.
    /// </para>
    /// <para>
    /// A spy over the real writer rather than a stub, for <see cref="UserProvisioningWriterTests" />'
    /// reason: the identity published here is what reaches <c>app.current_user_id</c> on the next
    /// connection open, and a writer that recorded and discarded would leave the request failing
    /// <c>22P02</c> before the line under test.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ADeadHandleOnTheMarkedRoute_PublishesTheIdentityAndNoAmbientBudget()
    {
        // Arrange — an account that DOES own a budget, so "no budget was published" is a decision on
        // this request rather than an account with nothing to publish.
        PublicationLog log = new();
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(
            host,
            services => services.Replace(ServiceDescriptor.Scoped<IUserContextWriter>(provider =>
                new SpyingUserContextWriter(
                    ActivatorUtilities.CreateInstance<CurrentUserWriter>(provider),
                    log))));
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] revoked = await SeedSessionAsync(host, owner.UserId, fill: 0x11, revoked: true);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage response = await SendAsync(client, RevocationPath, revoked);

        // Assert — the request really reached the route first. A 401 would leave the publication log
        // empty, and an empty log satisfies "no budget was published" while saying nothing.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The identity, and the account it names.
        await Assert.That(string.Join(", ", log.Publications.Select(publication => publication.Member)))
            .IsEqualTo("ResolveUser");
        await Assert.That(log.Publications[0].Value).IsEqualTo(owner.UserId);
    }

    /// <summary>
    /// That the marker relaxes "is this session still live" and nothing else.
    /// </summary>
    /// <remarks>
    /// The handle still has to name a real session belonging to the account it publishes, which is why
    /// this route is not on <see cref="AnonymousSurfaceTests" />' list. A marker that admitted an
    /// unmatched token would turn the one route that ends a session into a route anybody can reach with
    /// 32 bytes of their own choosing.
    /// </remarks>
    [Test]
    public async Task AnUnknownHandleOnTheMarkedRoute_IsStillRefused()
    {
        // Arrange — the unknown handle is well-formed in every way a request can observe: 32 bytes,
        // unpadded base64url. The only thing wrong with it is that no row was ever written for it.
        await using RepositoryTestHost host = await SessionCookieAuthenticationTests.StartHostAsync();
        await using ApiFactory factory = SessionCookieAuthenticationTests.CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        byte[] revoked = await SeedSessionAsync(host, owner.UserId, fill: 0x11, revoked: true);
        HttpClient client = factory.CreateClient();

        // Act
        HttpResponseMessage unknown = await SendAsync(client, RevocationPath, TokenBytes(0x99));
        HttpResponseMessage dead = await SendAsync(client, RevocationPath, revoked);
        HttpResponseMessage none = await SendAsync(client, RevocationPath, token: null);

        // Assert — the dead handle beside them is what makes both refusals a verdict on the handle
        // rather than on a route that refuses everybody, which is a state an application with no marker
        // at all satisfies perfectly.
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(dead.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    private const string OwnerSubject = "google-ended-session-owner";

    private const string OwnerEmail = "ended-session-owner@budgetoid.test";

    /// <summary>
    /// The verbs an endpoint answers, spelled the way the expectation above writes them.
    /// </summary>
    /// <remarks>
    /// Ordered and joined rather than taken as "the first one": a route that gained a second verb
    /// alongside the marker is a route whose marked surface grew, and reporting only the first would
    /// hide it.
    /// </remarks>
    private static string MethodsOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is { HttpMethods: { Count: > 0 } methods }
            ? string.Join("|", methods.Order(StringComparer.Ordinal))
            : "ANY";

    /// <summary>
    /// Sends one request carrying the first-party client header and, optionally, the session cookie.
    /// </summary>
    /// <remarks>
    /// The header goes on every request because the CSRF control refuses one without it before anything
    /// looks at the cookie — see <see cref="FirstPartyRequestTests" />, which owns that claim. A test
    /// here that omitted it would be reading a 403 and calling it a rejected handle.
    /// </remarks>
    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, byte[]? token)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path);
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
    /// Seeds a passkey credential, a session, and the handle it is presented by; returns the token bytes
    /// the cookie carries.
    /// </summary>
    /// <remarks>
    /// A <b>passkey</b> credential rather than the federated one <see cref="RepositoryTestHost.SeedOwnerAsync" />
    /// writes, because <c>Session.Establish</c> derives the kind from the credential's type: a federated
    /// session is <see cref="SessionKind.Locked" />, and a locked session refused for its kind is a
    /// refusal nobody could tell from the marker failing to work.
    /// </remarks>
    private static async Task<byte[]> SeedSessionAsync(
        RepositoryTestHost host,
        Guid userId,
        byte fill,
        bool revoked)
    {
        Guid credentialId = await host.SeedPasskeyAsync(userId, [.. Enumerable.Repeat(fill, 16)]);
        DateTime now = DateTime.UtcNow;

        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);
        Credential credential = await db.Credentials.SingleAsync(stored => stored.Id == credentialId);
        Session session = Session.Establish(credential, now.AddMinutes(-1), now.AddHours(1));
        if (revoked)
        {
            session.Revoke(now.AddSeconds(-1));
        }

        byte[] token = TokenBytes(fill);
        db.Sessions.Add(session);
        db.SessionTokens.Add(SessionToken.For(session, token));
        await db.SaveChangesAsync();

        return token;
    }

    private static byte[] TokenBytes(byte fill) => [.. Enumerable.Repeat(fill, SessionToken.TokenLength)];

    /// <summary>One call into <c>IUserContextWriter</c>: which member, and the id it was handed.</summary>
    private sealed record Publication(string Member, Guid Value);

    /// <summary>
    /// Every publication the request made, oldest first.
    /// </summary>
    /// <remarks>
    /// Owned by the test rather than registered in the container, because the writer is scoped and the
    /// scope dies with the request: a log resolved from the container would be gone by the time the
    /// assertions run.
    /// </remarks>
    private sealed class PublicationLog
    {
        private readonly List<Publication> _publications = [];

        public IReadOnlyList<Publication> Publications => _publications;

        public void Record(string member, Guid value) => _publications.Add(new Publication(member, value));
    }

    /// <summary>Records every publication and passes it straight on to the real writer.</summary>
    private sealed class SpyingUserContextWriter(IUserContextWriter inner, PublicationLog log)
        : IUserContextWriter
    {
        public void ResolveUser(Guid userId)
        {
            log.Record(nameof(ResolveUser), userId);
            inner.ResolveUser(userId);
        }

        public void ResolveBudget(Guid budgetId)
        {
            log.Record(nameof(ResolveBudget), budgetId);
            inner.ResolveBudget(budgetId);
        }
    }
}
