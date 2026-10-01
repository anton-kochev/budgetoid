using System.Diagnostics;
using System.Formats.Cbor;
using System.Numerics;
using System.Security.Cryptography;
using Domain.Users;

namespace Application.Passkeys.Verification;

/// <summary>
/// A decoded COSE public key, ready to verify a signature with.
/// </summary>
/// <remarks>
/// Only the two algorithms the product supports decode here; everything else is refused rather than
/// carried forward as an unverifiable key. See
/// <c>docs/decisions/0013-verify-webauthn-ceremonies-without-a-fido-library.md</c>.
/// </remarks>
public sealed class CoseKeyMaterial
{
    // COSE key common parameters and the two key-type-specific label sets. -1, -2 and -3 mean
    // different things per key type: crv/x/y for EC2, n/e for RSA.
    private const int LabelKeyType = 1;
    private const int LabelAlgorithm = 3;
    private const int LabelNegativeOne = -1;
    private const int LabelNegativeTwo = -2;
    private const int LabelNegativeThree = -3;

    private const int KeyTypeEc2 = 2;
    private const int KeyTypeRsa = 3;
    private const int CurveP256 = 1;

    /// <summary>P-256 coordinates are fixed width; a short one would be silently left-padded.</summary>
    private const int P256CoordinateLength = 32;

    /// <summary>
    /// The only RSA public exponent this product accepts, as a value rather than an encoding.
    /// </summary>
    private const uint PermittedRsaExponent = 65537;

    private const int MinimumRsaModulusBits = 2048;
    private const int MaximumRsaModulusBits = 4096;

    private readonly ECParameters _ecParameters;
    private readonly RSAParameters _rsaParameters;

    private CoseKeyMaterial(CoseAlgorithm algorithm, ECParameters ecParameters, RSAParameters rsaParameters)
    {
        Algorithm = algorithm;
        _ecParameters = ecParameters;
        _rsaParameters = rsaParameters;
    }

    public CoseAlgorithm Algorithm { get; }

    /// <summary>
    /// Decodes a COSE key, refusing anything this product cannot verify with.
    /// </summary>
    public static PasskeyVerificationResult<CoseKeyMaterial> Decode(ReadOnlyMemory<byte> coseKey)
    {
        int? keyType = null;
        int? algorithm = null;
        int? curve = null;
        byte[]? negativeOne = null;
        byte[]? negativeTwo = null;
        byte[]? negativeThree = null;

        try
        {
            CborReader reader = new(coseKey);
            reader.ReadStartMap();
            while (reader.PeekState() is not CborReaderState.EndMap)
            {
                int label = reader.ReadInt32();
                switch (label)
                {
                    case LabelKeyType:
                        keyType = reader.ReadInt32();
                        break;
                    case LabelAlgorithm:
                        algorithm = reader.ReadInt32();
                        break;
                    case LabelNegativeOne:
                        // -1 is the curve for EC2 and the modulus for RSA. The encoding tells the
                        // two apart without depending on kty having been read first, which the map
                        // order does not guarantee.
                        if (reader.PeekState() is CborReaderState.ByteString)
                        {
                            negativeOne = reader.ReadByteString();
                        }
                        else
                        {
                            curve = reader.ReadInt32();
                        }

                        break;
                    case LabelNegativeTwo:
                        negativeTwo = reader.ReadByteString();
                        break;
                    case LabelNegativeThree:
                        negativeThree = reader.ReadByteString();
                        break;
                    default:
                        // Unknown labels are skipped: a key may legitimately carry key_ops or kid.
                        reader.SkipValue();
                        break;
                }
            }

            reader.ReadEndMap();

            if (reader.BytesRemaining is not 0)
            {
                return Refused(PasskeyVerificationFailure.MalformedCoseKey);
            }
        }
        catch (CborContentException)
        {
            return Refused(PasskeyVerificationFailure.MalformedCoseKey);
        }
        catch (InvalidOperationException)
        {
            return Refused(PasskeyVerificationFailure.MalformedCoseKey);
        }
        catch (OverflowException)
        {
            return Refused(PasskeyVerificationFailure.MalformedCoseKey);
        }

        return algorithm switch
        {
            (int)CoseAlgorithm.Es256 => DecodeEs256(keyType, curve, x: negativeTwo, y: negativeThree),
            (int)CoseAlgorithm.Rs256 => DecodeRs256(keyType, modulus: negativeOne, exponent: negativeTwo),
            _ => Refused(PasskeyVerificationFailure.UnsupportedAlgorithm),
        };
    }

