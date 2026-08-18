using System.Security.Cryptography;
using Application.Passkeys.Verification;
using Domain.Users;

namespace TestSupport;

/// <summary>
/// A real authenticator in software: it holds a real key pair, emits real CBOR and real JSON, and
/// produces real signatures over the bytes WebAuthn says are signed.
/// </summary>
/// <remarks>
/// Nothing here is a stub. A stub would let a test assert that the verifier accepts whatever the
/// stub happens to emit, which is a tautology; a genuine signer means the accepting tests only pass
/// when the verifier reads the format the way the specification describes it. Every deviation the
/// negative tests need is an argument on one of the two methods, never a separate builder, so a test
/// moves exactly one value away from a response that would otherwise be accepted.
/// </remarks>
public sealed class SyntheticAuthenticator
{
    private const int DefaultCredentialIdLength = 32;
    private const int DefaultRsaKeySizeInBits = 2048;

    private readonly string _relyingPartyId;
    private readonly ECParameters _ecParameters;
    private readonly RSAParameters _rsaParameters;

    private SyntheticAuthenticator(
        string relyingPartyId,
        CoseAlgorithm algorithm,
        byte[] credentialId,
        byte[] coseKey,
        ECParameters ecParameters,
        RSAParameters rsaParameters)
    {
        _relyingPartyId = relyingPartyId;
        _ecParameters = ecParameters;
        _rsaParameters = rsaParameters;
        Algorithm = algorithm;
        CredentialId = credentialId;
        CoseKey = coseKey;
    }

    /// <summary>The algorithm this device signs with.</summary>
    public CoseAlgorithm Algorithm { get; }

    /// <summary>The handle this device answers to.</summary>
    public byte[] CredentialId { get; }

    /// <summary>The public key as this device would emit it, byte for byte.</summary>
    public byte[] CoseKey { get; }

    /// <summary>
    /// The user handle this device kept from the last <c>webauthn.create</c> it ran, or null when that
    /// ceremony was handed none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole of what a device knows about the account it was enrolled to.</b> A real
    /// authenticator stores <c>publicKey.user.id</c> verbatim at registration and hands exactly those
    /// bytes back as the assertion's <c>userHandle</c> — it never derives, recomputes or re-reads the
    /// value, so a relying party that put one thing in the options and wrote another into its own
    /// account row has locked the device out permanently and cannot find out from the device.
    /// </para>
    /// <para>
    /// Modelling that storage here is what lets a test present the handle the <em>server</em> issued
    /// instead of one the test derived for itself. A test that computes the handle from the challenge
    /// and passes it to <see cref="Authenticate" /> agrees with the finish leg's derivation whatever
    /// the options leg said, and so cannot see the two legs disagree.
    /// </para>
    /// </remarks>
    public byte[]? StoredUserHandle { get; private set; }

    /// <summary>
    /// Creates a P-256 device.
    /// </summary>
    public static SyntheticAuthenticator CreateEs256(string relyingPartyId, byte[]? credentialId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(relyingPartyId);

        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        byte[] coseKey = WebAuthnWireFormat.EncodeEc2CoseKey(
            WebAuthnWireFormat.KeyTypeEc2,
            (int)CoseAlgorithm.Es256,
            WebAuthnWireFormat.CurveP256,
            parameters.Q.X!,
            parameters.Q.Y!);

        return new SyntheticAuthenticator(
            relyingPartyId,
            CoseAlgorithm.Es256,
            credentialId ?? RandomCredentialId(),
            coseKey,
            parameters,
            default);
    }

    /// <summary>
    /// Creates an RSA device. Present because a platform authenticator that only offers RS256 is a
    /// real thing, and a verifier tested on one algorithm has only been tested on one algorithm.
    /// </summary>
    /// <param name="relyingPartyId">The relying party this device answers to.</param>
    /// <param name="credentialId">The handle this device answers to, or null for a random one.</param>
    /// <param name="keySizeInBits">
    /// The size of the key this device actually holds. A parameter because the modulus width is a
    /// property of the device rather than of the encoding, so a weak device is a real key pair of a
    /// weak size and not a hand-built blob.
    /// </param>
    public static SyntheticAuthenticator CreateRs256(
        string relyingPartyId,
        byte[]? credentialId = null,
        int keySizeInBits = DefaultRsaKeySizeInBits)
    {
        ArgumentException.ThrowIfNullOrEmpty(relyingPartyId);

        using RSA rsa = RSA.Create(keySizeInBits);
        RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: true);
        byte[] coseKey = WebAuthnWireFormat.EncodeRsaCoseKey(
            WebAuthnWireFormat.KeyTypeRsa,
            (int)CoseAlgorithm.Rs256,
            parameters.Modulus!,
            parameters.Exponent!);

