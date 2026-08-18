using System.Security.Claims;
using Domain.Sessions;
using Microsoft.AspNetCore.Authorization;

namespace Api.Infrastructure;

/// <summary>
/// That the request's session reaches the account's budget content — FR-109, carried on the
/// application's fallback policy and therefore on every route that declares nothing.
/// </summary>
/// <remarks>
/// A requirement with no state: what it needs is on the request, and one instance is built with the
/// policy at startup. The decision lives in <see cref="FullSessionRequirementHandler" />.
/// </remarks>
public sealed class FullSessionRequirement : IAuthorizationRequirement;

/// <summary>
/// Decides <see cref="FullSessionRequirement" /> from the kind claim
/// <see cref="SessionCookieAuthenticationHandler" /> published, the route's own opt-out, and nothing
/// else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every arm that does not succeed simply returns.</b> <c>Fail</c> is never called: it would veto the
/// policy for every other handler as well, and this requirement is one of two on the fallback policy
/// rather than the last word on the request. Leaving it unsatisfied is what refuses the request, and it
/// is the answer a handler that ran out of things to check should give.
/// </para>
/// <para>
/// <b>Registered as <see cref="IAuthorizationHandler" /> in <c>Program.cs</c>, and that registration is
/// load-bearing.</b> An unregistered handler leaves the fallback policy carrying a requirement nothing
/// can satisfy, which is a 403 on every authenticated request in the product —
/// <c>FullSessionRequirementTests</c> resolves the registered set rather than constructing this type
/// precisely so that state is caught.
/// </para>
/// </remarks>
public sealed class FullSessionRequirementHandler : AuthorizationHandler<FullSessionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FullSessionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Fail closed on a resource that is not a request. Nothing in this application invokes
        // authorization outside endpoint routing, so this arm is unreachable today — and the thing that
        // would make it reachable is an IAuthorizationService call passing some resource of its own, on
        // which "there is no endpoint and no cookie" must not read as "allowed".
        if (context.Resource is not HttpContext httpContext)
        {
            return Task.CompletedTask;
        }

        // The route's own opt-out, read off the endpoint because a handler sees the request and not the
        // route table. See AllowsLockedSessionAttribute for why the polarity runs this way.
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<AllowsLockedSessionAttribute>() is not null)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // TEMPORARY, and it leaves with the bridge scheme named in Program.cs. Sign-in still runs through
        // the identity provider, so every request arriving on a Google bearer is authenticated by
        // JwtBearer through Budgetoid.Bridge; such a principal has no session, therefore no kind claim,
        // and a requirement that refused what it did not find would refuse the entire product today.
        //
        // It is not a new hole: that surface is exactly as reachable after this commit as before it. What
        // this requirement changes is only what a *session* may do, and no bearer request has one. When
        // the bridge and JwtBearer are deleted, this branch has nothing left to preserve —
        // FullSessionRequirementTests.APrincipalFromAnotherScheme_Succeeds goes red at that moment, and
        // that redness is the reminder to remove the branch rather than the test.
        //
        // Every identity is asked rather than only the primary one: a principal carrying a session
        // identity anywhere in it is a session request, and reading only the first would let a second
        // identity stapled on in front of it skip the gate.
        if (!context.User.Identities.Any(identity => string.Equals(
                identity.AuthenticationType,
                SessionCookieAuthenticationHandler.SchemeName,
                StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (ReadsBudgetContent(
                context.User.FindFirstValue(SessionCookieAuthenticationHandler.SessionKindClaimType)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether <paramref name="claimValue" /> is a kind this product wrote, and one that reads budget
    /// content.
    /// </summary>
    /// <remarks>
    /// <b>The round trip is the rule, and a bare <c>Enum.TryParse</c> is not a substitute for it</b> — the
    /// same argument <c>CanonicalFactorId</c> makes about a format that is not a spelling. Two families of
    /// value get in without it. The obvious call is the case-insensitive overload, which admits
    /// <c>"full"</c>; and <em>every</em> overload accepts a numeric string, so <c>"1"</c> parses to
    /// <see cref="SessionKind.Full" /> under the case-sensitive one too. The claim is written by
    /// <c>SessionKind.ToString()</c>, which produces exactly one spelling per member, so comparing the
    /// presented text back against what the parsed member renders as is the only formulation that cannot
    /// drift from the value the authentication handler actually published. Anything else here is an
    /// account opened by a value nothing in this product ever wrote.
    /// </remarks>
    private static bool ReadsBudgetContent(string? claimValue) =>
        Enum.TryParse(claimValue, out SessionKind kind)
        && string.Equals(claimValue, kind.ToString(), StringComparison.Ordinal)
        && kind.ReadsBudgetContent();
}
