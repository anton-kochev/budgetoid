using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace Application.Passkeys.Verification;

/// <summary>
/// The members of <c>clientDataJSON</c> both ceremonies are decided on.
/// </summary>
/// <remarks>
/// Only the four members WebAuthn gives meaning to are surfaced. A real response carries more —
/// <c>androidPackageName</c>, <c>clientExtensions</c>, <c>hashAlgorithm</c> — and a parser that treats
/// an unknown member as a fault would refuse working authenticators, so unknown members are ignored.
/// </remarks>
public sealed record CollectedClientData
{
    public const string RegistrationType = "webauthn.create";
    public const string AuthenticationType = "webauthn.get";

    /// <summary>The ceremony the client believes it ran.</summary>
    public required string Type { get; init; }

    /// <summary>The challenge, decoded from its base64url text.</summary>
    public required ReadOnlyMemory<byte> Challenge { get; init; }

    /// <summary>The origin, decoded — the JSON text may escape its slashes.</summary>
    public required string Origin { get; init; }

    public required bool CrossOrigin { get; init; }

    /// <summary>
    /// Reads the client data from its original UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// The bytes are parsed where they are and never re-encoded: the assertion signature covers the
    /// exact bytes the client sent, so a value round-tripped through a string and back changes the
    /// hash and refuses every real signature.
    /// </remarks>
    public static PasskeyVerificationResult<CollectedClientData> Parse(ReadOnlyMemory<byte> clientDataJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(clientDataJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return Refused(PasskeyVerificationFailure.MalformedClientData);
            }

            if (!TryGetString(root, "type", out string? type)
                || !TryGetString(root, "challenge", out string? challengeText)
                || !TryGetString(root, "origin", out string? origin))
            {
                return Refused(PasskeyVerificationFailure.MalformedClientData);
            }

            // Base64Url decodes the WebAuthn alphabet directly. Substituting '-' and '_' by hand and
            // re-padding is where hand-rolled decoders accept strings the spec does not.
            if (!Base64Url.IsValid(challengeText))
            {
                return Refused(PasskeyVerificationFailure.MalformedClientData);
            }

            // Absent means same-origin. Only an explicit true is a cross-origin ceremony; anything
            // else in that slot is a malformed response rather than a permissive default.
            bool crossOrigin = false;
            if (root.TryGetProperty("crossOrigin", out JsonElement crossOriginElement))
            {
                if (crossOriginElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return Refused(PasskeyVerificationFailure.MalformedClientData);
                }

                crossOrigin = crossOriginElement.GetBoolean();
            }

            return PasskeyVerificationResult<CollectedClientData>.Verified(new CollectedClientData
            {
                Type = type,
                Challenge = Base64Url.DecodeFromChars(challengeText),
                Origin = origin,
                CrossOrigin = crossOrigin,
            });
        }
        catch (JsonException)
        {
            return Refused(PasskeyVerificationFailure.MalformedClientData);
        }
    }

    /// <summary>
    /// Applies the checks both ceremonies share, returning null when the client data is acceptable.
    /// </summary>
    public PasskeyVerificationFailure? Verify(
        string expectedType,
        ReadOnlyMemory<byte> expectedChallenge,
        IReadOnlyCollection<string> allowedOrigins)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedType);
        ArgumentNullException.ThrowIfNull(allowedOrigins);

        // A registration response replayed into the assertion endpoint, or the reverse, is refused
        // here: the two ceremonies are not interchangeable and the type is what separates them.
        if (!string.Equals(Type, expectedType, StringComparison.Ordinal))
        {
            return PasskeyVerificationFailure.UnexpectedCeremonyType;
        }

        // Equality against an allow-list, never StartsWith or Contains: both of those accept
        // https://budgetoid.app.attacker.example, which is a different site entirely.
        if (!ContainsOrdinal(allowedOrigins, Origin))
        {
            return PasskeyVerificationFailure.UntrustedOrigin;
        }

        if (CrossOrigin)
        {
            return PasskeyVerificationFailure.CrossOriginNotAllowed;
        }

        // Fixed-time because the comparand is a secret nonce: a length-prefix-style early exit tells
        // a caller how much of a guessed challenge was right.
        if (!CryptographicOperations.FixedTimeEquals(Challenge.Span, expectedChallenge.Span))
        {
            return PasskeyVerificationFailure.ChallengeMismatch;
        }

        return null;
    }

    private static bool ContainsOrdinal(IReadOnlyCollection<string> origins, string origin)
    {
        foreach (string allowed in origins)
        {
            if (string.Equals(allowed, origin, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetString(JsonElement root, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        // GetString unescapes, so the vector's "https:\/\/dev.dontneeda.pw" comes out as the origin
        // it names. Comparing raw JSON text instead would fail on an escape the client was free to
        // emit.
        value = element.GetString();

        return value is not null;
    }

    private static PasskeyVerificationResult<CollectedClientData> Refused(PasskeyVerificationFailure failure) =>
        PasskeyVerificationResult<CollectedClientData>.Refused(failure);
}
