using System.Security.Claims;

namespace Api.Infrastructure;

/// <summary>
/// The two claim gates a provider token clears before the routes under <c>/api/registration</c> may
/// create anything: the principal has a usable <c>sub</c> and <c>email</c>, and the provider asserts
/// the address as verified.
/// </summary>
/// <remarks>
/// <para>
/// <b>These gates are the only thing between a provider token and an account created under an address
/// nobody verified.</b> They live in <see cref="UserProvisioningMiddleware"/> today as well, above its
/// resolve and above its <see cref="RegistersAccountAttribute"/> arm, and the two copies run together
/// for exactly one commit — the one that deletes the middleware is what proves this filter is carrying
/// them, because <c>RegistrationClaimGateTests</c> stays green through the deletion or the gate was
/// never here. While both stand, the two title constants must read byte for byte the same as the
/// middleware's: that file's copies are what the tests compare against, and a reworded pair here would
/// look like a passing move and be a silent divergence.
/// </para>
/// <para>
/// <b>Why an endpoint filter and not any of the three things that run earlier.</b> A
/// <c>RequireAssertion</c> on the group's policy, or a custom <c>IAuthorizationRequirement</c>, both run
/// before the delegate — and both answer <b>403 with no title</b>. That collapses two refusals a caller
/// acts on differently ("your token is unusable" and "your provider does not vouch for this address")
/// into one untitled status, which is the distinction <c>RegistrationClaimGateTests</c> exists to hold.
/// <c>JwtBearerEvents.OnTokenValidated</c> runs earliest of all and <em>can</em> answer 401, but writing
/// a titled ProblemDetails from there needs <c>OnChallenge</c> written too, and the gate then becomes a
/// property of the <em>scheme</em> rather than of the route — invisible to anybody reading the route
/// table, which is where every other rule about who may reach these two routes is declared. And a new
/// middleware reading a new marker is <see cref="RegistersAccountAttribute"/> under a different name:
/// the same opt-in metadata, the same silence when a group forgets it.
/// </para>
/// <para>
/// <b>The accepted behaviour change: 400 can now overtake 401.</b> A filter runs after model binding, so
/// a caller sending an unverified address <em>and</em> a malformed body is now answered 400 by the
/// framework where the middleware answered 401. Nothing in the suite measures that ordering. It was put
/// to the repository owner and accepted, and the cost is worth saying plainly: such a caller learns
/// their body is wrong before they learn their address was never going to be accepted, which is a worse
/// order to debug in. It is not a disclosure — a deserialization failure is a fact about the caller's
/// own request and says nothing about what this server stores or about whose address is registered.
/// </para>
/// <para>
/// <b>Why the gates live here and not in the Application ring.</b>
/// <c>RegisterAccountHandler</c> promised, and the registration documentation promised with it, that
/// these three rungs would "come down into this layer in the commit that deletes the middleware".
/// <b>That promise cannot be kept and is being corrected rather than fulfilled.</b> Judging
/// <c>email_verified</c> needs one of two things, and the ring may have neither: a
/// <see cref="ClaimsPrincipal"/> inside <c>Application</c> — against the rule
/// <c>RegistrationEndpoints</c> states where it reads the two claim members off the principal at the
/// call site, which is what keeps <c>System.Security.Claims</c> out of that project altogether — or a
/// field on <c>RegisterAccountCommand</c> for the answer to land in, which the users-and-ownership
/// documentation argues against by name: the verified-email claim is read and never stored, and the
/// command carries only the subject and the address precisely so there is nowhere for it to go. What is
/// left is the boundary that already holds the principal, which is this one.
/// </para>
/// <para>
/// Applied to the group with <c>AddEndpointFilter</c> beside its <c>RequireAuthorization</c>, and read
/// off the route table there rather than opted into with a marker — see the argument above for why a
/// marker would be the middleware again.
/// </para>
/// </remarks>
public sealed class RegistrationClaimGate : IEndpointFilter
{
    /// <summary>
    /// The 401 an authenticated principal carrying no <c>sub</c> or no <c>email</c> receives. Public so
    /// a test can pin it: distinctness from the other titled refusals on this path is a property only a
    /// test naming both can hold, and a caller cannot act on a distinction the response does not make.
    /// </summary>
    public const string MissingClaimsTitle = "Authenticated principal is missing required claims.";

    /// <summary>
    /// The 401 a principal whose address the provider does not assert as verified receives. Public for
    /// the same reason as <see cref="MissingClaimsTitle" />.
    /// </summary>
    public const string UnverifiedEmailTitle =
        "Authenticated principal's email address is not asserted as verified.";

    /// <summary>
    /// Refuses the request before the handler is entered, or hands it on unchanged.
    /// </summary>
    /// <remarks>
    /// Neither claim value is carried out of this method. The route delegate reads the subject and the
    /// address off the principal itself, and a value plumbed through here would be a second source for
    /// them able to disagree with the first.
    /// </remarks>
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        ClaimsPrincipal principal = context.HttpContext.User;

        // One sentence for both halves, deliberately: telling a caller which of the two the server
        // could not read describes the token they are already holding.
        if (!HasRequiredClaim(principal, "sub") || !HasRequiredClaim(principal, "email"))
        {
            return Refuse(MissingClaimsTitle);
        }

        if (!HasVerifiedEmailClaim(principal))
        {
            return Refuse(UnverifiedEmailTitle);
        }

        return next(context);
    }

    /// <summary>
    /// The refusal, as the filter pipeline's own result rather than a write to the response.
    /// </summary>
    /// <remarks>
    /// <c>Results.Problem</c> is what makes the body <c>application/problem+json</c> with a
    /// <c>title</c> on it — the shape the middleware's copies produce, and the shape the tests read.
    /// Not <see langword="async"/>, so a refusal allocates no state machine and the admitting path
    /// returns the inner delegate's <see cref="ValueTask{TResult}"/> untouched.
    /// </remarks>
    private static ValueTask<object?> Refuse(string title) =>
        ValueTask.FromResult<object?>(
            Results.Problem(title: title, statusCode: StatusCodes.Status401Unauthorized));

    /// <summary>
    /// Whether the principal carries a claim of this type with something in it.
    /// </summary>
    /// <remarks>
    /// <b>Whitespace counts as missing.</b> A subject of three spaces is not an identifier a later
    /// sign-in can be matched on, and an address of three spaces is not one anybody can be reached at;
    /// admitting either would file a row keyed on nothing.
    /// </remarks>
    private static bool HasRequiredClaim(ClaimsPrincipal principal, string claimType) =>
        !string.IsNullOrWhiteSpace(principal.FindFirstValue(claimType));

    // Only a value bool.TryParse reads as true counts. Truthy dialects such as "1" are rejected:
    // no provider this codebase talks to emits one, so accepting one only widens the hole.
    private static bool HasVerifiedEmailClaim(ClaimsPrincipal principal) =>
        bool.TryParse(principal.FindFirstValue("email_verified"), out bool verified) && verified;
}
