using System.Security.Cryptography;
using Domain.Users;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// One recovery factor's share of the account: the factor's private key wrapped under the
/// key-encryption key it derives, and the account's two keys encapsulated to that factor's public
/// key — as a client mints them and as the three write paths carry them on the wire.
/// </summary>
/// <remarks>
/// <para>
/// Shared across the suite, which the ceremony helpers deliberately are not. What is shared here is not
/// a route's shape — every file still spells its own request out — but four numbers: each payload's
/// width and each payload's version byte. All four are read off <see cref="WrappedAccountKeys" />, the
/// same constants the database's own check constraints are rendered from, so no copy of any of them
/// can drift into a fixture that keeps passing after the real bound moved.
/// </para>
/// <para>
/// <b>Minted per call, never once per file.</b> <c>factor_id</c> is the table's primary key —
/// <c>PK_wrapped_account_keys</c> — so it is unique across the whole table rather than per account, and
/// a shared constant would turn the second write
/// anywhere in one database into a <c>23505</c> — and the tests that would meet it are the ones that
/// register or issue twice on purpose, to measure something else entirely.
/// </para>
/// <para>
/// <b>The two payloads are values of two different cryptographic suites, and the fixture says so four
/// ways.</b> <see cref="WrappedPrivateKeyBytes" /> is a <c>CiphertextEnvelope</c> —
/// <c>version || nonce || ciphertext || tag</c> — at exactly
/// <see cref="WrappedAccountKeys.WrappedPrivateKeyLength" /> bytes;
/// <see cref="EncapsulatedAccountKeysBytes" /> is an <c>EncapsulatedValueEnvelope</c> —
/// <c>version || ephemeral public key || nonce || ciphertext || tag</c> — at exactly
/// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" /> bytes. <b>The widths differing is
/// what makes a transposition catchable at all</b>, and it is a fact about the suites rather than a
/// choice anybody made here: 167 against 158, from a 138-byte PKCS#8 private key under a 29-byte AEAD
/// framing versus a 64-byte key pair under a 94-byte encapsulation framing.
/// </para>
/// <para>
/// <b>The purpose byte is kept even so, and it is not redundant.</b> A check constraint reports a
/// constraint name; a purpose byte is visible by eye in a byte-array failure message, which is what a
/// read-back assertion actually prints. It sits immediately after the version, where a real payload
/// carries the first byte of its nonce or of its ephemeral point — nothing here opens anything, and no
/// rule any layer holds looks past the leading byte, so the position costs nothing.
/// </para>
/// </remarks>
public sealed record WrappedKeyFixture(
    Guid Factor,
    byte[] PrivateKeyEnvelope,
    byte[] AccountKeysEnvelope)
{
    /// <summary>
    /// The byte that says which of the two payloads this is. Retained for failure-message legibility;
    /// the widths are what a constraint refuses a swap on.
    /// </summary>
    private const byte PrivateKeyPurpose = 0xC0;

    private const byte AccountKeysPurpose = 0x1D;

    /// <summary>
    /// The factor identifier as the wire spells it: the hyphenated 36-character form, which is the only
    /// spelling the route accepts.
    /// </summary>
    public string FactorId => Factor.ToString("D");

    /// <summary>The factor's private key, wrapped under its key-encryption key, as base64url.</summary>
    public string WrappedPrivateKey => Base64UrlText.Encode(PrivateKeyEnvelope);

    /// <summary>
    /// The account's content key and index key as one 64-byte plaintext, content key first,
    /// encapsulated to this factor's public key, as base64url.
    /// </summary>
    public string EncapsulatedAccountKeys => Base64UrlText.Encode(AccountKeysEnvelope);

    /// <summary>A fresh factor, and a distinguishable pair of payloads bound to it.</summary>
    public static WrappedKeyFixture Mint() => MintFor(Guid.CreateVersion7());

    /// <summary>
    /// The same, against a factor identifier the caller already holds — which only a test claiming a
    /// factor that is already registered has any business asking for.
    /// </summary>
    public static WrappedKeyFixture MintFor(Guid factorId) =>
        new(
            factorId,
            Payload(
                WrappedAccountKeys.WrappedPrivateKeyLength,
                WrappedAccountKeys.WrappedPrivateKeyVersion,
                PrivateKeyPurpose),
            Payload(
                WrappedAccountKeys.EncapsulatedAccountKeysLength,
                WrappedAccountKeys.EncapsulatedAccountKeysVersion,
                AccountKeysPurpose));

    /// <summary>
    /// A well-formed payload of one suite: that suite's own version, the purpose byte, and random bytes
    /// to the column's exact width.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The width and the version are parameters rather than read inside, because the one mistake this
    /// helper could make is pairing one suite's width with the other's version — the cross-wiring the
    /// two constants exist to keep apart. Written out at both call sites, the pairing is one line a
    /// reader checks rather than a branch they have to follow.
    /// </para>
    /// <para>
    /// Random rather than filled, so two payloads minted for two factors of the same account are
    /// distinct as well — a read-back that named the wrong row would otherwise pass on bytes nobody
    /// wrote for it.
    /// </para>
    /// </remarks>
    private static byte[] Payload(int length, byte version, byte purpose)
    {
        byte[] payload = RandomNumberGenerator.GetBytes(length);
        payload[0] = version;
        payload[1] = purpose;

        return payload;
    }
}