        return new SyntheticAuthenticator(
            relyingPartyId,
            CoseAlgorithm.Rs256,
            credentialId ?? RandomCredentialId(),
            coseKey,
            default,
            parameters);
    }

    /// <summary>
    /// Runs a <c>webauthn.create</c> ceremony.
    /// </summary>
    /// <param name="challenge">The challenge the server issued.</param>
    /// <param name="origin">The origin the client reports in its client data.</param>
    /// <param name="signCount">The counter this device reports at registration.</param>
    /// <param name="userPresent">Whether the UP flag is set.</param>
    /// <param name="userVerified">Whether the UV flag is set.</param>
    /// <param name="extensionDataFlag">Whether ED is set, independently of whether a block follows.</param>
    /// <param name="attestationFormat">The <c>fmt</c> the attestation object names.</param>
    /// <param name="prfEnabled">The PRF result the client reports, or null for none.</param>
    /// <param name="includeAttestedCredentialData">
    /// Whether AT is set and the credential is included. A registration without it is the one shape
    /// this ceremony must never produce, and it takes a parameter to build one.
    /// </param>
    /// <param name="coseKeyOverride">
    /// A key encoded by the caller instead of by this device, for the cases where the key itself is
    /// the deviation under test.
    /// </param>
    /// <param name="appendExtensionBytes">
    /// The block appended after the credential, written whenever it is supplied. Kept apart from
    /// <paramref name="extensionDataFlag"/> so the flag and the block are two independent flips.
    /// </param>
    /// <param name="crossOrigin">Whether the client data reports a cross-origin ceremony.</param>
    /// <param name="declaredCredentialIdLengthOverride">
    /// A <c>credentialIdLength</c> other than the one the credential actually has.
    /// </param>
    /// <param name="truncateToLength">Cuts the finished authenticator data block to this many bytes.</param>
    /// <param name="encodeAuthDataAsTextString">
    /// Writes the attestation object's <c>authData</c> as a CBOR text string instead of bytes.
    /// </param>
    /// <param name="omitAuthData">Leaves <c>authData</c> out of the attestation object.</param>
    /// <param name="userHandle">
    /// The <c>publicKey.user.id</c> the relying party issued with these options, kept on
    /// <see cref="StoredUserHandle" /> the way a real authenticator keeps it. Declared last, and
    /// optional, so every existing call site keeps compiling and keeps behaving exactly as it does
    /// today: nothing about the bytes this method returns depends on it, because a registration
    /// response carries no user handle anywhere — the handle is stored, not signed.
    /// </param>
    public AttestationResult Register(
        byte[] challenge,
        string origin,
        uint signCount = 0,
        bool userPresent = true,
        bool userVerified = true,
        bool extensionDataFlag = false,
        string attestationFormat = AttestationObject.NoneFormat,
        bool? prfEnabled = true,
        bool includeAttestedCredentialData = true,
        byte[]? coseKeyOverride = null,
        byte[]? appendExtensionBytes = null,
        bool crossOrigin = false,
        ushort? declaredCredentialIdLengthOverride = null,
        int? truncateToLength = null,
        bool encodeAuthDataAsTextString = false,
        bool omitAuthData = false,
        byte[]? userHandle = null)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentException.ThrowIfNullOrEmpty(origin);

        // Assigned unconditionally, including to null. A device holds one handle per credential and a
        // fresh enrolment replaces it; a member that only ever widened would leave a second ceremony's
        // device answering with the first ceremony's account.
        StoredUserHandle = userHandle;

        byte[] clientDataJson = WebAuthnWireFormat.EncodeClientDataJson(
            CollectedClientData.RegistrationType,
            challenge,
            origin,
            crossOrigin);

        AuthenticatorDataFlags flags = BuildFlags(userPresent, userVerified);
        if (includeAttestedCredentialData)
        {
            flags |= AuthenticatorDataFlags.AttestedCredentialData;
        }

        if (extensionDataFlag)
        {
            flags |= AuthenticatorDataFlags.ExtensionData;
        }

        byte[] authenticatorData = WebAuthnWireFormat.EncodeAuthenticatorData(
            WebAuthnWireFormat.RelyingPartyIdHash(_relyingPartyId),
            flags,
            signCount,
            CredentialId,
            coseKeyOverride ?? CoseKey,
            appendExtensionBytes,
            declaredCredentialIdLengthOverride,
            truncateToLength);

        return new AttestationResult
        {
            ClientDataJson = clientDataJson,
            AttestationObject = WebAuthnWireFormat.EncodeAttestationObject(
                attestationFormat,
                authenticatorData,
                encodeAuthDataAsTextString,
                omitAuthData),
            CredentialId = CredentialId,
            PrfEnabled = prfEnabled,
        };
    }

    /// <summary>
    /// Runs a <c>webauthn.get</c> ceremony, signing what the specification says is signed.
    /// </summary>
    /// <param name="challenge">The challenge the server issued.</param>
    /// <param name="origin">The origin the client reports in its client data.</param>
    /// <param name="userHandle">The user handle a discoverable credential returns.</param>
    /// <param name="signCount">The counter this device reports.</param>
    /// <param name="userPresent">Whether the UP flag is set.</param>
    /// <param name="userVerified">Whether the UV flag is set.</param>
    /// <param name="rpIdOverride">A relying party id other than this device's, hashed into the block.</param>
    /// <param name="encodeSignatureAsIeeeP1363">
    /// Emits the same ECDSA signature over the same bytes in the fixed-width concatenated encoding
    /// instead of the ASN.1 DER one WebAuthn specifies. A verifier that omits the DER format argument
    /// accepts this and refuses every real authenticator, so this is the only way to tell the two
    /// apart from the outside.
    /// </param>
    /// <param name="clientDataTypeOverride">
    /// A ceremony type other than <c>webauthn.get</c>, signed for real so the refusal comes from the
    /// type check rather than from a signature that never covered the substituted value.
    /// </param>
    /// <param name="crossOrigin">Whether the client data reports a cross-origin ceremony.</param>
    /// <param name="includeAttestedCredentialData">
    /// Sets AT and appends the credential and key. An assertion never carries these, so building one
    /// that does is the only way to reach the check that refuses it.
    /// </param>
    /// <param name="declaredCredentialIdLengthOverride">
    /// A <c>credentialIdLength</c> other than the one the credential actually has.
    /// </param>
    /// <param name="truncateToLength">Cuts the finished authenticator data block to this many bytes.</param>
    public AssertionResult Authenticate(
        byte[] challenge,
        string origin,
        byte[]? userHandle = null,
        uint signCount = 1,
        bool userPresent = true,
        bool userVerified = true,
        string? rpIdOverride = null,
        bool encodeSignatureAsIeeeP1363 = false,
        string? clientDataTypeOverride = null,
        bool crossOrigin = false,
        bool includeAttestedCredentialData = false,
        ushort? declaredCredentialIdLengthOverride = null,
        int? truncateToLength = null)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentException.ThrowIfNullOrEmpty(origin);

        byte[] clientDataJson = WebAuthnWireFormat.EncodeClientDataJson(
            clientDataTypeOverride ?? CollectedClientData.AuthenticationType,
            challenge,
            origin,
            crossOrigin);

        // An assertion carries no attested credential data and no extension block: AT and ED stay
        // clear, which is what makes the 37-byte fixed header the whole of it.
        AuthenticatorDataFlags flags = BuildFlags(userPresent, userVerified);
        if (includeAttestedCredentialData)
        {
            flags |= AuthenticatorDataFlags.AttestedCredentialData;
        }

        byte[] authenticatorData = WebAuthnWireFormat.EncodeAuthenticatorData(
            WebAuthnWireFormat.RelyingPartyIdHash(rpIdOverride ?? _relyingPartyId),
            flags,
            signCount,
            CredentialId,
            CoseKey,
            extensions: default,
            declaredCredentialIdLengthOverride,
            truncateToLength);

        byte[] signedData = BuildSignedData(authenticatorData, clientDataJson);

        return new AssertionResult
        {
            ClientDataJson = clientDataJson,
            AuthenticatorData = authenticatorData,
            Signature = Sign(signedData, encodeSignatureAsIeeeP1363),
            CredentialId = CredentialId,
            UserHandle = userHandle,
        };
    }

    /// <summary>
    /// Signs bytes with this device's private key, exactly as the two ceremonies do.
    /// </summary>
    /// <remarks>
    /// Exposed so a test can sign one authenticator's data with another authenticator's key without
    /// reaching for a second signing routine.
    /// </remarks>
    public byte[] Sign(byte[] data, bool encodeSignatureAsIeeeP1363 = false)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (Algorithm is CoseAlgorithm.Rs256)
        {
            using RSA rsa = RSA.Create(_rsaParameters);

            return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        using ECDsa ecdsa = ECDsa.Create(_ecParameters);

        // Rfc3279DerSequence is what a real authenticator emits. The P1363 branch produces a genuine
        // signature of the same data in the other encoding, so it is a valid signature that this
        // ceremony still has no business accepting.
        DSASignatureFormat format = encodeSignatureAsIeeeP1363
            ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            : DSASignatureFormat.Rfc3279DerSequence;

        return ecdsa.SignData(data, HashAlgorithmName.SHA256, format);
    }

    /// <summary>
    /// The bytes WebAuthn signs: authenticator data followed by the hash of the client data.
    /// </summary>
    public static byte[] BuildSignedData(ReadOnlySpan<byte> authenticatorData, ReadOnlySpan<byte> clientDataJson)
    {
        byte[] signedData = new byte[authenticatorData.Length + SHA256.HashSizeInBytes];
        authenticatorData.CopyTo(signedData);
        SHA256.HashData(clientDataJson, signedData.AsSpan(authenticatorData.Length));

        return signedData;
    }

    private static AuthenticatorDataFlags BuildFlags(bool userPresent, bool userVerified)
    {
        AuthenticatorDataFlags flags = AuthenticatorDataFlags.None;
        if (userPresent)
        {
            flags |= AuthenticatorDataFlags.UserPresent;
        }

        if (userVerified)
        {
            flags |= AuthenticatorDataFlags.UserVerified;
        }

        return flags;
    }

    private static byte[] RandomCredentialId() => RandomNumberGenerator.GetBytes(DefaultCredentialIdLength);
}
