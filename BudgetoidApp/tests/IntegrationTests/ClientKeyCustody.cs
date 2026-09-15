using System.Security.Cryptography;
using System.Text;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The browser's half of the account-key custody, reproduced in this project so that a test can seal an
/// envelope, send it, read the row back and open it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a second implementation of a client-side format, and being clear about what that buys is
/// half of what this type is for.</b> The server derives no key-encryption key and opens no envelope: it
/// takes opaque bytes, checks a width and a version byte, and stores them. So there is nothing here
/// this file could read a definition off, and nothing it can pin. A drift between
/// <c>+core/security/account-keys.ts</c> and the constants below would leave this file self-consistent
/// and still green — the frozen vectors in <c>account-keys.spec.ts</c> are what hold the derivation, and
/// they are on the only side that has one.
/// </para>
/// <para>
/// <b>What it does buy is the one claim about <c>wrapped_account_keys</c> that no row count can make:
/// that the envelope filed under a factor identifier is the envelope sealed under <em>that</em>
/// factor.</b> Associated data is not carried inside an envelope — it is rebuilt from where the envelope
/// was found — so a handler that shifted the card's eleven rows by one position satisfies every check
/// constraint, every foreign key and every count in the suite, and is discovered by somebody who typed a
/// recovery code in months later and found that it opened nothing. Sealing under the real associated
/// data and opening from what the database handed back is the only shape that catches it, and the
/// authenticated-encryption tag is what makes "opened" mean something rather than "the bytes matched".
/// </para>
/// <para>
/// The layout, the two HKDF labels and the associated-data grammar are transcribed from
/// <c>key-envelope.ts</c>, <c>recovery-codes.ts</c> and <c>account-keys.ts</c>. Each one is written out
/// rather than pointed at, because a C# project cannot read a TypeScript module and a paraphrase would
/// be a third spelling.
/// </para>
/// </remarks>
internal static class ClientKeyCustody
{
    /// <summary>The literal every wrapped key's associated data opens with.</summary>
    public const string WrappedKeyAssociatedDataPrefix = "budgetoid/wrapped-key/v1";

    /// <summary>The HKDF <c>info</c> of the branch that turns a recovery code into a wrapping key.</summary>
    /// <remarks>
    /// A sibling of the verifier branch below over the same code and the same salt. <c>info</c> is the
    /// only thing separating them, which is what keeps the value in a redemption request body from being
    /// the value that unwraps the account.
    /// </remarks>
    public const string RecoveryCodeKeyEncryptionKeyInfo =
        "budgetoid/recovery-code/key-encryption-key/v1";

    /// <summary>The HKDF <c>info</c> of the branch that produces the verifier a redemption presents.</summary>
    public const string RecoveryCodeVerifierInfo = "budgetoid/recovery-code/verifier/v1";

    /// <summary>
    /// Which value a factor's stored bytes are, as the associated data spells it.
    /// </summary>
    /// <remarks>
    /// <b>Two purposes over two different constructions, and they are not two halves of one pair any
    /// more.</b> <see cref="PrivateKeyPurpose" /> binds the AEAD envelope over the factor's private key,
    /// <em>wrapped under</em> the key-encryption key that factor derives.
    /// <see cref="AccountKeysPurpose" /> binds the encapsulation over both account keys as one
    /// plaintext, <em>encapsulated to</em> that factor's public half. Under the arrangement this
    /// replaced there were two AEAD envelopes, one per account key, and the purpose was what stopped a
    /// handler filing each in the other's column; the widths differ now, so the purpose is no longer
    /// carrying that job alone — but it still binds each value to the FACTOR, which is the shift no
    /// width can see.
    /// </remarks>
    public const string PrivateKeyPurpose = "private-key";

    public const string AccountKeysPurpose = "account-keys";

    /// <summary>
    /// The HKDF <c>info</c> of the branch that turns an ECDH shared secret into the key an encapsulated
    /// value is sealed under.
    /// </summary>
    public const string EncapsulationInfo = "budgetoid/account-keys/encapsulation/v1";

    /// <summary>The width of each account key, and of every branch derived off a code.</summary>
    public const int KeyBytes = 32;