    /// <summary>
    /// Verifies <paramref name="signature"/> over <paramref name="signedData"/> with this key.
    /// </summary>
    public bool VerifySignature(ReadOnlySpan<byte> signedData, ReadOnlySpan<byte> signature)
    {
        try
        {
            switch (Algorithm)
            {
                case CoseAlgorithm.Es256:
                    {
                        using ECDsa ecdsa = ECDsa.Create(_ecParameters);

                        // The format argument is not optional here. WebAuthn ES256 signatures are ASN.1
                        // DER sequences, and the overload without it expects IEEE P1363, so leaving it
                        // off refuses every signature a real authenticator produces.
                        return ecdsa.VerifyData(
                            signedData,
                            signature,
                            HashAlgorithmName.SHA256,
                            DSASignatureFormat.Rfc3279DerSequence);
                    }

                case CoseAlgorithm.Rs256:
                    {
                        using RSA rsa = RSA.Create(_rsaParameters);

                        return rsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    }

                default:
                    // Decode is the only way to obtain one of these and it admits no other value.
                    throw new UnreachableException($"Unhandled COSE algorithm {Algorithm}.");
            }
        }
        catch (CryptographicException)
        {
            // A signature that is not well-formed DER is a refusal, not a fault: an attacker chooses
            // those bytes.
            return false;
        }
    }

    private static PasskeyVerificationResult<CoseKeyMaterial> DecodeEs256(
        int? keyType,
        int? curve,
        byte[]? x,
        byte[]? y)
    {
        // A kty that disagrees with alg is refused outright. Coercing it — trusting alg and reading
        // the labels as EC2 anyway — would decode attacker-chosen bytes under a shape they did not
        // claim.
        if (keyType is not KeyTypeEc2)
        {
            return Refused(PasskeyVerificationFailure.CoseKeyTypeMismatch);
        }

        if (curve is not CurveP256
            || x is not { Length: P256CoordinateLength }
            || y is not { Length: P256CoordinateLength })
        {
            return Refused(PasskeyVerificationFailure.MalformedCoseKey);
        }

        ECParameters parameters = new()
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        };

        // Constructed once at registration so a key that is well-formed CBOR but not a point on the
        // curve is refused now, rather than at the person's first sign-in, when it is far more
        // expensive to diagnose and nothing can be done about it.
        try
        {
            using ECDsa probe = ECDsa.Create(parameters);
        }
        catch (CryptographicException)
        {
            return Refused(PasskeyVerificationFailure.MalformedCoseKey);
        }
        catch (ArgumentException)
        {
            return Refused(PasskeyVerificationFailure.MalformedCoseKey);
        }

