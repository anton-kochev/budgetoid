using System.Diagnostics.CodeAnalysis;
using Application.Security;
using Domain.Users;

namespace Application.Passkeys;

/// <summary>
/// The one decode-and-validate step for every wrapped account key share that crosses the wire.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Try</c> shape rather than a throw, for the reason <see cref="PasskeyEncoding.TryDecode"/> gives
/// for its own: both call sites — passkey registration and recovery-code generation — already turn a
/// malformed member into a refusal sentence of their own, and those sentences differ.
/// </para>
/// <para>
/// <b>What is left here after <see cref="CiphertextEnvelopeText"/> is the width, and only the width.</b>
/// The alphabet, the ceiling on decoded bytes and the version rule are the shared type's, because they
/// are the same rules for every sealed member this API accepts; a second spelling of any of them here
/// would be a way for the two to disagree. This type is the wrapped key's own bound sitting on top of
/// them.
/// </para>
/// <para>
/// The width and version rules are the database's own
/// (<c>CK_wrapped_account_keys_wrapped_content_key_length</c> and <c>..._version</c>), and
/// <see cref="WrappedAccountKeys.For"/> restates them again. None of the three is a duplicate to be
/// deleted: the lowest layer that can enforce a rule declaratively owns it, and the layers above it may
/// restate it for error quality — which is the whole of what this type is for. What it buys is a refusal
/// before anything is stored, rather than a 500 carrying a constraint name.
/// </para>
/// </remarks>
public static class WrappedKeyEnvelope
{
    /// <summary>
    /// Decodes <paramref name="value"/>, or returns false when it is absent, not base64url, not exactly
    /// <see cref="WrappedAccountKeys.EnvelopeLength"/> bytes, or does not carry version
    /// <see cref="WrappedAccountKeys.EnvelopeVersion"/>.
    /// </summary>
    /// <param name="value">The text a caller supplied.</param>
    /// <param name="envelope">The envelope bytes, when the value was accepted.</param>
    public static bool TryDecode(
        [NotNullWhen(true)] string? value,
        [NotNullWhen(true)] out byte[]? envelope)
    {
        envelope = null;

        // Reused rather than re-spelled: base64url is the one alphabet every binary member of this
        // exchange crosses JSON in, the ceiling is the one every sealed member is bounded by, and the
        // version rule is the format's rather than this member's. Two spellings of any of them is a
        // difference nobody meant.
        if (!CiphertextEnvelopeText.TryDecode(
            value, PasskeyPayloadLimits.WrappedKeyBytes, out byte[]? decoded))
        {
            return false;
        }

        // The exact width, which the checks above cannot make. They refuse an envelope wider than the
        // ceiling — the ceiling and the width are the same 61 bytes here — and they refuse one shorter
        // than the format's 29-byte floor, so an over-long envelope is already gone by the time it
        // reaches this line, and gone for the ceiling rather than for the width. What no shared rule
        // can see is the band between the two: a 29- to 60-byte envelope clears the floor, clears the
        // ceiling, carries the right version, and is still not a wrapped key. 29 is in that band and
        // not below it — the floor is a version, a nonce and a tag with no ciphertext between them, so
        // an envelope of exactly 29 bytes is well-formed. AES-GCM ciphertext is the length of its
        // plaintext and the plaintext is a 32-byte key, so this member has exactly one legal size and
        // the floor is not it.
        //
        // Written as an inequality against the width rather than a lower bound of its own, so that both
        // sides stay refused however the shared ceiling later moves. Neither side is padded or
        // truncated into shape: either repair stores a well-formed row holding an envelope whose tag
        // cannot verify, and the account looks registered until the day somebody needs the keys.
        if (decoded.Length != WrappedAccountKeys.EnvelopeLength)
        {
            return false;
        }

        envelope = decoded;

        return true;
    }
}
