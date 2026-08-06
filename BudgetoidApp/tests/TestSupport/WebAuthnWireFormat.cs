using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Passkeys.Verification;

namespace TestSupport;

/// <summary>
/// Encoders for the three WebAuthn wire structures, written from the specification rather than from
/// the verifier that reads them.
/// </summary>
/// <remarks>
/// This is the second implementation of the format, and that is the point: the verifier decodes what
/// this encodes, so a misreading on either side shows up as a failing test instead of as two halves
/// of one misunderstanding agreeing with each other. Every deviation a negative test needs is a
/// parameter here, so a test changes exactly one value away from a response that would be accepted.
/// </remarks>
public static class WebAuthnWireFormat
{
    private const int AaguidLength = 16;

    /// <summary>COSE key common parameter labels.</summary>
    private const int LabelKeyType = 1;

    private const int LabelAlgorithm = 3;
    private const int LabelNegativeOne = -1;
    private const int LabelNegativeTwo = -2;
    private const int LabelNegativeThree = -3;

    /// <summary>COSE <c>kty</c> values.</summary>
    public const int KeyTypeEc2 = 2;

    /// <summary>COSE <c>kty</c> for RSA.</summary>
    public const int KeyTypeRsa = 3;

    /// <summary>COSE <c>crv</c> for P-256.</summary>
    public const int CurveP256 = 1;

    /// <summary>SHA-256 of a relying party id — what authenticator data actually carries.</summary>
    public static byte[] RelyingPartyIdHash(string relyingPartyId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(relyingPartyId));

    /// <summary>
    /// Builds a <c>clientDataJSON</c> document as the client would.
    /// </summary>
    public static byte[] EncodeClientDataJson(
        string type,
        ReadOnlySpan<byte> challenge,
        string origin,
        bool crossOrigin = false)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("challenge", Base64UrlText.Encode(challenge));
            writer.WriteString("origin", origin);
            writer.WriteBoolean("crossOrigin", crossOrigin);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Assembles an authenticator data block.
    /// </summary>
    /// <param name="relyingPartyIdHash">The 32-byte hash that opens the block.</param>
    /// <param name="flags">The packed flag byte; the caller decides which bits are set.</param>
    /// <param name="signCount">Written big-endian, as the format requires.</param>
    /// <param name="credentialId">Present exactly when attested credential data is included.</param>
    /// <param name="coseKey">The key that follows the credential id, with no length prefix.</param>
    /// <param name="extensions">
    /// The bytes appended after everything else. Written whenever they are supplied rather than when
    /// ED is set, so the flag and the block are two independent deviations: a flag with no block and
    /// a block with no flag are both reachable.
    /// </param>
    /// <param name="declaredCredentialIdLengthOverride">
    /// The <c>credentialIdLength</c> actually written, when it should disagree with how many bytes
    /// follow. A length that overruns the buffer is a value the authenticator supplies, so it takes a
    /// deliberately wrong one to reach the check that rejects it.
    /// </param>
    /// <param name="truncateToLength">
    /// Cuts the finished block to this many bytes. Applied last, so it reaches any of the three
    /// bounds — the fixed header, the attested credential data, the extension block — without the
    /// caller computing an offset.
    /// </param>
    public static byte[] EncodeAuthenticatorData(
        ReadOnlySpan<byte> relyingPartyIdHash,
        AuthenticatorDataFlags flags,
        uint signCount,
        ReadOnlySpan<byte> credentialId = default,
        ReadOnlySpan<byte> coseKey = default,
        ReadOnlySpan<byte> extensions = default,
        ushort? declaredCredentialIdLengthOverride = null,
        int? truncateToLength = null)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(relyingPartyIdHash.Length, AuthenticatorData.RpIdHashLength);

        using MemoryStream buffer = new();
        buffer.Write(relyingPartyIdHash);
        buffer.WriteByte((byte)flags);

        Span<byte> signCountBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(signCountBytes, signCount);
        buffer.Write(signCountBytes);

        if ((flags & AuthenticatorDataFlags.AttestedCredentialData) != 0)
        {
            // All-zero AAGUID: what an authenticator reports when attestation was not requested.
            buffer.Write(new byte[AaguidLength]);

            Span<byte> credentialIdLength = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16BigEndian(
                credentialIdLength,
                declaredCredentialIdLengthOverride ?? (ushort)credentialId.Length);
            buffer.Write(credentialIdLength);

            buffer.Write(credentialId);
            buffer.Write(coseKey);
        }