        return PasskeyVerificationResult<CoseKeyMaterial>.Verified(
            new CoseKeyMaterial(CoseAlgorithm.Es256, parameters, default));
    }

    private static PasskeyVerificationResult<CoseKeyMaterial> DecodeRs256(
        int? keyType,
        byte[]? modulus,
        byte[]? exponent)
    {
        if (keyType is not KeyTypeRsa)
        {
            return Refused(PasskeyVerificationFailure.CoseKeyTypeMismatch);
        }

        if (modulus is not { Length: > 0 } || exponent is not { Length: > 0 })
        {
            return Refused(PasskeyVerificationFailure.MalformedCoseKey);
        }

        // What follows is strength policy, checked here rather than delegated to RSA.Create. That
        // constructor routes through SecurityTransforms on macOS and OpenSSL in the deployed
        // container, and the two disagree about degenerate exponents and short moduli — so leaving
        // the decision to it makes the acceptance criterion for a key trusted for the life of an
        // account depend on which machine is running. There is no revocation path once a key is
        // registered, so the criterion is pinned here and is the same everywhere.
        //
        // Deliberately not a database check constraint, and this is the ADR 0002 exception rather
        // than an oversight: the fact is not in a column. The modulus and exponent live inside the
        // CBOR blob in public_key_cose, PostgreSQL cannot read CBOR, and pushing procedural logic
        // down to reach the "lowest capable layer" is the boundary that ADR forbids. Splitting the
        // key into rsa_modulus/rsa_exponent columns would add data nothing reads and a second
        // representation free to drift from the stored one. The existing blob-length bound cannot
        // stand in either: an ES256 key is roughly 77 bytes and a 512-bit RS256 key roughly 90, so
        // no length bound separates a weak RSA key from every valid EC key.

        // Exponent: an allow-list of exactly one value, compared numerically so that a minimal
        // 01 00 01 and a zero-padded 00 01 00 01 are the same key. Leading zeros are stripped before
        // the width check so a padded encoding is not mistaken for an oversized one, and the check
        // is what keeps the conversion below from overflowing.
        //
        // Not {3, 65537}: e = 3 is where the low-exponent PKCS#1 v1.5 forgery family lives, and
        // whether a forgery lands turns on how carefully the verifier parses padding — exactly the
        // platform-dependent judgement this block exists to stop delegating. Nothing in the field
        // needs it; platform authenticators and TPM-backed credentials emit F4. Not a predicate
        // ("odd, >= 3, <= ceiling") either: that admits every exponent no authenticator has ever
        // produced, and exists only to avoid writing the decision down. The asymmetry decides it —
        // widening later is one constant, narrowing later locks out people already registered who
        // then cannot sign in at all.
        ReadOnlySpan<byte> significantExponent = TrimLeadingZeros(exponent);
        if (significantExponent.Length > sizeof(uint))
        {
            return Refused(PasskeyVerificationFailure.WeakCoseKey);
        }

        uint exponentValue = 0;
        foreach (byte octet in significantExponent)
        {
            exponentValue = (exponentValue << 8) | octet;
        }

        if (exponentValue is not PermittedRsaExponent)
        {
            return Refused(PasskeyVerificationFailure.WeakCoseKey);
        }

        // Modulus: effective bit length, counted after skipping leading zero octets.
        //
        // Bits, not bytes — the tempting wrong version is `modulus.Length >= 256` and it is one
        // character away from this one. RFC 8230 says COSE omits leading zero octets, but an
        // authenticator whose encoder copies a DER INTEGER emits a leading 00, so a length rule has
        // to accept 257 bytes to accept real keys. Once it does, a 512-bit modulus left-padded with
        // 224 zero bytes passes a byte-length check while still being a 512-bit key. Counting bits
        // after the skip accepts both real encodings and admits neither weak key.
        //
        // The ceiling is a decision, not a limit inherited from elsewhere: above 4096 bits no
        // authenticator exists, RSA.Create behaviour genuinely diverges by backend, and verification
        // cost grows superlinearly on a path an anonymous caller reaches. The stored blob's length
        // bound already refuses past roughly 8192 bits, but by accident.
        int modulusBits = SignificantBitLength(modulus);
        if (modulusBits is < MinimumRsaModulusBits or > MaximumRsaModulusBits)
        {
            return Refused(PasskeyVerificationFailure.WeakCoseKey);
        }

        RSAParameters parameters = new() { Modulus = modulus, Exponent = exponent };

        try
        {
            using RSA probe = RSA.Create(parameters);
        }
        catch (CryptographicException)
        {
            return Refused(PasskeyVerificationFailure.MalformedCoseKey);
        }
        catch (ArgumentException)
        {
            return Refused(PasskeyVerificationFailure.MalformedCoseKey);
        }

        return PasskeyVerificationResult<CoseKeyMaterial>.Verified(
            new CoseKeyMaterial(CoseAlgorithm.Rs256, default, parameters));
    }

    /// <summary>
    /// The number of bits a big-endian unsigned integer actually carries, ignoring any leading zero
    /// octets an encoder added. Zero for a value that is entirely zero.
    /// </summary>
    private static int SignificantBitLength(ReadOnlySpan<byte> bigEndian)
    {
        ReadOnlySpan<byte> significant = TrimLeadingZeros(bigEndian);
        if (significant.IsEmpty)
        {
            return 0;
        }

        // 32 - LeadingZeroCount widens the byte to uint, so the count is taken over the whole word;
        // subtracting from 32 gives the 1..8 bits set in the top octet.
        return ((significant.Length - 1) * 8) + (32 - BitOperations.LeadingZeroCount(significant[0]));
    }

    private static ReadOnlySpan<byte> TrimLeadingZeros(ReadOnlySpan<byte> bigEndian)
    {
        int index = 0;
        while (index < bigEndian.Length && bigEndian[index] is 0)
        {
            index++;
        }

        return bigEndian[index..];
    }

    private static PasskeyVerificationResult<CoseKeyMaterial> Refused(PasskeyVerificationFailure failure) =>
        PasskeyVerificationResult<CoseKeyMaterial>.Refused(failure);
}
