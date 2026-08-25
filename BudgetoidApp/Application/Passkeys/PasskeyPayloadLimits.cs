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
/// That conversion rounds up, so <see cref="PasskeyEncoding.TryDecode"/> measures the decoded buffer
/// as well as the text: <b>every number below is the number admitted, not a byte or two under it.</b>
/// Read that as a property of the decoder rather than of any member here — cutting the second
/// comparison would widen six of these seven without touching a line in this file.
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
    /// accepts at its largest — which makes this one of the two bounds here with no slack in it, the
    /// other being <see cref="WrappedKeyBytes"/>: it is 1.0x the longest signature that can
    /// legitimately arrive, not several times it. There is nothing to reclaim by lowering it; a smaller
    /// number refuses a real key. And nothing to lose by trusting it, either — a signature of 513 bytes
    /// is refused, because the decoded length is compared and not only the text it arrived as.
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
    /// bytes, so 64 is the protocol's own ceiling rather than a chosen one — and it is the ceiling
    /// applied, 65 and 66 bytes included, because the decoded length is compared and not only the text
    /// it arrived as. A handle the authenticator stored years ago is compared, never trusted, but there
    /// is no size above this that could match.
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

    /// <summary>
    /// A wrapped account key envelope, on the one width the domain will store.
    /// </summary>
    /// <remarks>
    /// Deliberately the same constant <see cref="WrappedAccountKeys"/> refuses a row against, for the
    /// reason <see cref="CredentialIdBytes"/> gives: a member the wire accepted but the entity could
    /// never store is text decoded for nothing, and a separate number here would only be a way for this
    /// ceiling and the column's <c>CHECK length(...) = 61</c> to disagree.
    /// <para>
    /// A <b>width</b> and not a padded ceiling — the only other member here that is exact is
    /// <see cref="SignatureBytes"/>. AES-GCM ciphertext is the length of its plaintext and the plaintext
    /// is a 32-byte key, so an envelope over a wrapped key has exactly one legal size and there is no
    /// slack to leave. Applied as a ceiling it now refuses the wide side exactly, 62 bytes included;
    /// what it cannot say is that 61 is also a floor, which is why
    /// <see cref="WrappedKeyEnvelope"/> keeps an equality of its own.
    /// </para>
    /// </remarks>
    public const int WrappedKeyBytes = WrappedAccountKeys.EnvelopeLength;
}
