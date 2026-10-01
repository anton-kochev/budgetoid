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

        // A principal that authenticated on any other scheme is not admitted here, and that is the
        // change the bridge scheme's deletion bought. While it stood, a Google bearer authenticated
        // through JwtBearer on the default scheme and carried no kind claim, so this requirement had to
        // let such a principal past or refuse the entire product. Nothing defaults to JwtBearer any
        // more: the fallback policy names the cookie scheme, and the one policy that names the provider
        // — the registration group's — declares itself and so never reaches this requirement at all. A
        // principal arriving here with no kind claim is therefore a cookie principal that does not have
        // one, which is a session this product did not write.
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
    /// same argument <c>CanonicalIdentifier</c> makes about a format that is not a spelling. Two families of
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
