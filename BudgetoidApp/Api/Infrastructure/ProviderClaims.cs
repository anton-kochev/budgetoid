using System.Security.Claims;

namespace Api.Infrastructure;

/// <summary>
/// What an identity provider's principal must say before anything may be filed under it: a usable
/// <c>sub</c> and <c>email</c>, and the address asserted as verified.
/// </summary>
/// <remarks>
/// <para>
/// <b>One judgement, two gates.</b> <see cref="RegistrationClaimGate" /> asks it of the principal the
/// registration group's policy — or the locked sign-in's — authenticated;
/// <see cref="ProviderAuthorizationGate" /> asks it of the
/// principal it authenticates itself, beside a session. The two answer in different shapes — registration
/// by title alone, the email change with a <c>refusal</c> word — so this class returns the verdict and
/// never a response. A second copy of the three checks is how one gate starts admitting <c>"1"</c> as
/// verified while the other refuses it.
/// </para>
/// <para>
/// It reads only the principal it is handed. Which principal that is — and that it is never the
/// session's, whose <c>sub</c> is an account id — is each caller's decision to make and to argue.
/// </para>
/// </remarks>
public static class ProviderClaims
{
    /// <summary>The provider's stable identifier for the person.</summary>
    public const string SubjectClaimType = "sub";

    /// <summary>The address the provider asserts for the person.</summary>
    public const string EmailClaimType = "email";

    /// <summary>Whether the provider vouches for <see cref="EmailClaimType" />.</summary>
    public const string EmailVerifiedClaimType = "email_verified";

    /// <summary>Why a provider principal was refused.</summary>
    public enum Refusal
    {
        /// <summary>No usable <c>sub</c>, or no usable <c>email</c>.</summary>
        MissingClaims,

        /// <summary>The provider does not assert the address as verified.</summary>
        UnverifiedEmail,
    }

    /// <summary>
    /// The refusal <paramref name="principal" /> earns, or <see langword="null" /> when it carries
    /// everything a provider identity has to.
    /// </summary>
    /// <remarks>
    /// The missing-claim check runs first, so a principal lacking both an address and a verified flag is
    /// told its token is unusable rather than that an address it never sent is unverified.
    /// </remarks>
    public static Refusal? RefusalFor(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!HasRequiredClaim(principal, SubjectClaimType) || !HasRequiredClaim(principal, EmailClaimType))
        {
            return Refusal.MissingClaims;
        }

        return HasVerifiedEmailClaim(principal) ? null : Refusal.UnverifiedEmail;
    }

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
        bool.TryParse(principal.FindFirstValue(EmailVerifiedClaimType), out bool verified) && verified;
}
