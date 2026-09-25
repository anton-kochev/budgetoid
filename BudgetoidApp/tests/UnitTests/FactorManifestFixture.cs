using System.Security.Cryptography;
using Domain.Security;
using Domain.Users;
using TestSupport;

namespace UnitTests;

/// <summary>
/// One account's manifest of factor public keys as a client mints it — the bytes, and the unpadded
/// base64url spelling a command carries them in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The integration suite's <c>ManifestFixture</c> in miniature, and it is a second type rather than
/// a shared one because the two assemblies cannot reach each other.</b> What is shared between them is
/// only the two numbers a payload has to satisfy — <see cref="CiphertextEnvelope.Version" /> and
/// <see cref="CiphertextEnvelope.MinimumLength" /> — and both are read off the types the handler's own
/// refusal is rendered from, so neither copy can keep passing after the real bound moves.
/// </para>
/// <para>
/// <b>Nothing here seals anything and nothing ever will.</b> A manifest is sealed under the account's
/// content key, which no server in this system has ever held, so every layer below the wire judges the
/// blob on its framing and on nothing else: a manifest naming eleven factors, one naming none, and
/// random noise of a legal width are one value here. Random bytes of the right shape are therefore the
/// whole requirement, and two manifests minted in one test are distinct — which is what lets a
/// read-back assert <em>which</em> manifest landed rather than only that one did.
/// </para>
/// <para>
/// <b>Deliberately wider than the floor.</b> A fixture sitting exactly on
/// <see cref="CiphertextEnvelope.MinimumLength" /> makes a truncation at that width the identity
/// function, so a handler or a fake that narrowed the blob would pass over it.
/// </para>
/// </remarks>
/// <param name="Manifest">The bytes themselves, exactly as the wire will carry them.</param>
public sealed record FactorManifestFixture(byte[] Manifest)
{
    /// <summary>
    /// The byte that says this payload is a manifest rather than one of the two wrapped-key envelopes,
    /// carried for the same reason <c>IntegrationTests.ManifestFixture</c> carries one: all three
    /// framings lead with <c>0x01</c>, so a misrouted payload is visible by eye in the byte-array
    /// failure message a read-back assertion prints. It sits immediately after the version, where a
    /// real payload carries the first byte of its nonce; nothing here opens anything.
    /// </summary>
    private const byte ManifestPurpose = 0x3F;

    /// <summary>
    /// The width a fixture takes when a caller does not name one — neither bound and not a round
    /// number, for the reason the type's remarks give.
    /// </summary>
    private const int PlausibleLength = CiphertextEnvelope.MinimumLength + 87;

    /// <summary>The manifest as base64url, unpadded, which is the only spelling a command accepts.</summary>
    public string Text => Base64UrlText.Encode(Manifest);

    /// <summary>A manifest of a width a real one naming a handful of factors would have.</summary>
    public static FactorManifestFixture Mint() => Of(PlausibleLength);

    /// <summary>A manifest of exactly <paramref name="length" /> bytes carrying the framing's version.</summary>
    public static FactorManifestFixture Of(int length)
    {
        byte[] payload = RandomNumberGenerator.GetBytes(length);
        payload[0] = CiphertextEnvelope.Version;

        if (length > 1)
        {
            payload[1] = ManifestPurpose;
        }

        return new FactorManifestFixture(payload);
    }

    /// <summary>
    /// The epoch an account's first manifest is stored at, which is what every seeding in these files
    /// files and what every request in them therefore promotes from.
    /// </summary>
    /// <remarks>
    /// Read off <see cref="FactorManifest.MinimumRotationEpoch" /> rather than written out, unlike
    /// <c>FactorManifestTests</c>, and the difference is which claim is being made. That file is
    /// <em>about</em> the floor, so reading the constant it checks would compare a constant with
    /// itself. Nothing here is about the floor: these files need the number a seeded account is at, and
    /// a literal would silently become the wrong arrangement the day the floor moved.
    /// </remarks>
    public static int SeededRotationEpoch => FactorManifest.MinimumRotationEpoch;

    /// <summary>The generation a request promoting a seeded account's manifest has to carry.</summary>
    public static int PromotedRotationEpoch => SeededRotationEpoch + 1;
}
