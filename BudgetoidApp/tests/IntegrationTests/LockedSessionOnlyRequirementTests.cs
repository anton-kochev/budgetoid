using System.Security.Claims;
using Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// The requirement that keeps a route marked locked-only away from a full session, arm by arm, away from
/// the route table that carries it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It judges marked routes and nothing else.</b> It rides the fallback policy beside
/// <see cref="FullSessionRequirement" />, so every route that declares nothing meets it; an unmarked
/// route is the other requirement's to decide, and this one must succeed there whatever the principal
/// carries — or it refuses the whole product.
/// </para>
/// <para>
/// <b>The handler is resolved from the application's own container</b>, for
/// <see cref="FullSessionRequirementTests" />' reason: running the registered set is what proves the
/// handler is registered. An unregistered handler leaves the fallback policy with a requirement nothing
/// can satisfy, and every authenticated request answers 403.
/// </para>
/// <para>
/// <b>Every negative arm carries a marked <c>Locked</c> control in the same test</b>, so "does not
/// succeed" cannot be satisfied by a handler that succeeds at nothing on a marked route.
/// </para>
/// <para>
/// <b>Refusing arms are asserted not to have failed.</b> The handler mirrors
/// <c>FullSessionRequirementHandler</c>: leaving the requirement unsatisfied is the refusal, and
/// <c>Fail</c> would veto the policy for every other handler as well.
/// </para>
/// <para>
/// <b>Production and no database.</b> Building the authorization services opens no connection.
/// </para>
/// </remarks>
public sealed class LockedSessionOnlyRequirementTests
{
    /// <summary>
    /// On a route without the marker, the requirement succeeds for a full session, a locked one, and a
    /// session carrying no kind claim.
    /// </summary>
    /// <remarks>
    /// The no-claim arm is the one that matters: the other requirement refuses it, and this one must not
    /// take that judgement on itself or answer a different way.
    /// </remarks>
    [Test]
    public async Task AnyPrincipalOnARouteWithNoMarker_Succeeds()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext full = ContextFor(
            factory,
            SessionPrincipal("Full"),
            HttpContextOn(RouteWithNoMarker));
        AuthorizationHandlerContext locked = ContextFor(
            factory,
            SessionPrincipal("Locked"),
            HttpContextOn(RouteWithNoMarker));
        AuthorizationHandlerContext missing = ContextFor(
            factory,
            SessionPrincipal(kind: null),
            HttpContextOn(RouteWithNoMarker));

        // Act
        await RunHandlersAsync(factory, full);
        await RunHandlersAsync(factory, locked);
        await RunHandlersAsync(factory, missing);

