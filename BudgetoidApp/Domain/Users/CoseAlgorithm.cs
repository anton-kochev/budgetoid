namespace Domain.Users;

/// <summary>
/// The signature algorithm a passkey's public key is verified with, named by its IANA COSE
/// identifier.
/// </summary>
/// <remarks>
/// The members carry their numeric protocol values rather than persisting as lowercase text the way
/// <see cref="CredentialType"/> and <see cref="Domain.Sessions.SessionKind"/> do. These numbers are
/// what the credential creation options offer and what the COSE key itself carries, so a second
/// textual vocabulary would be one more thing to keep in step with the protocol for no reader's
/// benefit.
/// </remarks>
public enum CoseAlgorithm
{
    Es256 = -7,
    Rs256 = -257,
}
