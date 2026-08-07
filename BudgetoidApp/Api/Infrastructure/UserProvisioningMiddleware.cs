using System.Security.Claims;
using Application.Users.EnsureUser;
using Microsoft.AspNetCore.Authorization;

namespace Api.Infrastructure;

/// <summary>
/// Establishes who the request is, and — on the route groups that carry
/// <see cref="ProvisionsUserAttribute" /> and only there — brings the account into existence when it
/// does not exist yet.
/// </summary>
/// <remarks>
/// <para>
/// The route's own <see cref="IAllowAnonymous" /> marker is read first and ends the method: an endpoint
/// that runs without a principal runs without an account, and those are the same statement. A route
/// carrying both markers therefore mints nothing, which is why <c>UserProvisioningRouteTests</c> holds
/// the two sets disjoint. Below that arm an authenticated request takes one of three ways:
/// </para>
/// <list type="bullet">
/// <item>the endpoint declares <see cref="ProvisionsUserAttribute" /> — find or create;</item>
/// <item>it declares nothing and the credential resolves — publish the identity and the budget;</item>
/// <item>it declares nothing and the credential resolves to no account — 401, and no row is written.</item>
/// </list>
/// <para>
/// <b>Why the anonymous arm comes first.</b> The claim gate exists to decide whether an address may be
/// <em>registered</em>; on a route that registers nothing it buys nothing and costs sign-in
/// availability. A client whose interceptor attaches the provider bearer to every <c>/api/</c> call
/// would otherwise have its passkey sign-in — the one exchange that must work without the provider —
/// refused over <c>email_verified</c>, a claim that ceremony never reads and never stores. Revoking the
/// email grant while leaving the application authorized would take away the way back in. Reading
/// <see cref="IAllowAnonymous" /> off the route is not a second definition of the anonymous surface: it
/// is the marker the route already carries for the authorization pipeline, so no exclusion list is
/// created here.
/// </para>
/// <para>
/// <b>Why the third way exists.</b> A provider id token stays valid for up to an hour after the account
/// it names has been erased. Minting on any authenticated request whose credential does not resolve
/// turns one in-flight poll or one forgotten second tab into a resurrected account — and the
/// resurrected account holds no passkey, so <c>POST /api/me/erasure</c> refuses it forever. Leaving
/// stopped meaning leaving.
/// </para>
/// <para>
/// <b>The claim gate stays above everything that can resolve or create an account.</b> The natural way
/// to write this method is to read the endpoint's metadata once and branch on both markers together,
/// which puts the marked path back on find-or-create before anybody has asked whether the address is
/// asserted verified. Only the anonymous arm may pass above the gate, and only because it reaches
/// neither resolution nor creation. The gate runs on every request rather than only on the first for the
/// reason recorded in the users-and-ownership documentation.
/// </para>
/// <para>
/// <b>What this does not fix.</b> A client that calls a <em>marked</em> endpoint on app boot still
/// resurrects an erased account for as long as the provider's id token lives. That hole is pre-existing;
/// it closes when registration becomes a consented act and the six markers collapse into one.
/// </para>
/// <para>
/// <b>The refused request needs nothing from <c>SessionContextInterceptor</c>, which stays as it is.</b>
/// The interceptor writes <c>''</c> for an unresolved value rather than skipping the setting, so any
/// policed statement on such a connection fails with <c>22P02</c> regardless. The <c>22P02</c> trap the
/// row-level-security decisions describe is a transaction opened <em>before</em> an identity that will
/// exist is published; here no identity will ever exist for this request, and it never reaches a
/// handler. Do not "fix" the interceptor on account of this path.
/// </para>
/// </remarks>
public sealed class UserProvisioningMiddleware(RequestDelegate next)
{
    /// <summary>
    /// The one sentence a request that authenticated as an account this product does not have receives.
    /// Public so a test can pin it: the caller's corrective action for this 401 is "sign in again", which
    /// is different from both other refusals reachable on the same path — one says the token is
    /// malformed, the other says a passkey was rejected — and a caller cannot act on a distinction the
    /// response does not make.
    /// </summary>
    public const string NoAccountTitle = "No account exists for the authenticated principal.";