        // Assert
        await Assert.That(full.HasSucceeded).IsTrue();
        await Assert.That(locked.HasSucceeded).IsTrue();
        await Assert.That(missing.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A route carrying only the opt-out from the full-session gate is not judged by this requirement.
    /// </summary>
    /// <remarks>
    /// The revocation route carries <see cref="AllowsLockedSessionAttribute" /> and admits a full session
    /// as well. A handler that read the opt-out instead of its own marker would refuse every full session
    /// its sign-out.
    /// </remarks>
    [Test]
    public async Task AFullSessionOnARouteCarryingOnlyTheOptOut_Succeeds()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext context = ContextFor(
            factory,
            SessionPrincipal("Full"),
            HttpContextOn(RouteWithOnlyTheOptOut));

        // Act
        await RunHandlersAsync(factory, context);

        // Assert
        await Assert.That(context.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A locked session on a marked route satisfies the requirement.
    /// </summary>
    [Test]
    public async Task ALockedSessionOnAMarkedRoute_Succeeds()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext context = ContextFor(
            factory,
            SessionPrincipal("Locked"),
            HttpContextOn(RouteWithMarker));

        // Act
        await RunHandlersAsync(factory, context);

        // Assert
        await Assert.That(context.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A full session on a marked route does not satisfy the requirement, and the requirement is not
    /// failed.
    /// </summary>
    /// <remarks>
    /// The route shape is the real one too — both markers together — so a handler that let the opt-out
    /// short-circuit its own marker is caught here.
    /// </remarks>
    [Test]
    public async Task AFullSessionOnAMarkedRoute_DoesNotSucceed()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext markerOnly = ContextFor(
            factory,
            SessionPrincipal("Full"),
            HttpContextOn(RouteWithMarker));
        AuthorizationHandlerContext bothMarkers = ContextFor(
            factory,
            SessionPrincipal("Full"),
            HttpContextOn(RouteWithBothMarkers));
        AuthorizationHandlerContext locked = ContextFor(
            factory,
            SessionPrincipal("Locked"),
            HttpContextOn(RouteWithBothMarkers));

        // Act
        await RunHandlersAsync(factory, markerOnly);
        await RunHandlersAsync(factory, bothMarkers);
        await RunHandlersAsync(factory, locked);

        // Assert
        await Assert.That(markerOnly.HasSucceeded).IsFalse();
        await Assert.That(markerOnly.HasFailed).IsFalse();
        await Assert.That(bothMarkers.HasSucceeded).IsFalse();
        await Assert.That(bothMarkers.HasFailed).IsFalse();
        await Assert.That(locked.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A session carrying no kind claim does not satisfy the requirement on a marked route.
    /// </summary>
    /// <remarks>
    /// The fail-closed arm: "refuse when the claim reads <c>Full</c>" admits a principal with nothing to
    /// compare.
    /// </remarks>
    [Test]
    public async Task ASessionWithNoKindClaimOnAMarkedRoute_DoesNotSucceed()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext missing = ContextFor(
            factory,
            SessionPrincipal(kind: null),
            HttpContextOn(RouteWithMarker));
        AuthorizationHandlerContext locked = ContextFor(
            factory,
            SessionPrincipal("Locked"),
            HttpContextOn(RouteWithMarker));

        // Act
        await RunHandlersAsync(factory, missing);
        await RunHandlersAsync(factory, locked);

        // Assert
        await Assert.That(missing.HasSucceeded).IsFalse();
        await Assert.That(missing.HasFailed).IsFalse();
        await Assert.That(locked.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A kind claim that parses to <c>Locked</c> but is not spelled the way the product writes it does
    /// not satisfy the requirement on a marked route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>"locked"</c> is admitted by the case-insensitive <c>Enum.TryParse</c>; <c>"0"</c> is
    /// <see cref="Domain.Sessions.SessionKind.Locked" />'s number, which every overload accepts;
    /// <c>" Locked"</c> is the padded form. None of them is what <c>SessionKind.ToString()</c> writes, so
    /// only the ordinal round trip refuses all three. <c>"1"</c> is Full's number, refused for the
    /// spelling and for the kind both.
    /// </para>
    /// <para>
    /// The parse assertions first pin that each input really reaches <c>Locked</c> by some parser, so
    /// the arm cannot quietly become a copy of the unknown-word case.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AKindClaimNotSpelledAsTheProductWritesIt_DoesNotSucceedOnAMarkedRoute()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext lowerCase = ContextFor(
            factory,
            SessionPrincipal("locked"),
            HttpContextOn(RouteWithMarker));
        AuthorizationHandlerContext lockedsNumber = ContextFor(
            factory,
            SessionPrincipal("0"),
            HttpContextOn(RouteWithMarker));
        AuthorizationHandlerContext fullsNumber = ContextFor(
            factory,
            SessionPrincipal("1"),
            HttpContextOn(RouteWithMarker));
        AuthorizationHandlerContext padded = ContextFor(
            factory,
            SessionPrincipal(" Locked"),
            HttpContextOn(RouteWithMarker));
        AuthorizationHandlerContext locked = ContextFor(
            factory,
            SessionPrincipal("Locked"),
            HttpContextOn(RouteWithMarker));

        // Act
        await RunHandlersAsync(factory, lowerCase);
        await RunHandlersAsync(factory, lockedsNumber);
        await RunHandlersAsync(factory, fullsNumber);
        await RunHandlersAsync(factory, padded);
        await RunHandlersAsync(factory, locked);

        // Assert — the inputs are the ones this arm is about.
        await Assert.That(Enum.TryParse("locked", ignoreCase: true, out Domain.Sessions.SessionKind fromCase))
            .IsTrue();
        await Assert.That(fromCase).IsEqualTo(Domain.Sessions.SessionKind.Locked);
        await Assert.That(Enum.TryParse("0", out Domain.Sessions.SessionKind fromNumber)).IsTrue();
        await Assert.That(fromNumber).IsEqualTo(Domain.Sessions.SessionKind.Locked);
        await Assert.That(Enum.TryParse(" Locked", out Domain.Sessions.SessionKind fromPadded)).IsTrue();
        await Assert.That(fromPadded).IsEqualTo(Domain.Sessions.SessionKind.Locked);

        // Then the refusals, none of them a veto, and the control beside them.
        await Assert.That(lowerCase.HasSucceeded).IsFalse();
        await Assert.That(lowerCase.HasFailed).IsFalse();
        await Assert.That(lockedsNumber.HasSucceeded).IsFalse();
        await Assert.That(lockedsNumber.HasFailed).IsFalse();
        await Assert.That(fullsNumber.HasSucceeded).IsFalse();
        await Assert.That(fullsNumber.HasFailed).IsFalse();
        await Assert.That(padded.HasSucceeded).IsFalse();
        await Assert.That(padded.HasFailed).IsFalse();
        await Assert.That(locked.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// A resource that is not a request does not satisfy the requirement, even for a locked session.
    /// </summary>
    /// <remarks>
    /// With no endpoint there is no marker to read, and "no marker" must not read as "not judged" on a
    /// resource the handler cannot see into — the arm fails closed, as
    /// <c>FullSessionRequirementHandler</c>'s does.
    /// </remarks>
    [Test]
    public async Task AResourceThatIsNotAnHttpContext_DoesNotSucceed()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        AuthorizationHandlerContext foreign = new(
            [new LockedSessionOnlyRequirement()],
            SessionPrincipal("Locked"),
            new object());
        AuthorizationHandlerContext locked = ContextFor(
            factory,
            SessionPrincipal("Locked"),
            HttpContextOn(RouteWithMarker));

        // Act
        await RunHandlersAsync(factory, foreign);
        await RunHandlersAsync(factory, locked);

        // Assert
        await Assert.That(foreign.HasSucceeded).IsFalse();
        await Assert.That(foreign.HasFailed).IsFalse();
        await Assert.That(locked.HasSucceeded).IsTrue();
    }

    /// <summary>
    /// The handler is in the application's registered <see cref="IAuthorizationHandler" /> set.
    /// </summary>
    /// <remarks>
    /// Every test above already depends on it — they run the registered set — but a red there reads as a
    /// wrong decision. This names the cause. The type is named here, which pins the handler's name; the
    /// brief fixes it, so the pin is deliberate.
    /// </remarks>
    [Test]
    public async Task TheHandler_IsRegistered()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        using IServiceScope scope = factory.Services.CreateScope();

        // Act
        IAuthorizationHandler[] handlers =
            scope.ServiceProvider.GetServices<IAuthorizationHandler>().ToArray();

        // Assert — the full-session handler as the control, so an empty set cannot pass as "not here".
        await Assert.That(handlers.OfType<FullSessionRequirementHandler>().Count()).IsEqualTo(1);
        await Assert.That(handlers.OfType<LockedSessionOnlyRequirementHandler>().Count()).IsEqualTo(1);
    }

    /// <summary>
    /// The fallback policy carries the requirement, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marker does nothing on a route the requirement never meets. The erasure-schedule route rides
    /// the fallback policy, so the requirement has to be on it; dropping the line leaves every handler
    /// test above green, because they build their own context.
    /// </para>
    /// <para>
    /// No test pinned <see cref="FullSessionRequirement" /> on the fallback before this one —
    /// <c>RegistrationRouteTests</c> reads only its schemes — so it is asserted here too, as the control
    /// and because it was a gap.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheFallbackPolicy_CarriesTheRequirement()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        // Act
        AuthorizationPolicy? fallback = await policies.GetFallbackPolicyAsync();

        // Assert
        await Assert.That(fallback).IsNotNull();
        await Assert.That(fallback!.Requirements.OfType<FullSessionRequirement>().Count()).IsEqualTo(1);
        await Assert.That(fallback.Requirements.OfType<LockedSessionOnlyRequirement>().Count()).IsEqualTo(1);
    }

    /// <summary>Endpoint metadata for a route that declares nothing — the fallback policy's surface.</summary>
    private static readonly object[] RouteWithNoMarker = [];

    /// <summary>Endpoint metadata for a route that only opts out of the full-session gate.</summary>
    private static readonly object[] RouteWithOnlyTheOptOut = [new AllowsLockedSessionAttribute()];

    /// <summary>Endpoint metadata for a route that declares itself locked-only, and nothing else.</summary>
    private static readonly object[] RouteWithMarker = [new RequiresLockedSessionAttribute()];

    /// <summary>Endpoint metadata shaped the way the erasure-schedule route carries it.</summary>
    private static readonly object[] RouteWithBothMarkers =
        [new AllowsLockedSessionAttribute(), new RequiresLockedSessionAttribute()];

    /// <summary>
    /// A principal shaped the way <see cref="SessionCookieAuthenticationHandler" /> shapes one, optionally
    /// without its kind claim. Claim values are written out, never taken from <c>SessionKind</c>.
    /// </summary>
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

    /// <summary>A request sitting on an endpoint carrying <paramref name="metadata" />.</summary>
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
    /// The context the handler is invoked with: this one requirement, this principal, and the request as
    /// the resource. <paramref name="factory" /> is taken for the reason
    /// <see cref="FullSessionRequirementTests" /> gives.
    /// </summary>
    private static AuthorizationHandlerContext ContextFor(
        ApiFactory factory,
        ClaimsPrincipal principal,
        HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return new AuthorizationHandlerContext(
            [new LockedSessionOnlyRequirement()],
            principal,
            httpContext);
    }

    /// <summary>
    /// Runs every authorization handler the application registers against <paramref name="context" />,
    /// through a scope in case one is registered scoped.
    /// </summary>
    private static async Task RunHandlersAsync(ApiFactory factory, AuthorizationHandlerContext context)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        foreach (IAuthorizationHandler handler in
                 scope.ServiceProvider.GetServices<IAuthorizationHandler>())
        {
            await handler.HandleAsync(context);
        }
    }

    /// <summary>The application, in the environment it ships in. No database is reached.</summary>
    private static ApiFactory CreateFactory() => new(
        "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
        environment: "Production");
}
