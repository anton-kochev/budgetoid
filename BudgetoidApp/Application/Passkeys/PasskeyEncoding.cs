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
    /// Decodes <paramref name="value"/>, or returns false when it is absent, empty, longer than
    /// <paramref name="maxDecodedBytes"/> would allow, or not base64url.
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
    /// The length is judged before <see cref="Base64Url.IsValid(ReadOnlySpan{char})"/> rather than
    /// after, and that ordering is the whole point of the parameter. Validation is a full pass over
    /// the text and decoding is a second one plus an allocation the size of the result; performing
    /// either first would mean an oversized member has already cost what the ceiling exists to refuse.
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

        decoded = Base64Url.DecodeFromChars(value);

        return true;
    }

    /// <summary>
    /// The longest base64url text that can encode <paramref name="decodedBytes"/> bytes.
    /// </summary>
    /// <remarks>
    /// The padded form, four characters per three bytes rounded up, even though base64url omits the
    /// padding: <see cref="Base64Url"/> accepts a padded value, so the looser of the two lengths is
    /// the one that never refuses a member a client legitimately encoded. It overshoots by at most two
    /// characters, which no ceiling here is tight enough to care about.
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
