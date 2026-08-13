using System.Security.Cryptography;
using Domain.Users;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// One recovery factor's share of the account keys, as a client mints it and as the two write paths
/// carry it on the wire.
/// </summary>
/// <remarks>
/// <para>
/// Shared across the suite, which the ceremony helpers deliberately are not. What is shared here is not
/// a route's shape — every file still spells its own request out — but two numbers: the envelope's
/// width and its version byte. Both are read off <see cref="WrappedAccountKeys" />, the same constants
/// the database's own check constraints are rendered from, so no copy of either can drift into a
/// fixture that keeps passing after the real bound moved.
/// </para>
/// <para>
/// <b>Minted per call, never once per file.</b> <c>IX_wrapped_account_keys_factor_id</c> is unique
/// across the whole table rather than per account, so a shared constant would turn the second write
/// anywhere in one database into a <c>23505</c> — and the tests that would meet it are the ones that
/// register or issue twice on purpose, to measure something else entirely.
/// </para>
/// <para>
/// <b>The two envelopes differ in a byte chosen for the purpose.</b> Both are
/// <see cref="WrappedAccountKeys.EnvelopeLength" /> bytes, both carry the same version, both columns
/// are <c>NOT NULL</c>: a handler that files each in the other's column satisfies every width check,
/// every version check and every database constraint, and the discovery happens in a browser months
/// later. Only a pair whose bytes differ can catch that, and a difference left to chance is one a
/// reader has to take on trust — so the byte after the version says which envelope this is.
/// </para>
/// </remarks>
public sealed record WrappedKeyFixture(Guid Factor, byte[] ContentEnvelope, byte[] IndexEnvelope)
{
    /// <summary>
    /// The factor identifier as the wire spells it: the hyphenated 36-character form, which is the only
    /// spelling the route accepts.
    /// </summary>
    public string FactorId => Factor.ToString("D");

    public string WrappedContentKey => Base64UrlText.Encode(ContentEnvelope);

    public string WrappedIndexKey => Base64UrlText.Encode(IndexEnvelope);

    /// <summary>A fresh factor, and a distinguishable pair of envelopes bound to it.</summary>
    public static WrappedKeyFixture Mint() => MintFor(Guid.CreateVersion7());

    /// <summary>
    /// The same, against a factor identifier the caller already holds — which only a test claiming a
    /// factor that is already registered has any business asking for.
    /// </summary>
    public static WrappedKeyFixture MintFor(Guid factorId) =>
        new(factorId, Envelope(ContentPurpose), Envelope(IndexPurpose));

    /// <summary>
    /// The byte that says which of the two envelopes this is. It sits immediately after the version,
    /// where a real envelope carries the first byte of its nonce — nothing here opens an envelope, and
    /// no rule any layer holds looks past the leading byte, so the position costs nothing and buys a
    /// swap that is visible by eye in a failure message.
    /// </summary>
    private const byte ContentPurpose = 0xC0;

    private const byte IndexPurpose = 0x1D;

    /// <summary>
    /// A well-formed envelope: the one version the contract defines, the purpose byte, and random bytes
    /// to the column's exact width.
    /// </summary>
    /// <remarks>
    /// Random rather than filled, so two envelopes minted for two factors of the same account are
    /// distinct as well — a read-back that named the wrong row would otherwise pass on bytes nobody
    /// wrote for it.
    /// </remarks>
    private static byte[] Envelope(byte purpose)
    {
        byte[] envelope = RandomNumberGenerator.GetBytes(WrappedAccountKeys.EnvelopeLength);
        envelope[0] = WrappedAccountKeys.EnvelopeVersion;
        envelope[1] = purpose;

        return envelope;
    }
}
