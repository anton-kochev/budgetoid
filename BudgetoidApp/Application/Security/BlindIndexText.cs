using System.Diagnostics.CodeAnalysis;
using Application.Passkeys;
using Domain.Security;

namespace Application.Security;

/// <summary>
/// The one decode-and-validate step for a blind index a client hands the API as text: base64url within
/// a ceiling, then the exact width <see cref="IndexedName.BlindIndexLength"/> names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built on <see cref="PasskeyEncoding.TryDecode"/> and deliberately not on
/// <see cref="CiphertextEnvelopeText"/>, which is the near miss.</b> A blind index sits beside a
/// ciphertext, arrives in the same request and is written in the same row, so the neighbouring type
/// looks like the natural base — and it would refuse every legal value this member can receive. That
/// type applies the framing rules of an envelope: a 29-byte floor and a leading version byte. A blind
/// index is a keyed digest with no version, no nonce and no tag, and its first byte is whatever
/// HMAC-SHA-256 produced; requiring it to be <c>0x01</c> would admit roughly one value in 256 and
/// refuse the rest as malformed. What the two genuinely share is the alphabet, and that is the layer
/// this type reuses.
/// </para>
/// <para>
/// <b>What is left after the shared decoder is the width, and only the width</b> — the shape
/// <see cref="WrappedKeyEnvelope"/> holds for its own member. The alphabet and the bound on decoded
/// bytes are the decoder's, because they are the same rules for every binary member this API accepts
/// as text; a second spelling of either here would be a way for the two to disagree.
/// </para>
/// <para>
/// <b>The ceiling and the width are the same number, and both are applied.</b> Passing
/// <see cref="IndexedName.BlindIndexLength"/> as the ceiling refuses everything wider, exactly —
/// the shared decoder measures the decoded buffer as well as the text it arrived as, so 33 bytes does
/// not slip through the padded allowance. What no ceiling can see is the short side, and this member is
/// where that is closed: a 31-byte value passes every check made above this type and is still not a
/// blind index. It is not padded or truncated into shape, for the reason
/// <see cref="WrappedKeyEnvelope"/> gives — either repair stores a well-formed row holding a value that
/// matches nothing, and the row looks correct until somebody searches for the name it was supposed to
/// find.
/// </para>
/// <para>
/// <b>Why the width is worth refusing at all, given the server cannot check anything else.</b> This
/// side holds no index key, so it can never say a value is the index <em>of</em> the name beside it —
/// a correct-width value computed over the wrong text, under the wrong key, or out of a random number
/// generator is accepted here and is wrong for the life of the account, silently, because a blind index
/// cannot be recomputed by anything but the browser that made it. The width is the only shape check
/// available, which is precisely why it is not left out as the small one.
/// </para>
/// <para>
/// A <c>Try</c> shape rather than a throw, and no message, for the reason every type beside it gives:
/// each call site already turns a malformed member into a refusal sentence of its own, and those
/// sentences differ. The <c>out</c> parameter is left <see langword="null"/> on every refusal, so a
/// caller that reads the buffer without reading the result finds nothing to work with.
/// </para>
/// <para>
/// <b>In <c>Application</c> and not in <c>Domain</c>, because base64url is a wire concern.</b>
/// <see cref="IndexedName"/> owns what a blind index is and knows nothing about how it travelled; this
/// type is the edge that turns text into those bytes. It declares no number of its own — the width
/// comes from the domain — which is what keeps the edge and the column from drifting apart.
/// </para>
/// </remarks>
public static class BlindIndexText
{
    /// <summary>
    /// Decodes <paramref name="value"/>, or returns false when it is absent, not base64url, or not
    /// exactly <see cref="IndexedName.BlindIndexLength"/> bytes.
    /// </summary>
    /// <param name="value">The text a caller supplied — 43 characters of unpadded base64url.</param>
    /// <param name="blindIndex">The index bytes, when the value was accepted.</param>
    /// <returns>
    /// <see langword="true"/> when the text decoded to a value of the one legal width; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// It takes no ceiling parameter, unlike <see cref="CiphertextEnvelopeText.TryDecode"/>: that
    /// member serves fields whose sizes differ, and this one has a single legal width to name for
    /// itself. A ceiling here would be a caller's chance to widen a bound that has no room in it.
    /// </remarks>
    public static bool TryDecode(
        [NotNullWhen(true)] string? value,
        [NotNullWhen(true)] out byte[]? blindIndex)
    {
        blindIndex = null;

        // The shared decoder and deliberately not CiphertextEnvelopeText, which is the near miss: that
        // type applies the framing rules of an envelope, and a blind index has no version byte to
        // satisfy them with — requiring 0x01 would admit roughly one value in 256. What the two
        // genuinely share is the alphabet and the bound on decoded bytes, which is this layer.
        //
        // The ceiling is the width, so everything wider is already gone by the line below: the decoder
        // measures the decoded buffer as well as the text it arrived as, and 33 bytes encodes to the
        // 44 characters the padded allowance for 32 admits, so it is refused after decoding rather
        // than before.
        if (!PasskeyEncoding.TryDecode(value, IndexedName.BlindIndexLength, out byte[]? decoded))
        {
            return false;
        }

        // The short side, which is the whole of what this type adds. Measured: 31 bytes encodes to 42
        // characters, the allowance for a ceiling of 32 is 44, and the decoder's post-decode
        // comparison is a `>`, so a short value clears every gate below and arrives here intact. No
        // ceiling of any tightness can see it.
        //
        // Written as an inequality against the width rather than as a lower bound of its own, so that
        // both sides stay refused however the shared ceiling later moves. It is not padded or
        // truncated into shape: either repair stores a well-formed row holding a value that matches
        // nothing, and the row looks correct until somebody searches for the name it was supposed to
        // find.
        if (decoded.Length != IndexedName.BlindIndexLength)
        {
            return false;
        }

        blindIndex = decoded;

        return true;
    }
}
