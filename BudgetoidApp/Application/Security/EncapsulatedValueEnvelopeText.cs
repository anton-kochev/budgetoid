using System.Diagnostics.CodeAnalysis;
using Application.Passkeys;
using Domain.Security;

namespace Application.Security;

/// <summary>
/// The one decode-and-validate step for every value carrying the
/// <see cref="EncapsulatedValueEnvelope"/> framing that a client hands the API as text: base64url
/// within a ceiling the caller names, then the framing rules that type owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>The second member <see cref="CiphertextEnvelopeText"/> said would be needed, written rather than
/// folded into it.</b> That type states the shape outright: a decoder for the other framing is a second
/// member beside it and never a rule passed into it. The reason is what the two framings share. Both
/// lead with <c>0x01</c>, both end in an AES-256-GCM tag, and three of their five widths are the same
/// number — so a single decoder taking a well-formedness delegate would read at every call site as
/// choosing a formatting option, and the call that chose wrong would be accepted. Text carrying an
/// encapsulated value passed to the neighbour decodes, clears the version check and is measured
/// against a floor 65 bytes below its own; text carrying an AEAD envelope passed to this one is
/// refused, which is the harmless direction and not a reason to rely on it.
/// </para>
/// <para>
/// <b>That framing and no other.</b> The member below names
/// <see cref="EncapsulatedValueEnvelope.IsWellFormed"/> outright rather than taking a rule as an
/// argument, so what it accepts is the one layout — version, ephemeral point, nonce, ciphertext, tag.
/// Nothing in the bytes says which of the two framings they are; the column, or the request member,
/// is the only discriminator, and that is what these two types make explicit at the edge.
/// </para>
/// <para>
/// A <c>Try</c> shape rather than a throw, for the reason <see cref="PasskeyEncoding.TryDecode"/> gives
/// for its own: every call site already turns a malformed member into a refusal sentence of its own,
/// and those sentences differ. Nothing here reports <em>why</em>, and the <c>out</c> parameter is left
/// <see langword="null"/> on every refusal so that a caller which reads the buffer without reading the
/// result finds nothing to work with.
/// </para>
/// <para>
/// <b>In <c>Application</c> and not in <c>Domain</c>, because base64url is a wire concern</b> — the
/// split <see cref="CiphertextEnvelopeText"/> argues for itself. <see cref="EncapsulatedValueEnvelope"/>
/// owns the format and knows nothing about how the bytes travelled; this type is the edge that turns
/// text into those bytes and applies the format's rules to the result. It defines no number of its own
/// — the floor and the version come from the domain, the ceiling comes from the caller — which is what
/// keeps the edge and the format from drifting apart.
/// </para>
/// <para>
/// <b>The ceiling is a parameter, and it has to be</b>, the same argument the sibling makes and not a
/// habit copied from it. The framing is defined over any plaintext: the account's two keys come to one
/// width, and the next value encapsulated to a factor will come to another. A constant here would be
/// one number pretending to serve all of them, and the first value that did not fit it would be refused
/// for a limit nobody wrote for it. <b>The ceiling is also not the width</b> — it bounds the wide side
/// only, and the band between this format's floor and a consumer's exact width is what no shared rule
/// can see. <see cref="Passkeys.EncapsulatedAccountKeysEnvelope"/> is what closes that band for the one
/// consumer there is today.
/// </para>
/// </remarks>
public static class EncapsulatedValueEnvelopeText
{
    /// <summary>
    /// Decodes <paramref name="value"/>, or returns false when it is absent, not base64url, longer than
    /// <paramref name="maxDecodedBytes"/>, shorter than
    /// <see cref="EncapsulatedValueEnvelope.MinimumLength"/>, or does not lead with
    /// <see cref="EncapsulatedValueEnvelope.Version"/>.
    /// </summary>
    /// <param name="value">The text a caller supplied.</param>
    /// <param name="maxDecodedBytes">
    /// The largest value this member may decode to, in bytes. Required rather than optional, for the
    /// reason <see cref="PasskeyEncoding.TryDecode"/> requires its own: a decode nobody bounded is work
    /// whose size the sender chose.
    /// </param>
    /// <param name="encapsulated">The encapsulated value's bytes, when the text was accepted.</param>
    /// <returns>
    /// <see langword="true"/> when the text decoded to a value this deployment can interpret; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool TryDecode(
        [NotNullWhen(true)] string? value,
        int maxDecodedBytes,
        [NotNullWhen(true)] out byte[]? encapsulated)
    {
        encapsulated = null;

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

        // The floor and the version, in that order and from the type that owns them. Version 2 is a
        // client claiming a suite this deployment has never implemented, and version 0 is a field
        // nobody set — an all-zero buffer of a legal length is what an uninitialised member, a
        // zero-filled allocation and a stubbed client all send. Stored either way the symptom is silent
        // and late, because the row is well-formed and the bytes turn out to be uninterpretable on the
        // day somebody rotates.
        //
        // EncapsulatedValueEnvelope.IsWellFormed and never CiphertextEnvelope.IsWellFormed, which is
        // the edit that reads as reuse: the two share their version byte and three of their five
        // widths, so delegating compiles, passes every version case, and admits every buffer 65 bytes
        // too short to hold an ephemeral point.
        //
        // A floor and not a width, deliberately: AES-GCM ciphertext is exactly the length of its
        // plaintext, so a width folded in here would tie the format to its first consumer and refuse
        // the next.
        if (!EncapsulatedValueEnvelope.IsWellFormed(decoded))
        {
            return false;
        }

        encapsulated = decoded;

        return true;
    }
}
