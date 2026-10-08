using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;

namespace Application.Passkeys.Verification;

/// <summary>
/// The authenticator data block both ceremonies carry, decoded.
/// </summary>
/// <remarks>
/// Parsing is exposed on its own, separate from the policy checks, so a captured ceremony can be run
/// against the wire format without also being run against the current policy ladder.
/// </remarks>
public sealed record AuthenticatorData
{
    /// <summary>SHA-256 of the relying party id — the id itself is never on the wire.</summary>
    public const int RpIdHashLength = 32;

    /// <summary>rpIdHash (32) + flags (1) + signCount (4); everything after this is optional.</summary>
    public const int FixedHeaderLength = 37;

    private const int FlagsOffset = 32;
    private const int SignCountOffset = 33;
    private const int AaguidLength = 16;
    private const int CredentialIdLengthLength = 2;

    public required ReadOnlyMemory<byte> RpIdHash { get; init; }

    public required AuthenticatorDataFlags Flags { get; init; }

    public required uint SignCount { get; init; }

    /// <summary>The attested credential, present exactly when the AT flag is set.</summary>
    public required AttestedCredentialData? AttestedCredential { get; init; }

    public bool IsUserPresent => (Flags & AuthenticatorDataFlags.UserPresent) != 0;

    public bool IsUserVerified => (Flags & AuthenticatorDataFlags.UserVerified) != 0;

    /// <summary>
    /// Decodes the fixed header, the attested credential data when AT is set, and validates the
    /// extension block when ED is set.
    /// </summary>
    public static PasskeyVerificationResult<AuthenticatorData> Parse(ReadOnlyMemory<byte> authenticatorData)
    {
        if (authenticatorData.Length < FixedHeaderLength)
        {
            return Refused(PasskeyVerificationFailure.MalformedAuthenticatorData);
        }

        ReadOnlySpan<byte> span = authenticatorData.Span;
        AuthenticatorDataFlags flags = (AuthenticatorDataFlags)span[FlagsOffset];

        // Big-endian, explicitly. BitConverter reads little-endian on every platform this runs on and
        // would turn a counter of 144 into 2415919104 without failing anywhere a test would notice.
        uint signCount = BinaryPrimitives.ReadUInt32BigEndian(span[SignCountOffset..FixedHeaderLength]);

        int offset = FixedHeaderLength;
        AttestedCredentialData? attestedCredential = null;

        if ((flags & AuthenticatorDataFlags.AttestedCredentialData) != 0)
        {
            if (authenticatorData.Length < offset + AaguidLength + CredentialIdLengthLength)
            {
                return Refused(PasskeyVerificationFailure.MalformedAuthenticatorData);
            }

            // The AAGUID sits here and is stepped over, never returned. See AttestedCredentialData.
            offset += AaguidLength;

            int credentialIdLength = BinaryPrimitives.ReadUInt16BigEndian(
                span.Slice(offset, CredentialIdLengthLength));
            offset += CredentialIdLengthLength;

            // A length the authenticator supplied, so it is checked against what is actually there
            // before it is used to slice.
            if (credentialIdLength > authenticatorData.Length - offset)
            {
                return Refused(PasskeyVerificationFailure.MalformedAuthenticatorData);
            }

            ReadOnlyMemory<byte> credentialId = authenticatorData.Slice(offset, credentialIdLength);
            offset += credentialIdLength;

            // The COSE key carries no length prefix and simply runs on, so the only thing that says
            // where it ends is how much of it the CBOR reader consumed — BytesRemaining against the
            // buffer it was given. Assuming the key runs to the end of the block loses the extension
            // outputs when ED is set; assuming a fixed size over-reads on RS256.
            ReadOnlyMemory<byte> remainder = authenticatorData[offset..];
            int coseKeyLength;
            try
            {
                CborReader reader = new(remainder);
                reader.SkipValue();
                coseKeyLength = remainder.Length - reader.BytesRemaining;
            }
            catch (CborContentException)
            {
                return Refused(PasskeyVerificationFailure.MalformedCoseKey);
            }
            catch (InvalidOperationException)
            {
                return Refused(PasskeyVerificationFailure.MalformedCoseKey);
            }

            attestedCredential = new AttestedCredentialData
            {
                CredentialId = credentialId,

                // Stored as the authenticator sent it. Re-encoding a COSE key normalises map order
                // and integer widths, and the stored bytes would then no longer be the bytes the
                // device produced.
                CoseKey = remainder[..coseKeyLength],
            };
            offset += coseKeyLength;
        }

        PasskeyVerificationFailure? extensionFailure = VerifyExtensions(authenticatorData[offset..], flags);
        if (extensionFailure is not null)
        {
            return Refused(extensionFailure.Value);
        }

        return PasskeyVerificationResult<AuthenticatorData>.Verified(new AuthenticatorData
        {
            RpIdHash = authenticatorData[..RpIdHashLength],
            Flags = flags,
            SignCount = signCount,
            AttestedCredential = attestedCredential,
        });
    }

    /// <summary>
    /// Whether this block was produced for <paramref name="relyingPartyId"/>.
    /// </summary>
    public bool MatchesRelyingParty(string relyingPartyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(relyingPartyId);

        // The authenticator sends SHA-256 of the rpId, never the id itself, so the comparison has to
        // happen on the hash. Fixed-time keeps the shape of the check uniform with the challenge one.
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(relyingPartyId), expected);

        return CryptographicOperations.FixedTimeEquals(RpIdHash.Span, expected);
    }

    private static PasskeyVerificationFailure? VerifyExtensions(
        ReadOnlyMemory<byte> remainder,
        AuthenticatorDataFlags flags)
    {
        if ((flags & AuthenticatorDataFlags.ExtensionData) == 0)
        {
            // No ED flag means nothing may follow. Trailing bytes here mean the block was parsed
            // against a different layout than the one that produced it.
            return remainder.IsEmpty ? null : PasskeyVerificationFailure.MalformedAuthenticatorData;
        }

        try
        {
            // The map is validated and then discarded. A registration ceremony's prf result arrives
            // in the client extension results, not in here, so there is nothing in this block this
            // product reads.
            CborReader reader = new(remainder);
            reader.ReadStartMap();
            while (reader.PeekState() is not CborReaderState.EndMap)
            {
                reader.SkipValue();
                reader.SkipValue();
            }

            reader.ReadEndMap();

            return reader.BytesRemaining is 0 ? null : PasskeyVerificationFailure.MalformedExtensionData;
        }
        catch (CborContentException)
        {
            return PasskeyVerificationFailure.MalformedExtensionData;
        }
        catch (InvalidOperationException)
        {
            return PasskeyVerificationFailure.MalformedExtensionData;
        }
    }

    private static PasskeyVerificationResult<AuthenticatorData> Refused(PasskeyVerificationFailure failure) =>
        PasskeyVerificationResult<AuthenticatorData>.Refused(failure);
}
