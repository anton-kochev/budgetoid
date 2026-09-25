using System.Diagnostics.CodeAnalysis;
using Application.Security;
using Domain.Users;

namespace Application.Passkeys;

/// <summary>
/// The one decode-and-validate step for every encapsulated copy of the account's two keys that crosses
/// the wire.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Try</c> shape rather than a throw, for the reason <see cref="PasskeyEncoding.TryDecode"/> gives
/// for its own: both call sites — passkey registration and recovery-code generation — already turn a
/// malformed member into a refusal sentence of their own, and those sentences differ.
/// </para>
/// <para>
/// <b>What is left here after <see cref="EncapsulatedValueEnvelopeText"/> is the width, and only the
/// width.</b> The alphabet, the ceiling on decoded bytes and the version rule are the shared type's,
/// because they are the same rules for every value of that suite this API accepts. This type is the
/// account-keys payload's own bound sitting on top of them.
/// </para>
/// <para>
/// <b>It is the mirror of <see cref="WrappedPrivateKeyEnvelope"/> and never a call into it.</b> The two
/// members arrive together in every request that files a factor, so the reader who is holding both
/// will see one shape written twice — and the values are 158 and 167 bytes of two different
/// cryptographic suites. Routed through the neighbour, an encapsulated value is judged against the AEAD
/// floor, which is 65 bytes below its own, and then against the wrong width: every one is refused, and
/// the day the widths coincide none would be.
/// </para>
/// <para>
/// <b>What no check on this side can reach is inside the plaintext.</b> The value is 64 bytes of
/// account key, <b>content key first</b>, and a client that encapsulated the two halves the other way
/// round produces a value of exactly this width carrying exactly this version, which decodes here,
/// stores, reads back and opens. <see cref="WrappedAccountKeys"/> spells out what that costs. Do not
/// read this type's acceptance as a statement about the payload; it is a statement about the framing.
/// </para>
/// <para>
/// The width and version rules are the database's own — the <c>wrapped_account_keys</c> column's length
/// and version check constraints — and <see cref="WrappedAccountKeys.For"/> restates them again. None
/// of the three is a duplicate to be deleted: the lowest layer that can enforce a rule declaratively
/// owns it, and the layers above it may restate it for error quality. What this one buys is a refusal
/// before anything is stored, rather than a 500 carrying a constraint name.
/// </para>
/// </remarks>
public static class EncapsulatedAccountKeysEnvelope
{
    /// <summary>
    /// Decodes <paramref name="value"/>, or returns false when it is absent, not base64url, not exactly
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength"/> bytes, or does not carry version
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysVersion"/>.
    /// </summary>
    /// <param name="value">The text a caller supplied.</param>
    /// <param name="encapsulated">The encapsulated value's bytes, when the text was accepted.</param>
    public static bool TryDecode(
        [NotNullWhen(true)] string? value,
        [NotNullWhen(true)] out byte[]? encapsulated)
    {
        encapsulated = null;

        // Reused rather than re-spelled, and taken from the suite this value belongs to:
        // EncapsulatedValueEnvelopeText applies EncapsulatedValueEnvelope.IsWellFormed, whose floor
        // counts the 65-byte ephemeral point. CiphertextEnvelopeText would clear the same version byte
        // over a floor of 29.
        if (!EncapsulatedValueEnvelopeText.TryDecode(
            value, PasskeyPayloadLimits.EncapsulatedAccountKeysBytes, out byte[]? decoded))
        {
            return false;
        }

        // The exact width, which the checks above cannot make. They refuse a value wider than the
        // ceiling — the ceiling and the width are the same 158 bytes here — and they refuse one shorter
        // than the format's 94-byte floor, so an over-long value is already gone by the time it reaches
        // this line, and gone for the ceiling rather than for the width. What no shared rule can see is
        // the band between the two: a 94- to 157-byte value clears the floor, clears the ceiling,
        // carries the right version, and is still not a pair of account keys. 94 is in that band and
        // not below it — the floor is a version, an ephemeral point, a nonce and a tag with no
        // ciphertext between them, so a value of exactly 94 bytes is well-formed. AES-GCM ciphertext is
        // the length of its plaintext and the plaintext is two 32-byte keys, so this member has exactly
        // one legal size and the floor is not it.
        //
        // Written as an inequality against the width rather than a lower bound of its own, so that both
        // sides stay refused however the shared ceiling later moves. Neither side is padded or
        // truncated into shape: either repair stores a well-formed row holding a value whose tag cannot
        // verify, and the factor looks usable until the day somebody needs the account's keys through
        // it.
        if (decoded.Length != WrappedAccountKeys.EncapsulatedAccountKeysLength)
        {
            return false;
        }

        encapsulated = decoded;

        return true;
    }
}
