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
/// takes 61 opaque bytes, checks a width and a version byte, and stores them. So there is nothing here
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

    /// <summary>Which of the account's two keys a wrapped copy holds, as the associated data spells it.</summary>
    public const string ContentPurpose = "content";

    public const string IndexPurpose = "index";

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

    /// <summary>The envelope's version byte, and the only one this format defines.</summary>
    private const byte EnvelopeVersion = 1;

    private const int NonceBytes = 12;

    private const int TagBytes = 16;

    private const int VersionBytes = 1;

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
