namespace Application.Passkeys.Verification;

/// <summary>
/// The reason a ceremony was refused.
/// </summary>
/// <remarks>
/// Every one of these is an expected outcome of a hostile or simply unsupported request, so they are
/// returned rather than thrown. The caller decides what reaches the client: an assertion failure is
/// meant to become one byte-identical 401 regardless of which member it carries, because a
/// distinguishing message is a credential-enumeration oracle.
/// </remarks>
public enum PasskeyVerificationFailure
{
    /// <summary>clientDataJSON was not JSON, or was missing a member the ceremony requires.</summary>
    MalformedClientData,

    /// <summary>clientData <c>type</c> was not the ceremony being verified.</summary>
    UnexpectedCeremonyType,

    /// <summary>The origin was not one of the allowed origins, compared by equality.</summary>
    UntrustedOrigin,

    /// <summary>clientData reported the ceremony ran in a cross-origin context.</summary>
    CrossOriginNotAllowed,

    /// <summary>The returned challenge was not the one this ceremony issued.</summary>
    ChallengeMismatch,

    /// <summary>The attestation object was not a CBOR map with the members WebAuthn defines.</summary>
    MalformedAttestationObject,

    /// <summary>An attestation format other than <c>none</c>, or a non-empty attestation statement.</summary>
    UnsupportedAttestationFormat,

    /// <summary>Authenticator data was too short, or its declared lengths ran past its end.</summary>
    MalformedAuthenticatorData,

    /// <summary>The rpIdHash was not SHA-256 of the expected relying party id.</summary>
    RelyingPartyMismatch,

    /// <summary>The user presence flag was clear.</summary>
    UserNotPresent,

    /// <summary>The user verification flag was clear.</summary>
    UserNotVerified,

    /// <summary>A registration carried no attested credential data, so there is no key to store.</summary>
    AttestedCredentialDataMissing,

    /// <summary>An assertion carried attested credential data, which that ceremony never produces.</summary>
    UnexpectedAttestedCredentialData,

    /// <summary>The extension block was absent when the flag claimed it, or was not a CBOR map.</summary>
    MalformedExtensionData,

    /// <summary>The credential id was outside the length a conforming authenticator can produce.</summary>
    CredentialIdOutOfRange,

    /// <summary>The COSE key did not decode, was missing a parameter, or would not construct.</summary>
    MalformedCoseKey,

    /// <summary>
    /// The COSE key was well-formed but its parameters are below the strength this product accepts.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="MalformedCoseKey"/> on purpose. A 512-bit RSA modulus with the
    /// standard exponent decodes perfectly; it is refused on policy, not on shape. In a log the two
    /// mean opposite things — a broken client versus an ancient device or an attacker — and acting on
    /// them the same way loses the only signal that separates them.
    /// </remarks>
    WeakCoseKey,

    /// <summary>The COSE <c>alg</c> was not one this product verifies.</summary>
    UnsupportedAlgorithm,

    /// <summary>The COSE <c>alg</c> was supported but not one the credential creation options offered.</summary>
    AlgorithmNotOffered,

    /// <summary>The COSE <c>kty</c> disagreed with the <c>alg</c> — refused, never coerced.</summary>
    CoseKeyTypeMismatch,

    /// <summary>The stored key's algorithm was not the algorithm the caller asked to verify with.</summary>
    AlgorithmMismatch,

    /// <summary>The signature did not verify over the authenticator data and the client data hash.</summary>
    SignatureInvalid,
}
