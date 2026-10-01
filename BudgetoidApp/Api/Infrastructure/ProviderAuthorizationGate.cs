using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Api.Infrastructure;

/// <summary>
/// Authenticates the identity provider's token on a route the session already authenticated, judges its
/// claims, and hands the vouched subject and address to the route — or refuses with a titled 401.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two principals, never merged.</b> The session cookie authenticates the request on the fallback
/// policy, and that principal's <c>sub</c> is this installation's <em>account id</em>. The provider's
/// token is a second proof on the same request, and its <c>sub</c> is a Google subject. So this filter
/// calls <see cref="AuthenticationHttpContextExtensions.AuthenticateAsync(HttpContext, string)" /> itself
/// and judges <em>that</em> result, never <see cref="HttpContext.User" />: a filter that read the
/// ambient principal would find a perfectly usable <c>sub</c> — the account's own id — and file a
/// credential under it. <c>AuthenticateAsync</c> returns a result and does not replace
/// <see cref="HttpContext.User" />, so the session stays the request's identity for everything else.
/// </para>
/// <para>
/// <b>Not a policy scheme, and that is not an oversight.</b> Adding the provider scheme to the route's
/// policy would make <c>AuthorizationMiddleware</c> merge both principals into
/// <see cref="HttpContext.User" />, and a merged principal carries two <c>sub</c> claims — whichever one
/// a reader found first would decide whose identity was filed. It would also take the route off the
/// fallback policy and its <see cref="FullSessionRequirement" />. The provider scheme stays reachable
/// through one policy, registration's, and through this filter.
/// </para>
/// <para>
/// <b>A filter, so it runs before the handler and after model binding.</b> Before the handler means a
/// provider refusal writes nothing: the gate inside the handler, which consumes the passkey challenge's
/// nonce, is never reached, so the nonce is left unspent and expires. After model binding is the accepted cost <see cref="RegistrationClaimGate" />
/// argues — a malformed body is a framework 400 before the token is looked at.
/// </para>
/// <para>
/// <b>The claim checks are <see cref="ProviderClaims" />'s</b>, the same three registration applies, so
/// the two surfaces cannot disagree about what "verified" means. The response is this filter's own: a
/// <c>refusal</c> word a client branches on, because on this route a 401 has three causes that need three
/// different next steps — sign in to Google again, choose an address Google verifies, or retry the
/// passkey — and the session is not among them.
/// </para>
/// <para>
/// <b>It writes nothing to the log.</b> The subject, the address and the token are all values the
/// product owes nobody a record of, and the refusal word is on the response.
/// </para>
/// <para>
/// Applied by <see cref="ProviderAuthorizationEndpointExtensions.RequireProviderAuthorization{TBuilder}" />,
/// which also adds <see cref="RequiresProviderAuthorizationMetadata" /> so a census can read off the
/// route table which routes reach the provider scheme this way.
/// </para>
/// </remarks>
public sealed class ProviderAuthorizationGate : IEndpointFilter
{
    /// <summary>The refusal word for a token that is absent, invalid, or missing <c>sub</c> or <c>email</c>.</summary>
    public const string ProviderTokenRefusal = "provider_token";

    /// <summary>The refusal word for a token whose address the provider does not assert as verified.</summary>
    public const string EmailUnverifiedRefusal = "email_unverified";

    /// <summary>The title a <see cref="ProviderTokenRefusal" /> carries.</summary>
    public const string ProviderTokenTitle = "The identity provider's token could not be used.";

    /// <summary>The title an <see cref="EmailUnverifiedRefusal" /> carries.</summary>
    public const string EmailUnverifiedTitle = "The identity provider does not assert this address as verified.";

    /// <summary>
    /// The subject and address the provider vouched for on this request, as this filter judged them.
    /// </summary>
    /// <remarks>
    /// A type of its own on <see cref="HttpContext.Features" />, rather than a string key in
    /// <see cref="HttpContext.Items" />, so nothing but this filter can set it by accident and a route can
    /// only read what was written here.
    /// </remarks>
    /// <param name="Subject">The provider's <c>sub</c> claim.</param>
    /// <param name="Email">The provider's <c>email</c> claim.</param>
    public sealed record VouchedIdentity(string Subject, string Email);

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext httpContext = context.HttpContext;

        // Absent, forged, expired, another audience, another issuer: every one of them is a result that
        // did not succeed, and every one is the same refusal. Which check the handler failed describes
        // the token the caller is already holding.
        AuthenticateResult result = await httpContext.AuthenticateAsync(ProviderAuthentication.SchemeName);
        if (result is not { Succeeded: true, Principal: { } principal })
        {
            return Refuse(ProviderTokenRefusal, ProviderTokenTitle);
        }

        switch (ProviderClaims.RefusalFor(principal))
        {
            case null:
                break;
            case ProviderClaims.Refusal.MissingClaims:
                return Refuse(ProviderTokenRefusal, ProviderTokenTitle);
            case ProviderClaims.Refusal.UnverifiedEmail:
                return Refuse(EmailUnverifiedRefusal, EmailUnverifiedTitle);
            case var unexpected:
                throw new ArgumentOutOfRangeException(
                    nameof(context), unexpected, "A provider claim refusal was added and nobody worded it.");
        }

        // The empty fallbacks are unreachable — RefusalFor has just refused a principal missing either —
        // and exist so this has a total answer rather than a null-forgiving operator asserting a rule
        // checked one line up.
        httpContext.Features.Set(new VouchedIdentity(
            principal.FindFirstValue(ProviderClaims.SubjectClaimType) ?? string.Empty,
            principal.FindFirstValue(ProviderClaims.EmailClaimType) ?? string.Empty));

        return await next(context);
    }

    /// <summary>
    /// The identity this filter vouched for on <paramref name="httpContext" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The route does not carry this filter. A 500 on purpose: the alternative a route could fall back to
    /// is <see cref="HttpContext.User" />, whose <c>sub</c> is the account id.
    /// </exception>
    public static VouchedIdentity IdentityOf(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Features.Get<VouchedIdentity>()
            ?? throw new InvalidOperationException(
                $"The route read a provider identity without {nameof(ProviderAuthorizationGate)} on it; "
                + "call RequireProviderAuthorization() on the route.");
    }

    /// <summary>
    /// The refusal, as the filter pipeline's own result: <c>application/problem+json</c> with a title and
    /// the <c>refusal</c> word.
    /// </summary>
    private static IResult Refuse(string refusal, string title) =>
        Results.Problem(
            title: title,
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?> { [RefusalMember.Name] = refusal });
}
