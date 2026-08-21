using System.Security.Claims;
using Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// The requirement that decides whether a session may reach anything but the sign-out, arm by arm,
/// away from the route table that carries it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this sits beside the request-level file rather than replacing it.</b>
/// <see cref="LockedSessionTests" /> proves the gate is wired to the routes; this proves it decides the
/// way it is meant to on inputs no live route can produce today — a claim that is absent, a claim spelled
/// in a case no <see cref="Domain.Sessions.SessionKind" /> member is spelled in, a principal that came in
/// on some other scheme. None of those can be arranged over HTTP, and every one of them is a way the gate
/// fails open.
/// </para>
/// <para>
/// <b>The handler is resolved from the application's own container rather than constructed by name.</b>
/// Two reasons, and the second is the one that matters. Constructing it here would pin its type name,
/// which is the production author's to choose. Resolving it proves something this file could not
/// otherwise say: that the handler is <em>registered</em>. An unregistered handler leaves the fallback
/// policy with a requirement nothing can satisfy — every authenticated request in the product answers
/// 403 — and a test that newed the class up would be green through all of it.
/// </para>
/// <para>
/// <b>Every negative arm carries a <c>Full</c> control in the same test.</b> Without it, "does not
/// succeed" is satisfied by a handler that succeeds at nothing, by a handler that was never registered,
/// and by an assembly where the requirement does nothing at all — which is precisely the state this file
/// is written against.
/// </para>
/// <para>
/// <b>Production and no database.</b> Building the route table and the authorization services opens no
/// connection, for <see cref="AnonymousSurfaceTests" />' reason.
/// </para>
/// </remarks>
public sealed class FullSessionRequirementTests
{
    /// <summary>
    /// A session whose kind claim reads <c>Full</c> satisfies the requirement.
    /// </summary>
    /// <remarks>
    /// The spelling is written out rather than taken from <c>SessionKind.Full.ToString()</c>. The claim
    /// value is produced by that call, so reading it back from the same expression would compare the enum
    /// with itself and pass under any spelling at all — including one no session ever carried.
    /// </remarks>
    [Test]
    public async Task AFullSessionPrincipal_Succeeds()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext context = ContextFor(
            factory,
            SessionPrincipal("Full"),
            HttpContextOn(RouteWithNoMarker));

        // Act
        await RunHandlersAsync(factory, context);

