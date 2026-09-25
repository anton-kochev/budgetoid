using System.Diagnostics.CodeAnalysis;
using Application.Security;
using Domain.Security;
using Domain.Users;

namespace Application.Passkeys;

/// <summary>
/// The one decode-and-validate step for every factor manifest that crosses the wire: the authenticated
/// blob naming every recovery factor and its public key, sealed under the account's content key.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Try</c> shape rather than a throw, for the reason <see cref="PasskeyEncoding.TryDecode"/> gives
/// for its own: the call site turns a malformed member into a refusal sentence of its own, worded for
/// the member a caller can correct.
/// </para>
/// <para>
/// <b>What this type adds over <see cref="CiphertextEnvelopeText"/> is not a check — it is the two
/// choices the call site would otherwise have to make correctly, made once and written down.</b> The
/// first is the framing: a manifest is <em>sealed under</em> the account's content key, so it carries
/// <see cref="CiphertextEnvelope"/> and never <see cref="EncapsulatedValueEnvelope"/>, and both lead
/// with <c>0x01</c> — routing it through the neighbour would measure it against a floor 65 bytes above
/// its own and refuse every manifest an account has. The second is the ceiling, which is
/// <see cref="FactorManifest.MaximumBytes"/> read off the entity rather than a number of this ring's
/// own, for the reason <see cref="PasskeyPayloadLimits.WrappedPrivateKeyBytes"/> gives about its own
/// borrowing: a member the wire accepted but the entity could never store is text decoded for nothing,
/// and a second number here would only be a way for this ceiling and the column's own
/// <c>CHECK length(...)</c> to disagree.
/// </para>
/// <para>
/// <b>This one is a band and not an equality, and that is the difference a reader has to carry away
/// from the three of them.</b> <see cref="WrappedPrivateKeyEnvelope"/> closes on exactly 167 bytes and
/// <see cref="EncapsulatedAccountKeysEnvelope"/> on exactly 158, because AES-GCM ciphertext is the
/// length of its plaintext and both of those plaintexts are fixed-width keys — so each has exactly one
/// legal size and anything else is refused before it is stored. A manifest's plaintext is a list that
/// grows with the number of factors the account has, so there is no width to compare against and no
/// check of that kind can exist here. <b>What that costs is worth stating plainly: this server cannot
/// tell a manifest naming eleven factors from one naming two, from one naming none, or from 4096 bytes
/// of noise a client sent instead.</b> Only a client holding the content key can, by opening it and
/// checking the authentication tag — which is the same limit the <c>user_isolation</c> policy records
/// about itself, and it is deliberate rather than a gap to be closed by parsing the blob here.
/// </para>
/// <para>
/// <b>The database's own lower bound is vacuous and this is the real one.</b>
/// <c>CK_factor_manifests_manifest_length</c> reads <c>length(manifest) between 1 and 4096</c>, and a
/// one-byte manifest is not a manifest — it is not even an envelope, because the shortest value of this
/// framing is <see cref="CiphertextEnvelope.MinimumLength"/> bytes of version, nonce and tag with no
/// ciphertext between them. That <c>CHECK</c> is not widened or narrowed to match: it is a fact about
/// <c>bytea</c> not being empty, which is what the column can say declaratively, and moving it would be
/// a schema change for a bound this member already refuses at the edge. So the floor that does the work
/// is the framing's, applied here, and the floor in the catalog is the emptiness rule wearing a number.
/// </para>
/// </remarks>
public static class FactorManifestEnvelope
{
    /// <summary>
    /// Decodes <paramref name="value"/>, or returns false when it is absent, not base64url, shorter than
    /// <see cref="CiphertextEnvelope.MinimumLength"/> bytes, wider than
    /// <see cref="FactorManifest.MaximumBytes"/>, or does not carry
    /// <see cref="CiphertextEnvelope.Version"/>.
    /// </summary>
    /// <param name="value">The text a caller supplied.</param>
    /// <param name="manifest">The manifest bytes, when the value was accepted.</param>
    public static bool TryDecode(
        [NotNullWhen(true)] string? value,
        [NotNullWhen(true)] out byte[]? manifest)
    {
        manifest = null;

        // CiphertextEnvelopeText and never EncapsulatedValueEnvelopeText: a manifest is sealed under the
        // account's content key, with no key agreement in front of it. The neighbour clears the same
        // version byte over a floor that counts a 65-byte ephemeral point this value does not carry.
        //
        // Nothing follows this call, unlike both siblings. Their trailing equality closes the band
        // between the framing's floor and their one legal width; a manifest has no such width, so an
        // equality here would be a number invented to have one.
        if (!CiphertextEnvelopeText.TryDecode(value, FactorManifest.MaximumBytes, out byte[]? decoded))
        {
            return false;
        }

        manifest = decoded;

        return true;
    }
}
