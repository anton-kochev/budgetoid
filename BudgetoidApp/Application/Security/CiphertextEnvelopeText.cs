using System.Diagnostics.CodeAnalysis;
using Application.Passkeys;
using Domain.Security;

namespace Application.Security;

/// <summary>
/// The one decode-and-validate step for every AEAD envelope a client hands the API as text: base64url
/// within a ceiling the caller names, then the framing rules
/// <see cref="CiphertextEnvelope"/> owns.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Try</c> shape rather than a throw, for the reason <see cref="PasskeyEncoding.TryDecode"/> gives
/// for its own: every call site already turns a malformed member into a refusal sentence of its own,
/// and those sentences differ. Nothing here reports <em>why</em>, and the <c>out</c> parameter is left
/// <see langword="null"/> on every refusal so that a caller which reads the buffer without reading the
/// result finds nothing to work with.
/// </para>
/// <para>
/// <b>In <c>Application</c> and not in <c>Domain</c>, because base64url is a wire concern.</b>
/// <see cref="CiphertextEnvelope"/> owns the format and knows nothing about how the bytes travelled;
/// this type is the edge that turns text into those bytes and applies the format's rules to the result.
/// It defines no number of its own — the floor and the version come from the domain, the ceiling comes
/// from the caller — which is what keeps the edge and the format from drifting apart.
/// </para>
/// <para>
/// <b>The ceiling is a parameter, and it has to be.</b> Every field sealed this way has its own size:
/// a note is as long as somebody typed, a wrapped key has exactly one width. A constant here would be
/// one number pretending to serve all of them, and the first field that did not fit it would be
/// refused for a limit nobody wrote for it.
/// </para>
/// </remarks>
public static class CiphertextEnvelopeText
{
    /// <summary>
    /// Decodes <paramref name="value"/>, or returns false when it is absent, not base64url, longer than
    /// <paramref name="maxDecodedBytes"/>, shorter than <see cref="CiphertextEnvelope.MinimumLength"/>,
    /// or does not lead with <see cref="CiphertextEnvelope.Version"/>.
    /// </summary>
    /// <param name="value">The text a caller supplied.</param>
    /// <param name="maxDecodedBytes">
    /// The largest envelope this member may decode to, in bytes. Required rather than optional, for the
    /// reason <see cref="PasskeyEncoding.TryDecode"/> requires its own: a decode nobody bounded is work
    /// whose size the sender chose.
    /// </param>
    /// <param name="envelope">The envelope bytes, when the value was accepted.</param>
    /// <returns>
    /// <see langword="true"/> when the text decoded to an envelope this deployment can interpret;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryDecode(
        [NotNullWhen(true)] string? value,
        int maxDecodedBytes,
        [NotNullWhen(true)] out byte[]? envelope)
    {
        envelope = null;

        // Reused rather than re-spelled: base64url is the one alphabet every binary member of this API
        // crosses JSON in, and two decoders with different bounds is a difference nobody meant. The
        // whole ceiling is that member's — the encoded text before anything is allocated, and the
        // decoded buffer once it exists, because the first of those is computed in the padded form and
        // overshoots by up to two bytes. Nothing is re-applied here: a second comparison against
        // maxDecodedBytes would be one rule with two owners, and the one that got edited would be
        // whichever the next reader happened to open.
        if (!PasskeyEncoding.TryDecode(value, maxDecodedBytes, out byte[]? decoded))
        {
            return false;
        }

        // The floor and the version, in that order and from the type that owns them. IFR-007 at the
        // edge: version 2 is a client claiming a contract this deployment has never implemented, and
        // version 0 is a field nobody set — an all-zero buffer of a legal length is what an
        // uninitialised member, a zero-filled allocation and a stubbed client all send. Stored either
        // way the symptom is silent and late, because the row is well-formed and the bytes turn out to
        // be uninterpretable on the day somebody needs them back.
        //
        // A floor and not a width, deliberately: AES-GCM ciphertext is exactly the length of its
        // plaintext, so folding a width in here would refuse every entry longer than an empty one and
        // the person would find out by not being able to save what they wrote.
        if (!CiphertextEnvelope.IsWellFormed(decoded))
        {
            return false;
        }

        envelope = decoded;

        return true;
    }
}
