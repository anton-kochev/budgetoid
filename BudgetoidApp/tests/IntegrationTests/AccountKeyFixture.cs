using System.Security.Cryptography;
using System.Text;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// One account's two keys, and factors that really carry them: the openable counterpart to
/// <see cref="WrappedKeyFixture" />, and a thin face over <see cref="ClientKeyCustody" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>What <see cref="WrappedKeyFixture.Mint" /> and <see cref="WrappedKeyFixture.MintFor" /> produce
/// opens nothing, and that is a property, not a gap.</b> Their payloads are the right version byte, a
/// purpose byte, and random bytes to the right width — everything the server checks — so the many cases
/// whose claim is about width, version or alphabet get a well-formed value without two P-256 key
/// generations (the factor's pair and the ephemeral one), an ECDH agreement, an HKDF and two AES-GCM
/// operations per factor. <see cref="WrappedKeyFixture" /> itself is a public record, and
/// <c>AccountRegistrationTests</c> already builds openable ones through its constructor from real
/// cryptography (<c>SealAccountKeysFor</c>, opened by <c>OpenedUnder</c>). This fixture is the shared
/// path to the same thing, not the suite's only one.
/// </para>
/// <para>
/// <b>This fixture exists only for cases whose claim is that something opens</b> — that a stored pair
/// still yields the account's keys, or that a narrative value sealed before some change is still
/// readable after it.
/// </para>
/// <para>
/// <b>The two must never be merged</b>, and not for speed: an openable factor costs well under a
/// millisecond beside a PostgreSQL container per test. Two reasons hold it. The cheap fixture writes a
/// purpose byte at offset 1, so a failure from a swapped column is readable by eye in the printed
/// bytes; a real envelope cannot carry one, because that position holds a nonce byte or the first byte
/// of a point. And the choice made at the call site states which claim a test is making — that a value
/// is well-formed, or that it opens.
/// </para>
/// <para>
/// <b>What this fixture still cannot hold: the order of the two halves inside the encapsulated
/// plaintext, when both sides flip together.</b> <see cref="ClientKeyCustody.EncapsulateAccountKeys" />
/// writes content key first and <see cref="ClientKeyCustody.TryOpenAccountKeys" /> reads it back. Flip
/// the reader alone and a case comparing what opened against <see cref="ContentKey" /> goes red. Flip
/// the writer and the reader together and the round trip reproduces itself, so every case here stays
/// green. That silence is inherited from <see cref="ClientKeyCustody" />; the only place the order is
/// held against both flipping is <c>ClientKeyCustodyTests</c>, against the frozen vector.
/// </para>
/// </remarks>
internal sealed class AccountKeyFixture
{
    private AccountKeyFixture(byte[] contentKey, byte[] indexKey)
    {
        ContentKey = contentKey;
        IndexKey = indexKey;
    }

    /// <summary>The account's content key: narrative values and the manifest are sealed under it.</summary>
    public byte[] ContentKey { get; }

    /// <summary>The account's index key: blind indexes are computed under it.</summary>
    public byte[] IndexKey { get; }

    /// <summary>
    /// A fresh account: two keys drawn once, which every factor minted from this fixture carries.
    /// </summary>
    public static AccountKeyFixture Mint() =>
        new(
            RandomNumberGenerator.GetBytes(ClientKeyCustody.KeyBytes),
            RandomNumberGenerator.GetBytes(ClientKeyCustody.KeyBytes));

    /// <summary>
    /// A fresh factor: its own identifier, its own key-encryption key, its own P-256 pair, and this
    /// account's two keys encapsulated to that pair's public half.
    /// </summary>
    /// <remarks>
    /// The key-encryption key is 32 random bytes. A real one is derived — HKDF-SHA-256 over the PRF
    /// output for a passkey, over the canonical code for a recovery code — but the server never sees a
    /// key-encryption key, so random bytes are a faithful stand-in for any claim a server test can
    /// make. <see cref="MintRecoveryCodeFactor" /> derives one anyway, for its own reason. Called
    /// repeatedly against one fixture, so "these factors carry the same pair" is visible at the call
    /// site rather than arranged somewhere else.
    /// </remarks>
    public Factor MintFactor() =>
        MintFactorUnder(RandomNumberGenerator.GetBytes(ClientKeyCustody.KeyBytes));

    /// <summary>
    /// A fresh recovery-code factor: a code drawn the way the client draws one, the verifier a
    /// redemption would present, and a factor whose key-encryption key is derived from that same code.
    /// </summary>
    /// <remarks>
    /// <b>The faithful key-encryption key keeps the arrangement honest.</b> The verifier and the key both
    /// come from one code, so this is a code factor a later redemption-driven test can reuse as it is.
    /// The server never sees a key-encryption key, so no server mutation can tell this choice apart from
    /// 32 random bytes; the choice is about what the arrangement claims to be. The code itself is not
    /// carried, because nothing reads it.
    /// </remarks>
    public RecoveryCodeFactor MintRecoveryCodeFactor()
    {
        string code = ClientKeyCustody.MintRecoveryCode();

        return new RecoveryCodeFactor(
            ClientKeyCustody.VerifierOf(code),
            MintFactorUnder(ClientKeyCustody.KeyEncryptionKeyFromRecoveryCode(code)));
    }

