using System.Security.Cryptography;
using Domain.Users;

namespace Application.Passkeys.Verification;

/// <summary>
/// The signature check of an assertion, on its own.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="PasskeyAssertionVerifier"/>: a captured ceremony can be run
/// against the cryptography without also being run against the current policy ladder, which is what
/// lets a real-device vector pin the signature format even when it predates a policy the product has
/// since adopted.
/// </remarks>
public static class PasskeySignatureVerifier
{
    /// <summary>
    /// Verifies a WebAuthn signature over <c>authenticatorData || SHA-256(clientDataJSON)</c>.
    /// </summary>
    /// <returns>Null when the signature verifies, otherwise the refusal.</returns>
    public static PasskeyVerificationFailure? Verify(
        ReadOnlyMemory<byte> coseKey,
        CoseAlgorithm algorithm,
        ReadOnlyMemory<byte> authenticatorData,
        ReadOnlyMemory<byte> clientDataJson,
        ReadOnlyMemory<byte> signature)
    {
        if (!CoseKeyMaterial.Decode(coseKey).TryGetValue(out CoseKeyMaterial? key, out PasskeyVerificationFailure failure))
        {
            return failure;
        }

        // The stored algorithm is the authority. A stored key whose own header names a different one
        // than the column says is a corrupted record, not something to resolve in favour of either.
        if (key.Algorithm != algorithm)
        {
            return PasskeyVerificationFailure.AlgorithmMismatch;
        }

        return key.VerifySignature(BuildSignedData(authenticatorData, clientDataJson).Span, signature.Span)
            ? null
            : PasskeyVerificationFailure.SignatureInvalid;
    }

    /// <summary>
    /// Concatenates the bytes the authenticator signed.
    /// </summary>
    private static ReadOnlyMemory<byte> BuildSignedData(
        ReadOnlyMemory<byte> authenticatorData,
        ReadOnlyMemory<byte> clientDataJson)
    {
        // Hashed from the bytes as received. Parsing the client data and re-serialising it produces
        // different bytes — key order, escaping, whitespace — and therefore a different hash, so the
        // signature would never verify no matter how correct everything else is.
        byte[] signedData = new byte[authenticatorData.Length + SHA256.HashSizeInBytes];
        authenticatorData.Span.CopyTo(signedData);
        SHA256.HashData(clientDataJson.Span, signedData.AsSpan(authenticatorData.Length));

        return signedData;
    }
}