    /// <summary>How many characters a minted code carries.</summary>
    public const int RecoveryCodeLength = 26;

    /// <summary>
    /// The alphabet a code is drawn from: Crockford's, minus <c>U</c>.
    /// </summary>
    /// <remarks>
    /// Thirty-two symbols, so one uniform byte masked to five bits produces one character with no bias.
    /// <c>I</c>, <c>L</c> and <c>O</c> are absent because a reader resolves them as <c>1</c>, <c>1</c>
    /// and <c>0</c> — which is what <see cref="Canonical" /> folds them to.
    /// </remarks>
    public const string RecoveryCodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The AEAD envelope's version byte, and the only one that format defines.</summary>
    private const byte EnvelopeVersion = 1;

    /// <summary>
    /// The encapsulated value's version byte, and the only one <em>that</em> format defines.
    /// </summary>
    /// <remarks>
    /// <b>A second constant holding the same number, never an alias of the first.</b> The two number
    /// different cryptography — one byte says "AES-256-GCM under a key both sides hold", the other says
    /// "ECDH to a public key, then AES-256-GCM" — and aliased, a bump to either suite would silently
    /// renumber the other. Nothing in a build can tell the two spellings apart, which is the argument
    /// <c>EncapsulatedValueEnvelope.Version</c> makes at length on the production side; this is the same
    /// decision on the client side of the wire.
    /// </remarks>
    private const byte EncapsulationVersion = 1;

    private const int NonceBytes = 12;

    private const int TagBytes = 16;

    private const int VersionBytes = 1;

    /// <summary>
    /// The width of an uncompressed SEC1 point on P-256: a <c>0x04</c> prefix and two 32-byte
    /// coordinates.
    /// </summary>
    private const int EphemeralPublicKeyBytes = 65;

    /// <summary>The width of one coordinate of a P-256 point.</summary>
    private const int CoordinateBytes = 32;

    /// <summary>
    /// ASCII's unit separator, spelled by its code point. A literal control character is invisible in
    /// every tool a reviewer would read this file in, which is the one property a byte of a frozen format
    /// cannot afford.
    /// </summary>
    private const char UnitSeparator = '\u001F';

    /// <summary>One code, drawn the way the client draws one.</summary>
    public static string MintRecoveryCode()
    {
        byte[] draw = RandomNumberGenerator.GetBytes(RecoveryCodeLength);
        StringBuilder code = new(RecoveryCodeLength);

        foreach (byte value in draw)
        {
            code.Append(RecoveryCodeAlphabet[value & (RecoveryCodeAlphabet.Length - 1)]);
        }

        return code.ToString();
    }

    /// <summary>
    /// The exact text every derivation off a code is built from.
    /// </summary>
    /// <remarks>
    /// The identity on everything <see cref="MintRecoveryCode" /> produces, and it is applied anyway: the
    /// two branches below have to fold to the same text or a code redeems and unwraps nothing.
    /// <c>ToUpperInvariant</c>, never a culture-aware fold, for the reason the client states — a Turkish
    /// locale maps <c>i</c> to <c>İ</c> and would derive two different verifiers from one typed code.
    /// </remarks>
    public static string Canonical(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        StringBuilder folded = new(code.Length);

        foreach (char character in code.ToUpperInvariant())
        {
            switch (character)
            {
                case 'I' or 'L':
                    folded.Append('1');
                    break;
                case 'O':
                    folded.Append('0');
                    break;
                case '-':
                    break;
                default:
                    if (!char.IsWhiteSpace(character))
                    {
                        folded.Append(character);
                    }

                    break;
            }
        }

        return folded.ToString();
    }

    /// <summary>The verifier branch: what a redemption presents, base64url as the wire carries it.</summary>
    public static string VerifierOf(string code) =>
        Base64UrlText.Encode(Derive(code, RecoveryCodeVerifierInfo));