        // Assert
        await Assert.That(context.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A session whose kind claim reads <c>Locked</c> does not satisfy the requirement.
    /// </summary>
    /// <remarks>
    /// This is FR-109 itself, stated on the one input the production path can actually produce today: a
    /// federated sign-in. The control beside it is what stops the assertion being satisfied by a handler
    /// that succeeds for nobody.
    /// </remarks>
    [Test]
    public async Task ALockedSessionPrincipal_DoesNotSucceed()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext locked = ContextFor(
            factory,
            SessionPrincipal("Locked"),
            HttpContextOn(RouteWithNoMarker));
        AuthorizationHandlerContext full = ContextFor(
            factory,
            SessionPrincipal("Full"),
            HttpContextOn(RouteWithNoMarker));

        // Act
        await RunHandlersAsync(factory, locked);
        await RunHandlersAsync(factory, full);

        // Assert
        await Assert.That(locked.HasSucceeded).IsFalse();
        await Assert.That(full.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A session principal carrying no kind claim at all does not satisfy the requirement.
    /// </summary>
    /// <remarks>
    /// <b>The fail-closed arm, and the one an implementation reads past without noticing.</b> The obvious
    /// shape — find the claim, and if it parses to <c>Locked</c> refuse — succeeds for an identity that
    /// carries no claim, because there is nothing there to compare. An identity can lose the claim: a
    /// cookie handler edited to stop publishing it, a principal assembled somewhere else, a scheme added
    /// later that authenticates on this name. Every one of those is an account reached by a session
    /// nobody proved anything about.
    /// </remarks>
    [Test]
    public async Task ASessionPrincipalWithNoKindClaim_DoesNotSucceed()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext missing = ContextFor(
            factory,
            SessionPrincipal(kind: null),
            HttpContextOn(RouteWithNoMarker));
        AuthorizationHandlerContext full = ContextFor(
            factory,
            SessionPrincipal("Full"),
            HttpContextOn(RouteWithNoMarker));

        // Act
        await RunHandlersAsync(factory, missing);
        await RunHandlersAsync(factory, full);

        // Assert
        await Assert.That(missing.HasSucceeded).IsFalse();
        await Assert.That(full.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A kind claim spelled as no <see cref="Domain.Sessions.SessionKind" /> member is spelled does not
    /// satisfy the requirement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both spellings, and the lower-case one is the whole test.</b> <c>Enum.TryParse</c> is
    /// case-insensitive unless it is told otherwise, and the obvious call is the case-insensitive
    /// overload — so <c>"full"</c> would be admitted by an implementation that looks completely correct.
    /// The claim is written by <c>SessionKind.ToString()</c>, which produces exactly one spelling, so
    /// admitting any other is admitting a value the product never wrote and something else did.
    /// </para>
    /// <para>
    /// <c>"Elevated"</c> is the second half: a word that is not a member under any casing, standing for
    /// the kind somebody adds later and for a claim carrying arbitrary text.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AKindClaimSpelledAnyOtherWay_DoesNotSucceed()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext lowerCase = ContextFor(
            factory,
            SessionPrincipal("full"),
            HttpContextOn(RouteWithNoMarker));
        AuthorizationHandlerContext unknownWord = ContextFor(
            factory,
            SessionPrincipal("Elevated"),
            HttpContextOn(RouteWithNoMarker));
        AuthorizationHandlerContext full = ContextFor(
            factory,
            SessionPrincipal("Full"),
            HttpContextOn(RouteWithNoMarker));

        // Act
        await RunHandlersAsync(factory, lowerCase);
        await RunHandlersAsync(factory, unknownWord);
        await RunHandlersAsync(factory, full);

        // Assert
        await Assert.That(lowerCase.HasSucceeded).IsFalse();
        await Assert.That(unknownWord.HasSucceeded).IsFalse();
        await Assert.That(full.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A locked session on a route carrying the opt-out marker satisfies the requirement.
    /// </summary>
    /// <remarks>
    /// The marker is read off the endpoint on the <see cref="HttpContext" /> the requirement is handed as
    /// its resource, which is the only place it can be read from: an authorization handler sees the
    /// request, not the route table. The locked principal is the point — a marker that only admitted
    /// sessions the gate would have admitted anyway would be an attribute that does nothing.
    /// </remarks>
    [Test]
    public async Task ALockedSessionOnAMarkedRoute_Succeeds()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext marked = ContextFor(
            factory,
            SessionPrincipal("Locked"),
            HttpContextOn(RouteWithMarker));
        AuthorizationHandlerContext unmarked = ContextFor(
            factory,
            SessionPrincipal("Locked"),
            HttpContextOn(RouteWithNoMarker));

        // Act
        await RunHandlersAsync(factory, marked);
        await RunHandlersAsync(factory, unmarked);

        // Assert — the unmarked arm beside it, so "succeeded" is the marker's doing and not the gate
        // admitting a locked session everywhere.
        await Assert.That(marked.HasSucceeded).IsTrue();
        await Assert.That(unmarked.HasSucceeded).IsFalse();
    }

    /// <summary>
    /// A principal that authenticated on any scheme other than the session cookie's does <b>not</b>
    /// satisfy the requirement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test asserted the opposite until the bridge scheme was deleted, and inverting it is the
    /// whole point of having written it.</b> While sign-in ran through the identity provider, a Google
    /// bearer was authenticated by <c>JwtBearer</c> through a <c>Budgetoid.Bridge</c> policy scheme and
    /// carried no session and therefore no kind claim; a requirement that refused what it could not find
    /// would have refused every request in the product. So the handler carried an explicit branch
    /// admitting such a principal, and the production comment beside it said that this test going red
    /// would be the reminder to delete the branch. The branch is gone, and this is that redness turned
    /// into the claim it was standing in for.
    /// </para>
    /// <para>
    /// <b>What closes the hole is not this handler, and that is worth being precise about.</b> Nothing
    /// defaults to <c>JwtBearer</c> any more: the fallback policy names the session cookie's scheme, and
    /// the one policy that names the provider — the registration group's — declares itself and so never
    /// reaches this requirement at all. A principal arriving here on some other scheme is therefore a
    /// state no live route produces, and this test constructs one directly for exactly that reason. What
    /// it pins is the handler's <em>reading</em>: a kind claim that is absent must not be read as
    /// permission, whatever puts a foreign principal in front of it later.
    /// </para>
    /// <para>
    /// The authentication type below is a scheme name that is not the cookie handler's; the value stands
    /// for every such name rather than for the bearer scheme in particular, which is why it is compared
    /// against <see cref="SessionCookieAuthenticationHandler.SchemeName" /> and not enumerated.
    /// </para>
    /// </remarks>
    [Test]
    public async Task APrincipalFromAnotherScheme_DoesNotSatisfyTheRequirement()
    {
        // Arrange — no kind claim, because a principal from another scheme has no session and so never
        // has one. The Full control beside it is this file's rule for every negative arm: without it,
        // "does not succeed" is satisfied by a handler that succeeds for nobody and by one that was
        // never registered at all.
        ClaimsPrincipal bearer = new(new ClaimsIdentity(
            [new Claim(SessionCookieAuthenticationHandler.SubjectClaimType, "google-subject")],
            BearerAuthenticationType));
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext context = ContextFor(
            factory,
            bearer,
            HttpContextOn(RouteWithNoMarker));
        AuthorizationHandlerContext full = ContextFor(
            factory,
            SessionPrincipal("Full"),
            HttpContextOn(RouteWithNoMarker));

        // Act
        await RunHandlersAsync(factory, context);
        await RunHandlersAsync(factory, full);

        // Assert — and that the value really is a different scheme, so a rename of the cookie scheme
        // cannot quietly turn this arm into a second copy of the session one.
        await Assert.That(BearerAuthenticationType)
            .IsNotEqualTo(SessionCookieAuthenticationHandler.SchemeName);
        await Assert.That(context.HasSucceeded).IsFalse();
        await Assert.That(full.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// The authentication type a principal that did not come in on the session cookie carries. Any name
    /// but the cookie scheme's would do; this is the one the provider path used to produce.
    /// </summary>
    private const string BearerAuthenticationType = "Bearer";

    /// <summary>Endpoint metadata for a route that declares nothing — the fallback policy's surface.</summary>
    private static readonly object[] RouteWithNoMarker = [];

    /// <summary>Endpoint metadata for a route that opts out of the gate.</summary>
    private static readonly object[] RouteWithMarker = [new AllowsLockedSessionAttribute()];

    /// <summary>
    /// A principal shaped the way <see cref="SessionCookieAuthenticationHandler" /> shapes one, optionally
    /// without its kind claim.
    /// </summary>
    /// <remarks>
    /// The identity's authentication type is the scheme name, which is what makes it authenticated at all
    /// and is also how the requirement tells a cookie request from a bearer one. The claim <em>type</em>
    /// comes from the production constant — it is an internal name, not wire contract — while every claim
    /// <em>value</em> is written out, for the reason <see cref="AFullSessionPrincipal_Succeeds" /> gives.
    /// </remarks>
    private static ClaimsPrincipal SessionPrincipal(string? kind)
    {
        List<Claim> claims =
        [
            new Claim(SessionCookieAuthenticationHandler.SubjectClaimType, Guid.Empty.ToString()),
            new Claim(SessionCookieAuthenticationHandler.SessionIdClaimType, Guid.Empty.ToString()),
        ];

        if (kind is not null)
        {
            claims.Add(new Claim(SessionCookieAuthenticationHandler.SessionKindClaimType, kind));
        }

        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, SessionCookieAuthenticationHandler.SchemeName));
    }

    /// <summary>
    /// A request sitting on an endpoint carrying <paramref name="metadata" />, which is where an
    /// authorization handler reads a route's opt-outs from.
    /// </summary>
    private static DefaultHttpContext HttpContextOn(object[] metadata)
    {
        DefaultHttpContext httpContext = new();
        httpContext.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            "a route under test"));

        return httpContext;
    }