    public async Task InvokeAsync(
        HttpContext httpContext,
        EnsureUserHandler ensureUserHandler,
        ResolveUserHandler resolveUserHandler,
        IUserContextWriter userContextWriter)
    {
        ClaimsPrincipal principal = httpContext.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            await next(httpContext);
            return;
        }

        // Populated here because WebApplication puts UseRouting at the very front of the pipeline, ahead
        // of every middleware registered in Program.cs — this one included, sitting between
        // UseAuthentication and UseAuthorization. A request matching no route leaves this null, which
        // reads as carrying neither marker: an authenticated caller with no account is then told 401
        // rather than 404, and a path that does not exist is the last place to start minting accounts.
        Endpoint? endpoint = httpContext.GetEndpoint();

        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(httpContext);
            return;
        }

        if (!TryGetRequiredClaim(principal, "sub", out string googleSubject) ||
            !TryGetRequiredClaim(principal, "email", out string email))
        {
            await Results.Problem(
                    title: "Authenticated principal is missing required claims.",
                    statusCode: StatusCodes.Status401Unauthorized)
                .ExecuteAsync(httpContext);
            return;
        }

        if (!HasVerifiedEmailClaim(principal))
        {
            await Results.Problem(
                    title: "Authenticated principal's email address is not asserted as verified.",
                    statusCode: StatusCodes.Status401Unauthorized)
                .ExecuteAsync(httpContext);
            return;
        }

        if (endpoint?.Metadata.GetMetadata<ProvisionsUserAttribute>() is not null)
        {
            // Only the subject and the verified email are read off the principal. Every other claim
            // the provider offers is deliberately left on the token: an account stores what it needs
            // to be reached, and a claim nothing reads is data we would be holding for no one.
            // email_verified is read and not stored: it decides whether the address may be
            // registered at all, and answers nothing about the person worth keeping afterwards.
            //
            // This is also the only branch the address reaches a handler on. The gate above reads it on
            // every request; ResolveUserCommand below carries no address at all, because nothing on that
            // path writes one.
            ProvisionedUser provisioned = await ensureUserHandler.HandleAsync(
                new EnsureUserCommand(googleSubject, email),
                httpContext.RequestAborted);

            Publish(userContextWriter, provisioned);
            await next(httpContext);
            return;
        }

        ProvisionedUser? resolved = await resolveUserHandler.HandleAsync(
            new ResolveUserCommand(googleSubject),
            httpContext.RequestAborted);

        if (resolved is null)
        {
            // The third refusal of the same shape this method produces, built the same way as the other
            // two — which is why it lives here rather than in an authorization requirement: an
            // authorization policy would answer a different question ("may this principal") than the one
            // asked here ("is there anyone to be").
            await Results.Problem(
                    title: NoAccountTitle,
                    statusCode: StatusCodes.Status401Unauthorized)
                .ExecuteAsync(httpContext);
            return;
        }

        Publish(userContextWriter, resolved);
        await next(httpContext);
    }

    /// <summary>
    /// Names the account and its budget through <see cref="IUserContextWriter" /> rather than by
    /// assigning the request-scoped state directly.
    /// </summary>
    /// <remarks>
    /// Going through the writer is the point, not a detail of style. <c>ResolveUser</c> carries the rule
    /// that publishing an identity clears the ambient budget, and a middleware that assigned both fields
    /// itself held that rule only by its own construction — leaving the next edit here free to publish an
    /// identity with a stranger's budget still standing beside it, with the whole suite green.
    /// <c>ResolveBudget</c> comes second because the first call clears it; nothing enforces that order,
    /// so <c>UserProvisioningWriterTests</c> pins it.
    /// </remarks>
    private static void Publish(IUserContextWriter userContextWriter, ProvisionedUser provisioned)
    {
        userContextWriter.ResolveUser(provisioned.UserId);
        userContextWriter.ResolveBudget(provisioned.BudgetId);
    }

    private static bool TryGetRequiredClaim(ClaimsPrincipal principal, string claimType, out string value)
    {
        value = principal.FindFirstValue(claimType) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    // Only a value bool.TryParse reads as true counts. Truthy dialects such as "1" are rejected:
    // no provider this codebase talks to emits one, so accepting one only widens the hole.
    private static bool HasVerifiedEmailClaim(ClaimsPrincipal principal) =>
        bool.TryParse(principal.FindFirstValue("email_verified"), out bool verified) && verified;
}
