using Domain.Users;

namespace Application.Passkeys;

/// <summary>
/// How large each base64url member of a ceremony response is allowed to be, in decoded bytes.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the sign-in leg is anonymous and decoding is not free: base64url validation
/// and decoding are two passes over the text plus an allocation the size of the result, and without a
/// ceiling the only bound is Kestrel's request body limit. Every number below is a ceiling on what a
/// conforming authenticator produces — refusing a real device is the one failure mode a limit like
/// this must not have, so most are several times the largest value the protocol and this product's own
/// accepted algorithms can yield. Not all of them are padded, and each says on itself which it is:
/// read the remark before cutting a number, because <see cref="SignatureBytes"/> is an exact bound
/// with no slack in it at all.
/// </para>
/// <para>
/// Bounds are stated in decoded bytes and converted where they are applied, because the size that
/// matters is the buffer, and the text a caller sends is about four characters per three bytes of it.
/// </para>
/// </remarks>
public static class PasskeyPayloadLimits
{
    /// <summary>
    /// <c>clientDataJSON</c>, for both ceremonies.
    /// </summary>
    /// <remarks>
    /// A real one is a JSON object of four members — type, the 32-byte challenge as 43 characters of
    /// base64url, the origin, and crossOrigin — which lands near 200 bytes. A kilobyte leaves room for
    /// a long origin and for the extra members the specification explicitly permits a client to append,
    /// and is still three orders of magnitude below what an unbounded body allows.
    /// </remarks>
    public const int ClientDataJsonBytes = 1024;

    /// <summary>
    /// <c>authenticatorData</c> on an assertion.
    /// </summary>
    /// <remarks>
    /// Fixed at 37 bytes — a 32-byte relying party id hash, one flags byte and a four-byte counter —
    /// unless the authenticator appends CBOR extension outputs, which attested credential data is not
    /// part of on this leg. 256 bytes is roughly seven times the required size, which covers extension
    /// outputs without admitting anything a device would send.
    /// </remarks>
    public const int AssertionAuthenticatorDataBytes = 256;

    /// <summary>
    /// <c>signature</c> on an assertion.
    /// </summary>
    /// <remarks>
    /// The product verifies two algorithms and nothing else. An ES256 signature is DER-encoded and
    /// tops out near 72 bytes; an RS256 signature is exactly the modulus length, so 256 bytes for the
    /// 2048-bit keys authenticators actually issue. 512 bytes is the 4096-bit RSA key this product
    /// accepts at its largest — which makes this the one limit here that is an exact bound and not a
    /// padded one: it is 1.0x the longest signature that can legitimately arrive, not several times
    /// it. There is nothing to reclaim by lowering it; a smaller number refuses a real key.
    /// </remarks>
    public const int SignatureBytes = 512;

    /// <summary>
    /// The credential handle, on the ceiling WebAuthn itself puts on one.
    /// </summary>
    /// <remarks>
    /// Deliberately the same constant the domain refuses a registration against: a handle this leg
    /// accepted but registration could never have stored is a handle no lookup can answer, so a
    /// separate number here would only be a way for the two to disagree.
    /// </remarks>
    public const int CredentialIdBytes = PasskeyPublicKey.MaxWebAuthnCredentialIdLength;

    /// <summary>
    /// <c>userHandle</c> on an assertion.
    /// </summary>
    /// <remarks>
    /// This product's handles are the 16 bytes of a user id, and WebAuthn caps a user handle at 64
    /// bytes, so 64 is the protocol's own ceiling rather than a chosen one. A handle the authenticator
    /// stored years ago is compared, never trusted, but there is no size above this that could match.
    /// </remarks>
    public const int UserHandleBytes = 64;

    /// <summary>
    /// <c>attestationObject</c> on a registration.
    /// </summary>
    /// <remarks>
    /// The largest member of either ceremony, because it carries the authenticator data with attested
    /// credential data inside it: 37 bytes of header, a 16-byte AAGUID, the two-byte credential id
    /// length, the credential id, and the COSE public key. Registration is offered with
    /// <c>attestation: "none"</c>, so the attestation statement is empty and no certificate chain
    /// rides along. At the maxima the domain will actually store — a 1023-byte credential id and a
    /// 1024-byte COSE key — that is a little over 2 KB, so 4 KB is the same doubling the other
    /// ceilings carry.
    /// <para>
    /// That doubling is measured against <c>attestation: "none"</c>, and a client is free to ignore
    /// the request: a <c>tpm</c> statement carrying a full x5c certificate chain — some Windows Hello
    /// paths produce one — can exceed 4 KB. Such a registration is refused either way, because the
    /// verifier accepts no attestation format but <c>none</c>, but above this limit it is refused for
    /// its size rather than for its format. Nothing is lost and nothing is admitted; the note exists so
    /// the resulting report is not investigated as a size defect when it is a format refusal.
    /// </para>
    /// </remarks>
    public const int AttestationObjectBytes = 4096;
}
