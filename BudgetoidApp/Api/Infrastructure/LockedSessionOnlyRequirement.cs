using Domain.Sessions;
using Microsoft.AspNetCore.Authorization;

namespace Api.Infrastructure;

/// <summary>
/// That a route marked <see cref="RequiresLockedSessionAttribute" /> is reached by a locked session and
/// by no other — carried on the application's fallback policy beside <see cref="FullSessionRequirement" />.
/// </summary>
/// <remarks>
/// A requirement with no state: what it needs is on the request, and one instance is built with the
/// policy at startup. The decision lives in <see cref="LockedSessionOnlyRequirementHandler" />.
/// </remarks>
public sealed class LockedSessionOnlyRequirement : IAuthorizationRequirement;

/// <summary>
/// Decides <see cref="LockedSessionOnlyRequirement" /> from the route's marker and the kind claim
/// <see cref="SessionCookieAuthenticationHandler" /> published, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>An unmarked route is not this handler's to judge, and it succeeds there whatever the principal
/// carries.</b> The requirement rides the fallback policy, so it meets every route that declares
/// nothing; a principal with no kind claim on such a route is <see cref="FullSessionRequirement" />'s to
/// refuse, and answering it here as well would be two rules owning one judgement.
/// </para>
/// <para>
/// <b>Every arm that does not succeed simply returns</b>, for <see cref="FullSessionRequirementHandler" />'s
/// reason: <c>Fail</c> would veto the policy for every other handler as well.
/// </para>
/// <para>
/// <b>Registered as <see cref="IAuthorizationHandler" /> in <c>Program.cs</c>, and that registration is
/// load-bearing.</b> Unregistered, the fallback policy carries a requirement nothing can satisfy — a 403
/// on every authenticated request in the product. <c>LockedSessionOnlyRequirementTests</c> resolves the
/// registered set rather than constructing this type.
/// </para>
/// </remarks>
public sealed class LockedSessionOnlyRequirementHandler : AuthorizationHandler<LockedSessionOnlyRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        LockedSessionOnlyRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Fail closed on a resource that is not a request: with no endpoint there is no marker to read,
        // and "no marker" must not read as "not judged" on something this handler cannot see into.
        if (context.Resource is not HttpContext httpContext)
        {
            return Task.CompletedTask;
        }

        // Its own marker and only that. The opt-out from the full-session gate is a different question —
        // the sign-out route carries it and admits a full session too.
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<RequiresLockedSessionAttribute>() is null)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // An equality against Locked, never "does not read budget content": a kind added later reaches no
        // locked-only route until somebody edits this line, which is the fail-closed direction.
        if (SessionCookieAuthenticationHandler.TryReadSessionKind(context.User, out SessionKind kind)
            && kind == SessionKind.Locked)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
