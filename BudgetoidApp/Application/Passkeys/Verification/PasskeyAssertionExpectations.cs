using Domain.Users;

namespace Application.Passkeys.Verification;

/// <summary>
/// What the server issued and stored, against which one assertion is judged.
/// </summary>
public sealed record PasskeyAssertionExpectations
{
    /// <summary>The challenge this ceremony issued.</summary>
    public required ReadOnlyMemory<byte> Challenge { get; init; }

    /// <summary>Origins compared by equality — never by prefix.</summary>
    public required IReadOnlyCollection<string> AllowedOrigins { get; init; }

    public required string RelyingPartyId { get; init; }

    /// <summary>The COSE key recorded at registration, not one read out of this request.</summary>
    public required ReadOnlyMemory<byte> CoseKey { get; init; }

    /// <summary>
    /// The algorithm recorded at registration. Reading the algorithm out of the request would let a
    /// caller name the one whose verification it can satisfy.
    /// </summary>
    public required CoseAlgorithm Algorithm { get; init; }
}
