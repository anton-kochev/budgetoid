using System.Formats.Cbor;

namespace Application.Passkeys.Verification;

/// <summary>
/// The CBOR wrapper a registration response carries the authenticator data in.
/// </summary>
public sealed record AttestationObject
{
    /// <summary>The only attestation format this product accepts.</summary>
    public const string NoneFormat = "none";

    public required string Format { get; init; }

    /// <summary>Whether <c>attStmt</c> carried anything at all.</summary>
    public required bool HasAttestationStatement { get; init; }

    public required ReadOnlyMemory<byte> AuthenticatorData { get; init; }

    public static PasskeyVerificationResult<AttestationObject> Parse(ReadOnlyMemory<byte> attestationObject)
    {
        string? format = null;
        bool? hasAttestationStatement = null;
        ReadOnlyMemory<byte>? authenticatorData = null;

        try
        {
            CborReader reader = new(attestationObject);
            reader.ReadStartMap();
            while (reader.PeekState() is not CborReaderState.EndMap)
            {
                string label = reader.ReadTextString();
                switch (label)
                {
                    case "fmt":
                        format = reader.ReadTextString();
                        break;
                    case "attStmt":
                        hasAttestationStatement = ReadHasEntries(reader);
                        break;
                    case "authData":
                        authenticatorData = reader.ReadByteString();
                        break;
                    default:
                        reader.SkipValue();
                        break;
                }
            }

            reader.ReadEndMap();

            if (reader.BytesRemaining is not 0)
            {
                return Refused(PasskeyVerificationFailure.MalformedAttestationObject);
            }
        }
        catch (CborContentException)
        {
            return Refused(PasskeyVerificationFailure.MalformedAttestationObject);
        }
        catch (InvalidOperationException)
        {
            return Refused(PasskeyVerificationFailure.MalformedAttestationObject);
        }

        if (format is null || hasAttestationStatement is null || authenticatorData is null)
        {
            return Refused(PasskeyVerificationFailure.MalformedAttestationObject);
        }

        return PasskeyVerificationResult<AttestationObject>.Verified(new AttestationObject
        {
            Format = format,
            HasAttestationStatement = hasAttestationStatement.Value,
            AuthenticatorData = authenticatorData.Value,
        });
    }

    private static bool ReadHasEntries(CborReader reader)
    {
        // Read as a map even though it is required to be empty: a non-map here is a malformed
        // attestation object rather than an unsupported format, and the two refusals differ.
        reader.ReadStartMap();
        bool hasEntries = false;
        while (reader.PeekState() is not CborReaderState.EndMap)
        {
            reader.SkipValue();
            reader.SkipValue();
            hasEntries = true;
        }

        reader.ReadEndMap();

        return hasEntries;
    }

    private static PasskeyVerificationResult<AttestationObject> Refused(PasskeyVerificationFailure failure) =>
        PasskeyVerificationResult<AttestationObject>.Refused(failure);
}
