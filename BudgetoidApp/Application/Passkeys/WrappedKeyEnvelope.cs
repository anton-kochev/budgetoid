using System.Diagnostics.CodeAnalysis;
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
        // exchange crosses JSON in, and two decoders with different bounds is a difference nobody meant.
        // The ceiling is judged before the alphabet is validated and before anything is allocated, which
        // is the argument that member makes for taking one at all.
        if (!PasskeyEncoding.TryDecode(value, PasskeyPayloadLimits.WrappedKeyBytes, out byte[]? decoded))
        {
            return false;
        }

        // The ceiling above does NOT imply this. It bounds the encoded text, and 62 bytes encodes to 83
        // characters against an allowance of 84 — so an envelope one byte too wide arrives here having
        // passed every check so far. Width before version, and not merely for message quality: an empty
        // or short buffer has no leading byte for the version check to read.
        //
        // Both sides of the bound are refused, and neither is padded or truncated into shape: either
        // repair stores a well-formed row holding an envelope whose tag cannot verify, and the account
        // looks registered until the day somebody needs the keys.
        if (decoded.Length != WrappedAccountKeys.EnvelopeLength)
        {
            return false;
        }

        // IFR-007 at the edge. The successor version does not exist: version 2 is a client claiming a
        // contract this deployment has never implemented, and version 0 is a field nobody set — an
        // all-zero buffer of the legal width is what an uninitialised field and a stubbed client both
        // send, and it satisfies the width rule exactly.
        if (decoded[0] != WrappedAccountKeys.EnvelopeVersion)
        {
            return false;
        }

        envelope = decoded;

        return true;
    }
}
