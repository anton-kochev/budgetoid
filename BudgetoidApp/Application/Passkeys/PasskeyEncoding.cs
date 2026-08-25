using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;

namespace Application.Passkeys;

/// <summary>
/// Base64url, the one encoding every binary member of a WebAuthn exchange crosses JSON in.
/// </summary>
/// <remarks>
/// <see cref="Base64Url"/> directly rather than a hand-rolled substitute-and-repad over
/// <see cref="Convert"/>: the WebAuthn alphabet is what this type implements, and hand-rolled
/// decoders are where strings the specification does not allow get accepted.
/// </remarks>
public static class PasskeyEncoding
{
    public static string Encode(ReadOnlySpan<byte> value) => Base64Url.EncodeToString(value);

    /// <summary>
    /// Decodes <paramref name="value"/>, or returns false when it is absent, empty, not base64url, or
    /// decodes to more than <paramref name="maxDecodedBytes"/> bytes.
    /// </summary>
    /// <param name="value">The text a caller supplied.</param>
    /// <param name="maxDecodedBytes">
    /// The largest buffer this member may decode to. Required rather than optional, so that no member
    /// of a ceremony can be decoded without one: the sign-in leg is anonymous, and an unbounded decode
    /// there is work an attacker gets to name the size of.
    /// </param>
    /// <param name="decoded">The bytes, when the value was accepted.</param>
    /// <remarks>
    /// <para>
    /// A <c>Try</c> shape rather than a throw because both call sites already have to turn a bad
    /// member into an answer of their own, and those answers differ: registration says what was wrong,
    /// and sign-in says nothing at all.
    /// </para>
    /// <para>
    /// <b>The ceiling is applied twice, and both applications are load-bearing.</b> The text is judged
    /// against <see cref="MaxEncodedLength"/> before <see cref="Base64Url.IsValid(ReadOnlySpan{char})"/>
    /// rather than after, and that ordering is the whole point of the parameter: validation is a full
    /// pass over the text and decoding is a second one plus an allocation the size of the result, so
    /// performing either first would mean an oversized member has already cost what the ceiling exists
    /// to refuse. That first gate is cheap but loose — the allowance is computed in the padded form and
    /// overshoots — so the buffer is measured again once it exists, and that second comparison is what
    /// makes <paramref name="maxDecodedBytes"/> mean what it says.
    /// </para>
    /// <para>
    /// <b>Both live here rather than in the callers.</b> This method is what creates the slack, so it is
    /// the lowest layer that can close it declaratively; left to callers it would be closed at whichever
    /// of them remembered, and a limit widened by a byte or two is invisible in every other.
    /// </para>
    /// </remarks>
    public static bool TryDecode(
        [NotNullWhen(true)] string? value,
        int maxDecodedBytes,
        [NotNullWhen(true)] out byte[]? decoded)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);

        decoded = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxEncodedLength(maxDecodedBytes))
        {
            return false;
        }

        if (!Base64Url.IsValid(value))
        {
            return false;
        }

        byte[] bytes = Base64Url.DecodeFromChars(value);

        // The gate above does NOT imply this one. MaxEncodedLength is the padded form, so text that
        // fits the allowance can still decode to one or two bytes past the ceiling the caller named:
        // for a ceiling of 29 the allowance is 40 characters, and a 30-byte value encodes to exactly
        // 40. Measured against PasskeyPayloadLimits, every member but the credential id was over-
        // admitted that way — 1024 admitted 1026, 512 admitted 513, 64 admitted 66 — so a member's
        // declared cap was not the cap it got.
        //
        // Written into the decoder rather than left to each caller because the decoder is what created
        // the slack, and because a caller that forgot its own post-decode comparison would widen its
        // own limit with nothing to show for it. Assigned to a local first so that the out parameter
        // stays null on every refusal: a caller reading the buffer without reading the result finds
        // nothing to work with.
        if (bytes.Length > maxDecodedBytes)
        {
            return false;
        }

        decoded = bytes;

        return true;
    }

    /// <summary>
    /// The longest base64url text that can encode <paramref name="decodedBytes"/> bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The padded form, four characters per three bytes rounded up, even though base64url omits the
    /// padding: <see cref="Base64Url"/> accepts a padded value, so the looser of the two lengths is
    /// the one that never refuses a member a client legitimately encoded.
    /// </para>
    /// <para>
    /// <b>It overshoots by up to two characters, which means this is not a bound on decoded bytes and
    /// must not be read as one.</b> Measured: text that fits inside the allowance this returns for a
    /// ceiling of 29 bytes decodes to 30. That is why <see cref="TryDecode"/> compares the decoded
    /// length as well, and why no caller owes a post-decode comparison of its own: the ceiling named
    /// there is the ceiling enforced, so every member of <see cref="PasskeyPayloadLimits"/> now admits
    /// exactly the number it declares. Before that comparison existed, all but
    /// <see cref="PasskeyPayloadLimits.CredentialIdBytes"/> admitted one or two bytes more.
    /// </para>
    /// <para>
    /// A caller may still need a rule this cannot make. <see cref="WrappedKeyEnvelope"/> keeps an exact
    /// width because its member has one, and what that catches is the <em>short</em> side — the band
    /// between the shared format's floor and the width — which no ceiling of any tightness can see.
    /// </para>
    /// </remarks>
    public static int MaxEncodedLength(int decodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decodedBytes);

        return (decodedBytes + 2) / 3 * 4;
    }

    /// <summary>
    /// The 16 raw bytes a user id crosses the wire as, in RFC 4122 order.
    /// </summary>
    /// <remarks>
    /// Big-endian explicitly, not the platform layout <see cref="Guid.ToByteArray()"/> produces: the
    /// handle is stored by the authenticator and handed back at sign-in, possibly years later, so the
    /// byte order has to be the one the value's own specification names rather than one an
    /// architecture happens to have.
    /// </remarks>
    public static byte[] ToUserHandle(Guid userId) => userId.ToByteArray(bigEndian: true);
}