    /// <summary>The key branch: the bytes a wrapping key is imported from.</summary>
    /// <remarks>
    /// Returned as bytes rather than as a key object, which is the one place this reproduction cannot
    /// follow the client: WebCrypto imports it non-extractable so that nothing holding the object can
    /// read the value, and there is no equivalent in a test that has to seal with it by hand.
    /// </remarks>
    public static byte[] KeyEncryptionKeyFromRecoveryCode(string code) =>
        Derive(code, RecoveryCodeKeyEncryptionKeyInfo);

    /// <summary>
    /// The associated data one wrapped copy is bound to:
    /// <c>"budgetoid/wrapped-key/v1" || 0x1F || factor id || 0x1F || purpose</c>, in UTF-8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The factor identifier is rendered <c>"D"</c> — lower-case, hyphenated — and that spelling is the
    /// binding.</b> A client that sealed under one spelling and sent another would have both envelopes
    /// stop opening permanently, with no error naming the cause; the server refuses every other spelling
    /// for exactly that reason.
    /// </para>
    /// <para>
    /// The purpose is in here so one factor's two envelopes are not interchangeable: without it, a
    /// handler that filed each in the other's column would still open both.
    /// </para>
    /// </remarks>
    public static byte[] AssociatedData(Guid factorId, string purpose) =>
        Encoding.UTF8.GetBytes(
            $"{WrappedKeyAssociatedDataPrefix}{UnitSeparator}{factorId:D}{UnitSeparator}{purpose}");

