using Domain.Users;

namespace Application.Passkeys.Verification;

/// <summary>
/// What an accepted registration ceremony yields for the caller to store.
/// </summary>
public sealed record VerifiedRegistration
{
    /// <summary>The handle the authenticator answers to.</summary>
    public required ReadOnlyMemory<byte> WebAuthnCredentialId { get; init; }

    /// <summary>The public key as the authenticator encoded it, byte for byte.</summary>
    public required ReadOnlyMemory<byte> CoseKey { get; init; }

    public required CoseAlgorithm Algorithm { get; init; }

    /// <summary>
    /// The counter the authenticator reported. Seeding the stored counter is the caller's job.
    /// </summary>
    public required uint SignCount { get; init; }
}
