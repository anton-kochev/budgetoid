using Domain.Users;

namespace Application.Passkeys.Verification;

/// <summary>
/// What the server issued and will accept for one registration ceremony.
/// </summary>
public sealed record PasskeyRegistrationExpectations
{
    /// <summary>
    /// The challenge this ceremony issued. Consuming it so it cannot be replayed belongs to the
    /// caller; this type only says what the response has to match.
    /// </summary>
    public required ReadOnlyMemory<byte> Challenge { get; init; }

    /// <summary>Origins compared by equality — never by prefix.</summary>
    public required IReadOnlyCollection<string> AllowedOrigins { get; init; }

    public required string RelyingPartyId { get; init; }

    /// <summary>
    /// The algorithms the credential creation options offered. An authenticator answering with one
    /// that was never offered is refused rather than accommodated.
    /// </summary>
    public required IReadOnlyCollection<CoseAlgorithm> OfferedAlgorithms { get; init; }
}
