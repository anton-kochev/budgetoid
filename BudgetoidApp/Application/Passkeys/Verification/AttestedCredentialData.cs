namespace Application.Passkeys.Verification;

/// <summary>
/// The credential a registration ceremony created, as it sits inside authenticator data.
/// </summary>
/// <remarks>
/// The AAGUID that precedes both members is read past and deliberately not carried here. It names the
/// authenticator model, and this product enforces no authenticator allow-list, so a field nothing
/// reads would be one more thing stored about a person's hardware for no purpose.
/// </remarks>
public sealed record AttestedCredentialData
{
    /// <summary>The handle the authenticator answers to.</summary>
    public required ReadOnlyMemory<byte> CredentialId { get; init; }

    /// <summary>The COSE key exactly as the authenticator encoded it.</summary>
    public required ReadOnlyMemory<byte> CoseKey { get; init; }
}