        if (!extensions.IsEmpty)
        {
            buffer.Write(extensions);
        }

        byte[] block = buffer.ToArray();

        return truncateToLength is { } length ? block[..length] : block;
    }

    /// <summary>
    /// Wraps authenticator data in the CBOR attestation object a registration response carries.
    /// </summary>
    /// <param name="format">The <c>fmt</c> the object names.</param>
    /// <param name="authenticatorData">The block <c>authData</c> carries.</param>
    /// <param name="encodeAuthDataAsTextString">
    /// Writes <c>authData</c> as a CBOR text string instead of a byte string, which is the type
    /// confusion a reader that trusts the label rather than the major type would step into.
    /// </param>
    /// <param name="omitAuthData">Leaves the member out entirely — a different arm from the wrong type.</param>
    public static byte[] EncodeAttestationObject(
        string format,
        ReadOnlySpan<byte> authenticatorData,
        bool encodeAuthDataAsTextString = false,
        bool omitAuthData = false)
    {
        CborWriter writer = new();
        writer.WriteStartMap(omitAuthData ? 2 : 3);
        writer.WriteTextString("fmt");
        writer.WriteTextString(format);
        writer.WriteTextString("attStmt");
        writer.WriteStartMap(0);
        writer.WriteEndMap();

        if (!omitAuthData)
        {
            writer.WriteTextString("authData");
            if (encodeAuthDataAsTextString)
            {
                writer.WriteTextString(Convert.ToBase64String(authenticatorData));
            }
            else
            {
                writer.WriteByteString(authenticatorData);
            }
        }

        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>
    /// Encodes an EC2 COSE key. Every label is a parameter so a test can name a key type or curve
    /// that disagrees with the algorithm without a second encoder existing to do it.
    /// </summary>
    public static byte[] EncodeEc2CoseKey(
        int keyType,
        int algorithm,
        int curve,
        ReadOnlySpan<byte> x,
        ReadOnlySpan<byte> y)
    {
        CborWriter writer = new();
        writer.WriteStartMap(5);
        writer.WriteInt32(LabelKeyType);
        writer.WriteInt32(keyType);
        writer.WriteInt32(LabelAlgorithm);
        writer.WriteInt32(algorithm);
        writer.WriteInt32(LabelNegativeOne);
        writer.WriteInt32(curve);
        writer.WriteInt32(LabelNegativeTwo);
        writer.WriteByteString(x);
        writer.WriteInt32(LabelNegativeThree);
        writer.WriteByteString(y);
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>
    /// Encodes an RSA COSE key.
    /// </summary>
    public static byte[] EncodeRsaCoseKey(
        int keyType,
        int algorithm,
        ReadOnlySpan<byte> modulus,
        ReadOnlySpan<byte> exponent)
    {
        CborWriter writer = new();
        writer.WriteStartMap(4);
        writer.WriteInt32(LabelKeyType);
        writer.WriteInt32(keyType);
        writer.WriteInt32(LabelAlgorithm);
        writer.WriteInt32(algorithm);
        writer.WriteInt32(LabelNegativeOne);
        writer.WriteByteString(modulus);
        writer.WriteInt32(LabelNegativeTwo);
        writer.WriteByteString(exponent);
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>
    /// A minimal, well-formed extension-output map for the block ED announces.
    /// </summary>
    /// <remarks>
    /// Its only job is to occupy bytes after the COSE key, which is what proves the key reader finds
    /// its own end rather than running to the end of the block.
    /// </remarks>
    public static byte[] EncodeExtensionOutputs()
    {
        CborWriter writer = new();
        writer.WriteStartMap(1);
        writer.WriteTextString("credProtect");
        writer.WriteInt32(2);
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>
    /// A well-formed CBOR value that is not a map, for the block ED announces.
    /// </summary>
    /// <remarks>
    /// Well-formed on purpose: a reader that only checks whether the bytes decode as CBOR at all
    /// accepts this, and only one that insists on a map refuses it.
    /// </remarks>
    public static byte[] EncodeNonMapExtensionOutputs()
    {
        CborWriter writer = new();
        writer.WriteTextString("credProtect");

        return writer.Encode();
    }
}