    /// <summary>
    /// A factor over <paramref name="keyEncryptionKey" />: its own identifier, its own P-256 pair, and
    /// this account's two keys encapsulated to that pair's public half.
    /// </summary>
    private Factor MintFactorUnder(byte[] keyEncryptionKey)
    {
        Guid id = Guid.CreateVersion7();

        using ECDiffieHellman keyPair = ClientKeyCustody.CreateFactorKeyPair();
        using ECDiffieHellmanPublicKey publicKey = keyPair.PublicKey;

        return new Factor(
            id,
            keyEncryptionKey,
            ClientKeyCustody.WrapPrivateKey(keyEncryptionKey, keyPair, id),
            ClientKeyCustody.EncapsulateAccountKeys(publicKey, ContentKey, IndexKey, id));
    }

    /// <summary>
    /// Seals <paramref name="text" /> for one narrative column under this account's content key, as
    /// the wire carries it: unpadded base64url over the envelope.
    /// </summary>
    public string SealNarrative(string table, string column, Guid rowId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Base64UrlText.Encode(
            ClientKeyCustody.Seal(
                ContentKey,
                ClientKeyCustody.Utf8(text),
                ClientKeyCustody.NarrativeFieldAssociatedData(table, column, rowId)));
    }

    /// <summary>
    /// Opens a stored narrative value under <paramref name="contentKey" />, or returns
    /// <see langword="false" />.
    /// </summary>
    /// <remarks>
    /// <b>Static, and the content key is the caller's — do not "simplify" it into an instance method
    /// over <see cref="ContentKey" />.</b> A case asserting that a value is still readable after some
    /// change has to supply the key it believes the account now holds, typically one a factor just
    /// opened out of the database. Opened under the fixture's own key instead, the assertion is a
    /// tautology: the fixture sealed it, so the fixture opens it, whatever happened in between. The
    /// static shape keeps the key the caller's own, but it is not enough on its own: a caller that has
    /// already asserted the recovered key equals <see cref="ContentKey" /> gets nothing from this open
    /// against production — only protection against that assertion being edited away later.
    /// A wire value that is not base64url, and bytes that authenticate but are not UTF-8, are refusals
    /// like any other rather than throws.
    /// </remarks>
    public static bool TryOpenNarrative(
        byte[] contentKey,
        string table,
        string column,
        Guid rowId,
        string wire,
        out string text)
    {
        ArgumentNullException.ThrowIfNull(contentKey);
        ArgumentNullException.ThrowIfNull(wire);

        text = string.Empty;

        byte[] envelope;

        try
        {
            envelope = Base64UrlText.Decode(wire);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!ClientKeyCustody.TryOpen(
                contentKey,
                envelope,
                ClientKeyCustody.NarrativeFieldAssociatedData(table, column, rowId),
                out byte[] plaintext))
        {
            return false;
        }

        try
        {
            text = StrictUtf8.GetString(plaintext);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return true;
    }

    /// <summary>UTF-8 that throws on an invalid sequence rather than substituting U+FFFD.</summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>One recovery code of this account, as a set issue carries it.</summary>
    /// <param name="Verifier">What the code's redemption presents, base64url as the wire carries it.</param>
    /// <param name="Factor">The code's factor, its key-encryption key derived from the same code.</param>
    internal sealed record RecoveryCodeFactor(string Verifier, Factor Factor);

    /// <summary>One factor of this account, as a client mints it and as the write paths carry it.</summary>
    /// <param name="Id">The factor identifier both values are bound to.</param>
    /// <param name="KeyEncryptionKey">The 32 bytes the private key is wrapped under.</param>
    /// <param name="PrivateKeyEnvelope">The wrapped private key, as sent.</param>
    /// <param name="AccountKeysEnvelope">The account's two keys encapsulated to this factor, as sent.</param>
    internal sealed record Factor(
        Guid Id,
        byte[] KeyEncryptionKey,
        byte[] PrivateKeyEnvelope,
        byte[] AccountKeysEnvelope)
    {
        /// <summary>
        /// The identifier as the wire spells it: <c>"D"</c>, lower-case and hyphenated, the only
        /// spelling the routes accept.
        /// </summary>
        public string FactorId => ClientKeyCustody.CanonicalFactorId(Id);

        /// <summary>The wrapped private key, as unpadded base64url.</summary>
        public string WrappedPrivateKey => Base64UrlText.Encode(PrivateKeyEnvelope);

        /// <summary>The encapsulated account keys, as unpadded base64url.</summary>
        public string EncapsulatedAccountKeys => Base64UrlText.Encode(AccountKeysEnvelope);

        /// <summary>
        /// Opens what the database handed back, under this factor's own key-encryption key and
        /// identifier, or returns <see langword="false" />.
        /// </summary>
        /// <remarks>
        /// <b>Takes the stored bytes rather than reading <see cref="PrivateKeyEnvelope" /> and
        /// <see cref="AccountKeysEnvelope" /> — do not "simplify" the parameters away.</b> Opening the
        /// values this record minted proves only that the codec agrees with itself; the claim a case
        /// makes is that the row the server stored, under the factor it filed it under, still opens.
        /// The stored bytes are <em>necessary, not sufficient</em>: a case that also asserts stored ==
        /// minted ahead of the open makes the open redundant against production, since the minted
        /// values open by construction. The open carries the load only where no byte equality
        /// precedes it.
        /// </remarks>
        public bool TryOpen(
            ReadOnlyMemory<byte> storedWrappedPrivateKey,
            ReadOnlyMemory<byte> storedEncapsulatedAccountKeys,
            out byte[] contentKey,
            out byte[] indexKey) =>
            ClientKeyCustody.TryOpenAccountKeys(
                KeyEncryptionKey,
                storedWrappedPrivateKey.ToArray(),
                storedEncapsulatedAccountKeys.ToArray(),
                Id,
                out contentKey,
                out indexKey);
    }
}
