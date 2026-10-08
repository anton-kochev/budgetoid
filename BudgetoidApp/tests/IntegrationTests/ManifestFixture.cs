using System.Security.Cryptography;
using Domain.Security;
using Domain.Users;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// One account's manifest of factor public keys as a client mints it: the authenticated blob naming
/// every recovery factor, sealed under the account's content key, and carried on the wire as unpadded
/// base64url.
/// </summary>
/// <remarks>
/// <para>
/// Shared for <see cref="WrappedKeyFixture" />'s reason and no other: what is shared is not a route's
/// shape but the numbers a payload has to satisfy — the framing's version byte, the framing's floor and
/// the column's cap. All three are read off <see cref="CiphertextEnvelope" /> and
/// <see cref="FactorManifest" />, the same constants the handler's own refusal sentence is rendered
/// from, so no copy of any of them can drift into a fixture that keeps passing after the real bound
/// moved.
/// </para>
/// <para>
/// <b>A band where <see cref="WrappedKeyFixture" /> has two exact widths, and the difference is the
/// whole reason this is a separate type.</b> Both wrapped-key payloads have exactly one legal size
/// because each plaintext is a fixed-width key; a manifest's plaintext is a list that grows with the
/// number of factors the account holds, so there is no width to compare against and the only bounds are
/// <see cref="CiphertextEnvelope.MinimumLength" /> and <see cref="FactorManifest.MaximumBytes" />.
/// Routing a manifest through the neighbour would measure it against a floor 65 bytes above its own —
/// both framings lead with <c>0x01</c> and nothing in the bytes says which — so this type exists to keep
/// the two apart in a fixture as well as in production.
/// </para>
/// <para>
/// <b><see cref="Mint" /> is neither bound and is not a round number, and that is a correction rather
/// than a preference.</b> Every manifest fixture written for the read side of this feature was exactly
/// ten bytes wide, which made a ten-byte truncation the identity function on all of them: a projection
/// that narrowed the column would have passed every one of those tests. <see cref="PlausibleLength" />
/// is what a real manifest naming the eleven factors one registration writes actually measures, so a
/// truncation, a padding or a re-encode has somewhere to show. The two bounds are reachable by name —
/// <see cref="AtTheFloor" /> and <see cref="AtTheCap" /> — for the cases that are about the bounds.
/// </para>
/// <para>
/// <b>Nothing here seals anything and nothing ever will.</b> A manifest is sealed under the account's
/// content key, which no server in this system has ever held, so the bytes are judged on their framing
/// and on nothing else — a blob naming eleven factors, one naming two, and random noise of a legal width
/// are the same value to every layer below the wire. Random bytes of the right shape are therefore the
/// whole requirement, and a fixture that built a plausible-looking list inside the envelope would be
/// claiming a check this product does not make.
/// </para>
/// </remarks>
/// <param name="Manifest">The bytes themselves, exactly as the wire will carry them.</param>
public sealed record ManifestFixture(byte[] Manifest)
{
    /// <summary>
    /// The byte that says this payload is a manifest rather than one of the two wrapped-key envelopes.
    /// </summary>
    /// <remarks>
    /// <see cref="WrappedKeyFixture" />'s argument for its own two, and it applies more strongly here:
    /// a manifest and a wrapped private key share a version byte and a framing, so the width is the only
    /// thing that tells a misrouted payload from a correct one — and a manifest's width is a band. It
    /// sits immediately after the version, where a real payload carries the first byte of its nonce;
    /// nothing opens anything here and no rule looks past the leading byte, so the position costs
    /// nothing.
    /// </remarks>
    private const byte ManifestPurpose = 0x3F;

    /// <summary>How many bytes the client's encoding spends on the factor count itself.</summary>
    private const int FactorCountBytes = 2;

    /// <summary>
    /// How many bytes one named factor costs: its identifier beside its uncompressed P-256 public key,
    /// as the client encodes them.
    /// </summary>
    /// <remarks>
    /// An estimate, and nothing turns on its exactness. What it is for is a default width that is
    /// plausible rather than convenient — see the type's remarks on the ten-byte fixtures this replaces.
    /// </remarks>
    private const int BytesPerFactor = 103;

    /// <summary>
    /// What a manifest naming the eleven factors one registration writes measures: the framing, the
    /// count, and one entry per factor.
    /// </summary>
    /// <remarks>
    /// Computed rather than written out, so it moves with the framing's own floor and with the card's
    /// size instead of standing still while both change underneath it.
    /// </remarks>
    public static int PlausibleLength =>
        CiphertextEnvelope.MinimumLength
        + FactorCountBytes
        + ((RegistrationCeremony.RequiredCodeCount + 1) * BytesPerFactor);

    /// <summary>The manifest as base64url, unpadded, which is the only spelling the route accepts.</summary>
    public string Text => Base64UrlText.Encode(Manifest);

    /// <summary>A manifest of a width a real one naming eleven factors would have.</summary>
    public static ManifestFixture Mint() => Of(PlausibleLength);

    /// <summary>
    /// The narrowest manifest the framing admits: version, nonce and tag with no ciphertext between
    /// them.
    /// </summary>
    /// <remarks>
    /// Named rather than spelled as <c>Of(CiphertextEnvelope.MinimumLength)</c> at a call site, so the
    /// case that proves the floor is <em>accepted</em> reads as the claim it is. The database's own
    /// lower bound is <c>length(manifest) between 1 and 4096</c> and is vacuous beside this one.
    /// </remarks>
    public static ManifestFixture AtTheFloor() => Of(CiphertextEnvelope.MinimumLength);

    /// <summary>The widest manifest the column will hold.</summary>
    public static ManifestFixture AtTheCap() => Of(FactorManifest.MaximumBytes);

    /// <summary>
    /// A manifest of exactly <paramref name="length" /> bytes carrying the framing's own version.
    /// </summary>
    public static ManifestFixture Of(int length) => new(Payload(length, CiphertextEnvelope.Version));

    /// <summary>
    /// A manifest of exactly <paramref name="length" /> bytes whose leading byte is
    /// <paramref name="version" /> — which only a test claiming a framing this deployment does not
    /// implement has any business asking for.
    /// </summary>
    public static ManifestFixture Of(int length, byte version) => new(Payload(length, version));

    /// <summary>
    /// <paramref name="length" /> random bytes carrying <paramref name="version" /> and the purpose
    /// byte.
    /// </summary>
    /// <remarks>
    /// The purpose byte is written only when there is room for it, because the floor is reachable by
    /// name and a two-byte prologue has to fit inside every width this helper is asked for. Random
    /// rather than filled, so two manifests minted in one test are distinct and a read-back that named
    /// the wrong account cannot pass on bytes nobody wrote for it.
    /// </remarks>
    private static byte[] Payload(int length, byte version)
    {
        byte[] payload = RandomNumberGenerator.GetBytes(length);
        payload[0] = version;

        if (length > 1)
        {
            payload[1] = ManifestPurpose;
        }

        return payload;
    }
}