    /// <summary>
    /// Seals <paramref name="plaintext" /> under <paramref name="key" />, bound to
    /// <paramref name="associatedData" />: <c>version || nonce || ciphertext || tag</c>.
    /// </summary>
    public static byte[] Seal(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(associatedData);

        byte[] envelope = new byte[VersionBytes + NonceBytes + plaintext.Length + TagBytes];
        envelope[0] = EnvelopeVersion;

        Span<byte> nonce = envelope.AsSpan(VersionBytes, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using AesGcm cipher = new(key, TagBytes);
        cipher.Encrypt(
            nonce,
            plaintext,
            envelope.AsSpan(VersionBytes + NonceBytes, plaintext.Length),
            envelope.AsSpan(VersionBytes + NonceBytes + plaintext.Length, TagBytes),
            associatedData);

        return envelope;
    }

    /// <summary>
    /// Opens <paramref name="envelope" /> under <paramref name="key" /> and
    /// <paramref name="associatedData" />, or returns <see langword="false" />.
    /// </summary>
    /// <remarks>
    /// <b>A <see cref="bool" /> rather than a throw, because failing to open is the expected answer half
    /// the time this is called.</b> A copy that belongs to another factor, the two copies presented in
    /// each other's place and a single flipped bit are one refusal — GCM authenticating the associated
    /// data — and none of them yields bytes. A test asserting that a shifted envelope does <em>not</em>
    /// open reads better as a false than as a caught exception, and a caught exception would also swallow
    /// a genuinely malformed input.
    /// </remarks>
    public static bool TryOpen(
        byte[] key,
        byte[] envelope,
        byte[] associatedData,
        out byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(associatedData);

        plaintext = [];

        if (envelope.Length < VersionBytes + NonceBytes + TagBytes || envelope[0] != EnvelopeVersion)
        {
            return false;
        }

        int sealedLength = envelope.Length - VersionBytes - NonceBytes - TagBytes;
        byte[] opened = new byte[sealedLength];

        try
        {
            using AesGcm cipher = new(key, TagBytes);
            cipher.Decrypt(
                envelope.AsSpan(VersionBytes, NonceBytes),
                envelope.AsSpan(VersionBytes + NonceBytes, sealedLength),
                envelope.AsSpan(VersionBytes + NonceBytes + sealedLength, TagBytes),
                opened,
                associatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        plaintext = opened;

        return true;
    }

    /// <summary>A fresh ECDH P-256 key pair, the way a client mints one per recovery factor.</summary>
    /// <remarks>
    /// <b>This is the capability the reshape bought, and the reason this file grew a second format.</b>
    /// Wrapping the account's keys directly under a factor's key-encryption key means re-wrapping them
    /// needs that key, and for a passkey it exists only while the authenticator is being touched — so a
    /// rotation needed every registered authenticator present at once. Encapsulating to a factor's
    /// PUBLIC half needs only the public half, which the account's manifest carries.
    /// </remarks>
    public static ECDiffieHellman CreateFactorKeyPair() =>
        ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// The factor's private key, wrapped under <paramref name="keyEncryptionKey" /> and bound to
    /// <paramref name="factorId" />: a <see cref="Seal" /> over the PKCS#8 encoding.
    /// </summary>
    /// <remarks>
    /// PKCS#8 for P-256 is 138 bytes, so the envelope is 29 + 138 = 167 — which is what
    /// <c>WrappedAccountKeys.WrappedPrivateKeyLength</c> says and what the column's check constraint
    /// refuses anything else for. The width is not restated here: it falls out of the encoding, and a
    /// local copy would be a number able to disagree with the one the server enforces.
    /// </remarks>
    public static byte[] WrapPrivateKey(byte[] keyEncryptionKey, ECDiffieHellman keyPair, Guid factorId)
    {
        ArgumentNullException.ThrowIfNull(keyPair);

        return Seal(
            keyEncryptionKey,
            keyPair.ExportPkcs8PrivateKey(),
            AssociatedData(factorId, PrivateKeyPurpose));
    }

    /// <summary>
    /// The account's content key and index key as one 64-byte plaintext, <b>content key first</b>,
    /// encapsulated to <paramref name="recipient" /> and bound to <paramref name="factorId" />:
    /// <c>version || ephemeral public key || nonce || ciphertext || tag</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order of the two keys is a contract between clients and this server can never check
    /// it.</b> Both are 32 bytes, so a pair the other way round produces a value of exactly the right
    /// width carrying exactly the right version, which stores, reads back and opens — and yields an
    /// index key used to seal narrative text and a content key used to compute blind indexes. Nothing
    /// on the server side has a symptom.
    /// </para>
    /// <para>
    /// The ephemeral key pair is minted per call and discarded, which is what "ephemeral" means and is
    /// the reason two encapsulations of one plaintext to one recipient are different bytes.
    /// </para>
    /// </remarks>
    public static byte[] EncapsulateAccountKeys(
        ECDiffieHellmanPublicKey recipient,
        byte[] contentKey,
        byte[] indexKey,
        Guid factorId)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(contentKey);
        ArgumentNullException.ThrowIfNull(indexKey);

        using ECDiffieHellman ephemeral = CreateFactorKeyPair();
        byte[] key = EncapsulationKey(ephemeral.DeriveRawSecretAgreement(recipient));
        byte[] plaintext = [.. contentKey, .. indexKey];
        byte[] point = UncompressedPoint(ephemeral);

        byte[] value =
            new byte[VersionBytes + EphemeralPublicKeyBytes + NonceBytes + plaintext.Length + TagBytes];
        value[0] = EncapsulationVersion;
        point.CopyTo(value.AsSpan(VersionBytes));

        Span<byte> nonce = value.AsSpan(VersionBytes + EphemeralPublicKeyBytes, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        int ciphertextOffset = VersionBytes + EphemeralPublicKeyBytes + NonceBytes;
        using AesGcm cipher = new(key, TagBytes);
        cipher.Encrypt(
            nonce,
            plaintext,
            value.AsSpan(ciphertextOffset, plaintext.Length),
            value.AsSpan(ciphertextOffset + plaintext.Length, TagBytes),
            AssociatedData(factorId, AccountKeysPurpose));

        return value;
    }

    /// <summary>
    /// Opens a factor's stored pair the way a client does: unwrap the private key under
    /// <paramref name="keyEncryptionKey" />, then decapsulate with the private half it produced.
    /// </summary>
    /// <remarks>
    /// <b>Two steps and not one, and the order is the design.</b> The first needs a key derived from a
    /// recovery factor the person is holding; the second needs only the output of the first. A value
    /// that fails at either step yields nothing, which is what lets a caller assert that another
    /// factor's key opens nothing here without having to say which step refused it.
    /// </remarks>
    public static bool TryOpenAccountKeys(
        byte[] keyEncryptionKey,
        byte[] wrappedPrivateKey,
        byte[] encapsulatedAccountKeys,
        Guid factorId,
        out byte[] contentKey,
        out byte[] indexKey)
    {
        ArgumentNullException.ThrowIfNull(encapsulatedAccountKeys);

        contentKey = [];
        indexKey = [];

        if (!TryOpen(
                keyEncryptionKey,
                wrappedPrivateKey,
                AssociatedData(factorId, PrivateKeyPurpose),
                out byte[] pkcs8))
        {
            return false;
        }

        int floor = VersionBytes + EphemeralPublicKeyBytes + NonceBytes + TagBytes;

        if (encapsulatedAccountKeys.Length < floor
            || encapsulatedAccountKeys[0] != EncapsulationVersion)
        {
            return false;
        }

        using ECDiffieHellman recipient = CreateFactorKeyPair();
        recipient.ImportPkcs8PrivateKey(pkcs8, out _);

        using ECDiffieHellman ephemeral = PublicKeyFrom(
            encapsulatedAccountKeys.AsSpan(VersionBytes, EphemeralPublicKeyBytes));
        byte[] key = EncapsulationKey(recipient.DeriveRawSecretAgreement(ephemeral.PublicKey));

        int ciphertextOffset = VersionBytes + EphemeralPublicKeyBytes + NonceBytes;
        int ciphertextLength = encapsulatedAccountKeys.Length - floor;
        byte[] opened = new byte[ciphertextLength];

        try
        {
            using AesGcm cipher = new(key, TagBytes);
            cipher.Decrypt(
                encapsulatedAccountKeys.AsSpan(VersionBytes + EphemeralPublicKeyBytes, NonceBytes),
                encapsulatedAccountKeys.AsSpan(ciphertextOffset, ciphertextLength),
                encapsulatedAccountKeys.AsSpan(ciphertextOffset + ciphertextLength, TagBytes),
                opened,
                AssociatedData(factorId, AccountKeysPurpose));
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        if (opened.Length != KeyBytes * 2)
        {
            return false;
        }

        // Content key FIRST. The split is the whole of what this method knows about the plaintext's
        // shape, and getting it backwards is the one mistake nothing on either side of the wire can see.
        contentKey = opened[..KeyBytes];
        indexKey = opened[KeyBytes..];

        return true;
    }

    /// <summary>
    /// <c>HKDF-SHA-256(raw shared secret, salt = ∅, EncapsulationInfo)</c>, at
    /// <see cref="KeyBytes" /> bytes.
    /// </summary>
    /// <remarks>
    /// The raw agreement rather than .NET's hashed derivations, because the format names the KDF itself:
    /// a client running HKDF over something already hashed would agree with nothing.
    /// </remarks>
    private static byte[] EncapsulationKey(byte[] sharedSecret) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            KeyBytes,
            salt: [],
            Encoding.UTF8.GetBytes(EncapsulationInfo));

    /// <summary>The uncompressed SEC1 encoding of <paramref name="keyPair" />'s public half.</summary>
    private static byte[] UncompressedPoint(ECDiffieHellman keyPair)
    {
        ECPoint point = keyPair.ExportParameters(includePrivateParameters: false).Q;

        return [0x04, .. point.X!, .. point.Y!];
    }

    /// <summary>A key object carrying only the public half encoded in <paramref name="point" />.</summary>
    private static ECDiffieHellman PublicKeyFrom(ReadOnlySpan<byte> point) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = point.Slice(1, CoordinateBytes).ToArray(),
                Y = point.Slice(1 + CoordinateBytes, CoordinateBytes).ToArray(),
            },
        });

    /// <summary>
    /// <c>HKDF-SHA-256(utf8(canonical code), salt = ∅, info)</c>, at <see cref="KeyBytes" /> bytes.
    /// </summary>
    /// <remarks>
    /// The salt is empty by requirement rather than by simplification: a redemption arrives carrying a
    /// verifier and no identity at all, so there is no per-account value a salt could be taken from
    /// before the lookup the salt would be needed for.
    /// </remarks>
    private static byte[] Derive(string code, string info) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(Canonical(code)),
            KeyBytes,
            salt: [],
            Encoding.UTF8.GetBytes(info));
}
