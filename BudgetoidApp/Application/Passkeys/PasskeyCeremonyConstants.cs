using Domain.Users;

namespace Application.Passkeys;

/// <summary>
/// The fixed vocabulary both ceremonies speak, in one place because the two legs have to agree.
/// </summary>
public static class PasskeyCeremonyConstants
{
    /// <summary>The only credential type WebAuthn defines.</summary>
    public const string PublicKeyCredentialType = "public-key";

    /// <summary>
    /// Nothing about the authenticator's make or model is asked for. Under this setting the AAGUID
    /// arrives zeroed and no attestation statement is produced, which is what keeps registration from
    /// collecting a device fingerprint the product has no use for.
    /// </summary>
    public const string NoAttestation = "none";

    /// <summary>
    /// User verification is required, not preferred: a passkey is the credential that reaches budget
    /// content, so the authenticator has to prove a person was there, not merely that a key was.
    /// </summary>
    public const string RequiredUserVerification = "required";

    /// <summary>
    /// A discoverable credential is required rather than preferred. The sign-in leg sends no
    /// <c>allowCredentials</c> — it cannot, without first asking who is signing in — so an
    /// authenticator that stored nothing discoverable would have registered a credential nobody can
    /// ever use.
    /// </summary>
    public const string RequiredResidentKey = "required";

    /// <summary>
    /// The algorithms registration offers, and therefore the set a response is judged against.
    /// Declared once because <c>PasskeyRegistrationExpectations.OfferedAlgorithms</c> has to be the
    /// same list the options advertised — offering one set and accepting another is either a refusal
    /// of working authenticators or an acceptance of an algorithm nobody chose.
    /// </summary>
    public static readonly IReadOnlyList<CoseAlgorithm> OfferedAlgorithms = [CoseAlgorithm.Es256, CoseAlgorithm.Rs256];
}
