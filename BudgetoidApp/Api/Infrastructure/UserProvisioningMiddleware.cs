using System.Security.Claims;
using Application.Users.EnsureUser;

namespace Api.Infrastructure;

public sealed class UserProvisioningMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext httpContext, EnsureUserHandler handler, CurrentUser currentUser)
    {
        ClaimsPrincipal principal = httpContext.User;
        if (principal.Identity?.IsAuthenticated == true)
        {
            if (!TryGetRequiredClaim(principal, "sub", out string googleSubject) ||
                !TryGetRequiredClaim(principal, "email", out string email))
            {
                await Results.Problem(
                        title: "Authenticated principal is missing required claims.",
                        statusCode: StatusCodes.Status401Unauthorized)
                    .ExecuteAsync(httpContext);
                return;
            }

            // Only the subject and the verified email are read off the principal. Every other claim
            // the provider offers is deliberately left on the token: an account stores what it needs
            // to be reached, and a claim nothing reads is data we would be holding for no one.
            ProvisionedUser provisioned = await handler.HandleAsync(
                new EnsureUserCommand(googleSubject, email),
                httpContext.RequestAborted);

            currentUser.UserId = provisioned.UserId;
            currentUser.BudgetId = provisioned.BudgetId;
        }

        await next(httpContext);
    }

    private static bool TryGetRequiredClaim(ClaimsPrincipal principal, string claimType, out string value)
    {
        value = principal.FindFirstValue(claimType) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