    /// <summary>
    /// The context an authorization handler is invoked with: this one requirement, this principal, and
    /// the request as the resource.
    /// </summary>
    /// <remarks>
    /// <paramref name="factory" /> is unused by the construction and taken anyway, so that every call site
    /// reads as "a context for this application" and none of them can be lifted out of a host that has to
    /// exist for <see cref="RunHandlersAsync" /> to have anything to run.
    /// </remarks>
    private static AuthorizationHandlerContext ContextFor(
        ApiFactory factory,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return new AuthorizationHandlerContext([new FullSessionRequirement()], principal, httpContext);
    }

    /// <summary>
    /// Runs every authorization handler the application registers against <paramref name="context" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of them rather than one picked by type, for the reason the class remarks give: the handler's
    /// name belongs to the production author, and running the registered set is what proves the gate's
    /// handler is in it. A handler that does not recognise this requirement does nothing with it, so the
    /// others are inert here — the framework's own <c>PassThroughAuthorizationHandler</c> included, which
    /// acts only on requirements that are their own handlers.
    /// </para>
    /// <para>
    /// Through a scope, because a handler may be registered scoped and resolving one from the root
    /// provider would throw rather than fail an assertion.
    /// </para>
    /// </remarks>
    private static async Task RunHandlersAsync(ApiFactory factory, AuthorizationHandlerContext context)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        foreach (IAuthorizationHandler handler in
                 scope.ServiceProvider.GetServices<IAuthorizationHandler>())
        {
            await handler.HandleAsync(context);
        }
    }

    /// <summary>
    /// The application, in the environment it ships in. No database is reached: nothing here serves a
    /// request.
    /// </summary>
    private static ApiFactory CreateFactory() => new(
        "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
        environment: "Production");
}
