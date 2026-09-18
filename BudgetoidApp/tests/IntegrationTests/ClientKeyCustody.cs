using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
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
/// takes opaque bytes, checks a width, a leading version byte and a base64url alphabet, and stores them.
/// So there is nothing here this file could read a definition off, and nothing the <em>server</em> can
/// pin.
/// </para>
/// <para>
/// <b>What used to be written here is that a drift from <c>+core/security/factor-keypair.ts</c> would
/// leave this file self-consistent and still green. That is now less true, and the reason is
/// <c>ClientKeyCustodyTests</c>.</b> Every message this type composes and both values it produces are
/// pinned against <c>docs/business-logic/vectors/factor-keypair-v1.json</c> — known answers computed
/// outside this repository by a third implementation, which the browser already reproduces. So the
/// grammar below is held by frozen bytes on two independent sides, in two languages, neither of which
/// authored them. What is still unheld is everything <em>outside</em> those vectors: they say nothing
/// about the encapsulated plaintext's half order, nothing about which factor a handler filed a row
/// under, and nothing about a scheme both implementations could agree on and both have wrong. A vector
/// pins bytes; it does not pin judgement.
/// </para>
/// <para>
/// <b>What sealing here buys beyond the vectors is the one claim about <c>wrapped_account_keys</c> that
/// no row count can make: that the envelope filed under a factor identifier is the envelope sealed under
/// <em>that</em> factor.</b> Associated data is not carried inside an envelope — it is rebuilt from where
/// the envelope was found — so a handler that shifted the card's eleven rows by one position satisfies
/// every check constraint, every foreign key and every count in the suite, and is discovered by somebody
/// who typed a recovery code in months later and found that it opened nothing. Sealing under the real
/// associated data and opening from what the database handed back is the only shape that catches it, and
/// the authenticated-encryption tag is what makes "opened" mean something rather than "the bytes
/// matched".
/// </para>
/// <para>
/// The layout, the labels and the associated-data grammar are transcribed from <c>key-envelope.ts</c>,
/// <c>recovery-codes.ts</c>, <c>factor-keypair.ts</c> and <c>factor-manifest.ts</c>. Each one is written
/// out rather than pointed at, because a C# project cannot read a TypeScript module and a paraphrase
/// would be a third spelling.
/// </para>
/// <para>
/// <b>The three version bytes below are each written out as a number, and none may ever be spelled as
/// another's name.</b> <c>EncapsulationVersion = EnvelopeVersion</c> compiles, folds to the same byte,
/// and reddens nothing — it is the same defect <c>WrappedAccountKeys</c> spends four paragraphs on, on
/// the client side of the wire. The two number different cryptography, and
/// <see cref="FactorKeypairGrammarVersion" /> numbers neither, so any aliasing between them renumbers a
/// suite nobody meant to touch. It is held rather than merely asserted:
/// <c>EnvelopeSuiteCensusTests.EveryClientReproductionVersionByte_IsWrittenOutRatherThanAliased</c>
/// reads this file's source text — the only place the difference between a literal and an alias
/// survives, because the compiler folds a <c>const</c> initialiser before any metadata exists.
/// </para>
/// <para>
/// <b>The rule this replaced was about a spelling that cannot occur, and saying so is the point.</b> It
/// read "no constant here may be named plain <c>Version</c>, because <c>EnvelopeSuiteCensusTests</c>
/// discovers a suite by that member name". Measured: that census filters on
/// <c>IsPublic &amp;&amp; IsAbstract &amp;&amp; IsSealed</c>, in namespace <c>Domain.Security</c> or
/// below, in <c>typeof(CiphertextEnvelope).Assembly</c>. This class is <c>internal</c>, in
/// <c>IntegrationTests</c>, in an assembly <c>UnitTests.csproj</c> does not reference — three
/// independent reasons the hazard cannot reach it. A rule guarding an impossible mistake reads as
/// coverage and leaves the reachable one unguarded.
/// </para>
/// </remarks>
internal static class ClientKeyCustody
{
    /// <summary>The literal every message of the factor-keypair grammar opens with.</summary>
    /// <remarks>
    /// One label over four messages — three associated-data messages and the HKDF <c>info</c> — where
    /// the arrangement this replaced had a prefix for the wrapped key and a separate bare constant for
    /// the encapsulation's <c>info</c>. What separates the four is their fields, not their labels, which
    /// is exactly why <see cref="WrappedPrivateKeyAssociatedData" /> and
    /// <see cref="EncapsulatedAccountKeysAssociatedData" /> below are two methods and never one
    /// parameterised by purpose.
    /// </remarks>
    public const string FactorKeypairLabel = "budgetoid/factor-keypair/v1";

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
    /// The field that says a wrapped private key is a wrapped private key, and the only field separating
    /// its associated data from the encapsulation's.
    /// </summary>
    /// <remarks>
    /// <b>It is the whole of the difference between two messages of one grammar, and the shorter is a
    /// strict byte prefix of the longer.</b> Sixty-six bytes against seventy-eight for a canonical
    /// identifier. That is why there is no shared builder taking a purpose: a builder handed nothing for
    /// its purpose field does not produce garbage, it produces the <em>other valid message of this same
    /// scheme</em> — and a value sealed under it opens, under the wrong binding, for ever.
    /// </remarks>
    public const string PrivateKeyPurpose = "private-key";

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

    /// <summary>
    /// The width of an uncompressed SEC1 point on P-256: a <c>0x04</c> prefix and two 32-byte
    /// coordinates.
    /// </summary>
    /// <remarks>
    /// Public because a manifest entry carries one of these beside its identifier, so a caller
    /// assembling <see cref="FactorPublicKey" /> values has a width to name.
    /// </remarks>
    public const int PublicKeyBytes = 65;

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

    /// <summary>
    /// The version byte carried <em>inside</em> every message of the factor-keypair grammar — all three
    /// associated-data messages and the HKDF <c>info</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A third one, and it versions neither suite.</b> It cannot be
    /// <see cref="EnvelopeVersion" /> and it cannot be <see cref="EncapsulationVersion" />: the manifest
    /// rides the AEAD suite and the encapsulated value rides the other, and this byte appears in the
    /// associated data of <em>both</em>. So it is a version of the grammar — of which fields go in what
    /// order — and a reader who aliases it to either suite renumbers a suite nobody meant to touch, in a
    /// build where all three spellings emit the same byte.
    /// </para>
    /// <para>
    /// <b>Spelled in hex, and never as a character.</b> <c>'\u0001'</c>, <c>(char)1</c> and a bare
    /// <c>1</c> pushed into an interpolated string are each one byte at this version and <em>two</em>
    /// from <c>0x80</c> up, where UTF-8 widens a code point with no error anywhere. That is the whole
    /// reason the messages below are composed from bytes rather than from text, and why the vector file
    /// freezes a message at a version this scheme does not have.
    /// </para>
    /// </remarks>
    private const byte FactorKeypairGrammarVersion = 0x01;

    private const int NonceBytes = 12;

    private const int TagBytes = 16;

    private const int VersionBytes = 1;

    /// <summary>The width of one coordinate of a P-256 point.</summary>
    private const int CoordinateBytes = 32;

    /// <summary>
    /// ASCII's unit separator, spelled by its code point. A literal control character is invisible in
    /// every tool a reviewer would read this file in, which is the one property a byte of a frozen format
    /// cannot afford.
    /// </summary>
    private const char UnitSeparator = '\u001F';

    /// <summary>
    /// The same decision as <see cref="UnitSeparator" />, read back as the byte the join writes rather
    /// than spelled a second time.
    /// </summary>
    /// <remarks>
    /// Derived rather than declared, the rule the client keeps for the same pair: two literals of one
    /// value can drift, and a drift in this byte makes every envelope already written unopenable with the
    /// same failure a corrupted key gives.
    /// </remarks>
    private const byte UnitSeparatorByte = (byte)UnitSeparator;

    /// <summary>One factor of an account's set, as the manifest names it.</summary>
    /// <param name="FactorId">Rendered <c>"D"</c> — lower-case, hyphenated — where it is written.</param>
    /// <param name="PublicKey">
    /// Raw and uncompressed: <see cref="PublicKeyBytes" /> bytes leading <c>0x04</c>.
    /// </param>
    public sealed record FactorPublicKey(Guid FactorId, byte[] PublicKey);

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
    /// The exact text every derivation off a code is built from:
    /// upper case, then strip whitespace and hyphens, then fold <c>I</c> and <c>L</c> to <c>1</c> and
    /// <c>O</c> to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identity on everything <see cref="MintRecoveryCode" /> produces, and it is applied anyway: the
    /// two branches below have to fold to the same text or a code redeems and unwraps nothing. Never a
    /// culture-aware fold, for the reason the client states — a Turkish locale maps <c>i</c> to <c>İ</c>
    /// and would derive two different verifiers from one typed code.
    /// </para>
    /// <para>
    /// <b>The order is the rule and upper case runs first.</b> Fold the digits before upper casing and a
    /// lower-case <c>i</c> survives as <c>i</c> rather than becoming <c>1</c>, so every code typed back
    /// the way a person actually types one would derive a different verifier from the code that was
    /// minted.
    /// </para>
    /// <para>
    /// <b>Two of the three steps are defined by the browser's platform rather than by this product, and
    /// that is what makes this method hard rather than obvious.</b> The authoritative implementation is
    /// <c>+core/security/recovery-code-canonical.ts</c>, which runs in a browser and derives real keys;
    /// this is a reproduction. Its <c>toUpperCase()</c> is Unicode <em>full</em> uppercase and its
    /// <c>\s</c> is ECMAScript's whitespace class, and <b>.NET's nearest local equivalents are wrong on
    /// both</b> — measurably, on four code points. So neither step may be written as a call to whatever
    /// this platform happens to call the same thing; both are transcribed below. This is exactly the
    /// class of defect CON-009 — every client implements the identical cryptographic contract — is about:
    /// a fold that disagrees by one code point makes a code that redeems fine open nothing, or the
    /// reverse, silently, long after the typing that caused it.
    /// </para>
    /// <para>
    /// Both steps are pinned against <c>docs/business-logic/vectors/recovery-code-v1.json</c> by
    /// <c>RecoveryCodeCanonicalFormTests</c> — known answers computed outside both codebases, which the
    /// browser already reproduces. See <see cref="JavaScriptWhitespace" /> and
    /// <see cref="FullUpperCaseExpansions" /> for what each transcription is and why.
    /// </para>
    /// <para>
    /// <b>One thing this reproduction does not answer for: an unpaired surrogate.</b> Iterating runes
    /// resolves one to <c>U+FFFD</c>, where <c>toUpperCase()</c> would pass it through untouched. No
    /// vector names that case, nothing here holds it, and no path in this suite can produce one — a code
    /// is minted from <see cref="RecoveryCodeAlphabet" /> and typed back from a keyboard.
    /// </para>
    /// </remarks>
    public static string Canonical(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        StringBuilder folded = new(code.Length);

        // Two UTF-16 code units is the whole of what one simply-upper-cased scalar can occupy; the
        // expansions that need more are the table's, and they are already strings.
        Span<char> upperCased = stackalloc char[2];

        foreach (Rune rune in code.AsSpan().EnumerateRunes())
        {
            if (rune.IsBmp
                && FullUpperCaseExpansions.TryGetValue((char)rune.Value, out string? expansion))
            {
                FoldInto(folded, expansion);
                continue;
            }

            int written = Rune.ToUpperInvariant(rune).EncodeToUtf16(upperCased);
            FoldInto(folded, upperCased[..written]);
        }

        return folded.ToString();
    }

    /// <summary>
    /// The second and third steps of the fold, over text the first step has already upper-cased.
    /// </summary>
    /// <remarks>
    /// The client spells this as three chained <c>replace</c> calls — strip, then <c>I</c>/<c>L</c>, then
    /// <c>O</c> — and one pass reaches the same answer because the three act on disjoint characters:
    /// upper casing produces no hyphen and no whitespace, and folding <c>I</c> and <c>L</c> to <c>1</c>
    /// produces no <c>O</c>. <c>U</c> is excluded from the draw and deliberately <em>not</em> folded: it
    /// is excluded so a draw cannot spell an obscenity, not because a reader resolves it as something
    /// else.
    /// </remarks>
    private static void FoldInto(StringBuilder folded, ReadOnlySpan<char> upperCased)
    {
        foreach (char character in upperCased)
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
                    if (!JavaScriptWhitespace.Contains(character))
                    {
                        folded.Append(character);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// ECMAScript's <c>\s</c>, transcribed code point by code point: <c>WhiteSpace</c> ∪
    /// <c>LineTerminator</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written out rather than expressed as <c>char.IsWhiteSpace</c>, which is a different set.</b> The
    /// authoritative fold strips whatever the browser's <c>/[\s-]/</c> matches, so this set is part of the
    /// cryptographic contract and not a convenience. The two platforms disagree in both directions, which
    /// is why no local equivalent can stand in for the transcription:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>U+FEFF</c> — a byte order mark, which is what a paste out of a document carries — is in
    /// ECMAScript's <c>WhiteSpace</c> and is stripped. <c>char.IsWhiteSpace</c> answers <c>false</c>, so a
    /// fold written with it would carry the mark into HKDF and derive a verifier the browser never will.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>U+0085</c> NEXT LINE is <em>not</em> in ECMAScript's class and survives the fold.
    /// <c>char.IsWhiteSpace</c> answers <c>true</c>, so the same fold would strip it and derive a
    /// verifier for a shorter code than the browser folded. It is the byte order mark's mirror image, and
    /// the pair is the reason neither answer may be left to a local test.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// A <see cref="SearchValues{T}" /> rather than a string scan or a <c>switch</c>: membership is the
    /// only question asked of it, it is built once, and the set reads as one list a reader can diff
    /// against the specification.
    /// </para>
    /// </remarks>
    private static readonly SearchValues<char> JavaScriptWhitespace = SearchValues.Create(
        // WhiteSpace: TAB, VT, FF, SP, NBSP, ZWNBSP and every Space_Separator.
        "\u0009\u000B\u000C\u0020\u00A0\u1680"
        + "\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A"
        + "\u202F\u205F\u3000\uFEFF"
        // LineTerminator: LF, CR, LINE SEPARATOR, PARAGRAPH SEPARATOR. The first two are spelled
        // \n and \r because C# rejects \u000A and \u000D inside a string literal outright.
        + "\n\r\u2028\u2029");

    /// <summary>
    /// Every code point whose Unicode <em>full</em> uppercase differs from the single-character mapping
    /// <see cref="Rune.ToUpperInvariant" /> applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fold is not length preserving, and a per-character implementation makes the opposite
    /// assumption without noticing it has made one.</b> The browser's <c>toUpperCase()</c> is Unicode
    /// full uppercase: <c>ß</c> becomes <c>SS</c>, two characters out of one. .NET's BCL has no
    /// full-casing API at all — <c>ToUpperInvariant</c> and <c>TextInfo.ToUpper</c> are both simple,
    /// one scalar in and one scalar out — so the expansions have to be carried as data.
    /// </para>
    /// <para>
    /// <c>U+0131</c> is here for the other reason: its mapping to <c>I</c> is a single character, and
    /// .NET's invariant casing declines to make it anyway, to keep the dotless <c>i</c> away from the
    /// Turkish-<c>I</c> problem. The browser makes it, and then the <c>I</c> folds to <c>1</c> — so the
    /// same typed character reaches two different canonical forms on the two platforms unless this row
    /// exists.
    /// </para>
    /// <para>
    /// <b>Provenance, and what is actually held.</b> The rows are Unicode's unconditional full-uppercase
    /// special cases; they were enumerated by running both platforms over every scalar value and keeping
    /// the disagreements, so the set is complete by construction rather than by transcription from a
    /// list. Only two of them — <c>U+00DF</c> and <c>U+0131</c> — are pinned by a frozen vector; the rest
    /// are correct data that no test in this repository holds, and a reader should treat them as such.
    /// Nothing below is reachable from <see cref="RecoveryCodeAlphabet" />, so no minted code can move if
    /// one of them is wrong.
    /// </para>
    /// <para>
    /// <b>What is deliberately absent:</b> the handful of code points where the two platforms' Unicode
    /// versions simply differ (<c>U+A7CF</c>, <c>U+A7D3</c>, <c>U+A7D5</c> and the <c>U+16EBB</c> block).
    /// Those are not a rule disagreement and freezing either side's answer would pin a version, not a
    /// contract.
    /// </para>
    /// </remarks>
    private static readonly FrozenDictionary<char, string> FullUpperCaseExpansions =
        new (char From, string To)[]
        {
            ('\u00DF', "\u0053\u0053"), ('\u0131', "\u0049"), ('\u0149', "\u02BC\u004E"),
            ('\u01F0', "\u004A\u030C"), ('\u0390', "\u0399\u0308\u0301"), ('\u03B0', "\u03A5\u0308\u0301"),
            ('\u0587', "\u0535\u0552"), ('\u1E96', "\u0048\u0331"), ('\u1E97', "\u0054\u0308"),
            ('\u1E98', "\u0057\u030A"), ('\u1E99', "\u0059\u030A"), ('\u1E9A', "\u0041\u02BE"),
            ('\u1F50', "\u03A5\u0313"), ('\u1F52', "\u03A5\u0313\u0300"), ('\u1F54', "\u03A5\u0313\u0301"),
            ('\u1F56', "\u03A5\u0313\u0342"), ('\u1F80', "\u1F08\u0399"), ('\u1F81', "\u1F09\u0399"),
            ('\u1F82', "\u1F0A\u0399"), ('\u1F83', "\u1F0B\u0399"), ('\u1F84', "\u1F0C\u0399"),
            ('\u1F85', "\u1F0D\u0399"), ('\u1F86', "\u1F0E\u0399"), ('\u1F87', "\u1F0F\u0399"),
            ('\u1F88', "\u1F08\u0399"), ('\u1F89', "\u1F09\u0399"), ('\u1F8A', "\u1F0A\u0399"),
            ('\u1F8B', "\u1F0B\u0399"), ('\u1F8C', "\u1F0C\u0399"), ('\u1F8D', "\u1F0D\u0399"),
            ('\u1F8E', "\u1F0E\u0399"), ('\u1F8F', "\u1F0F\u0399"), ('\u1F90', "\u1F28\u0399"),
            ('\u1F91', "\u1F29\u0399"), ('\u1F92', "\u1F2A\u0399"), ('\u1F93', "\u1F2B\u0399"),
            ('\u1F94', "\u1F2C\u0399"), ('\u1F95', "\u1F2D\u0399"), ('\u1F96', "\u1F2E\u0399"),
            ('\u1F97', "\u1F2F\u0399"), ('\u1F98', "\u1F28\u0399"), ('\u1F99', "\u1F29\u0399"),
            ('\u1F9A', "\u1F2A\u0399"), ('\u1F9B', "\u1F2B\u0399"), ('\u1F9C', "\u1F2C\u0399"),
            ('\u1F9D', "\u1F2D\u0399"), ('\u1F9E', "\u1F2E\u0399"), ('\u1F9F', "\u1F2F\u0399"),
            ('\u1FA0', "\u1F68\u0399"), ('\u1FA1', "\u1F69\u0399"), ('\u1FA2', "\u1F6A\u0399"),
            ('\u1FA3', "\u1F6B\u0399"), ('\u1FA4', "\u1F6C\u0399"), ('\u1FA5', "\u1F6D\u0399"),
            ('\u1FA6', "\u1F6E\u0399"), ('\u1FA7', "\u1F6F\u0399"), ('\u1FA8', "\u1F68\u0399"),
            ('\u1FA9', "\u1F69\u0399"), ('\u1FAA', "\u1F6A\u0399"), ('\u1FAB', "\u1F6B\u0399"),
            ('\u1FAC', "\u1F6C\u0399"), ('\u1FAD', "\u1F6D\u0399"), ('\u1FAE', "\u1F6E\u0399"),
            ('\u1FAF', "\u1F6F\u0399"), ('\u1FB2', "\u1FBA\u0399"), ('\u1FB3', "\u0391\u0399"),
            ('\u1FB4', "\u0386\u0399"), ('\u1FB6', "\u0391\u0342"), ('\u1FB7', "\u0391\u0342\u0399"),
            ('\u1FBC', "\u0391\u0399"), ('\u1FC2', "\u1FCA\u0399"), ('\u1FC3', "\u0397\u0399"),
            ('\u1FC4', "\u0389\u0399"), ('\u1FC6', "\u0397\u0342"), ('\u1FC7', "\u0397\u0342\u0399"),
            ('\u1FCC', "\u0397\u0399"), ('\u1FD2', "\u0399\u0308\u0300"), ('\u1FD3', "\u0399\u0308\u0301"),
            ('\u1FD6', "\u0399\u0342"), ('\u1FD7', "\u0399\u0308\u0342"), ('\u1FE2', "\u03A5\u0308\u0300"),
            ('\u1FE3', "\u03A5\u0308\u0301"), ('\u1FE4', "\u03A1\u0313"), ('\u1FE6', "\u03A5\u0342"),
            ('\u1FE7', "\u03A5\u0308\u0342"), ('\u1FF2', "\u1FFA\u0399"), ('\u1FF3', "\u03A9\u0399"),
            ('\u1FF4', "\u038F\u0399"), ('\u1FF6', "\u03A9\u0342"), ('\u1FF7', "\u03A9\u0342\u0399"),
            ('\u1FFC', "\u03A9\u0399"), ('\uFB00', "\u0046\u0046"), ('\uFB01', "\u0046\u0049"),
            ('\uFB02', "\u0046\u004C"), ('\uFB03', "\u0046\u0046\u0049"), ('\uFB04', "\u0046\u0046\u004C"),
            ('\uFB05', "\u0053\u0054"), ('\uFB06', "\u0053\u0054"), ('\uFB13', "\u0544\u0546"),
            ('\uFB14', "\u0544\u0535"), ('\uFB15', "\u0544\u053B"), ('\uFB16', "\u054E\u0546"),
            ('\uFB17', "\u0544\u053D"),
        }.ToFrozenDictionary(row => row.From, row => row.To);

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
    /// The associated data a factor's wrapped private key is bound to:
    /// <c>label ‖ 0x1F ‖ version ‖ 0x1F ‖ factor id ‖ 0x1F ‖ "private-key"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The factor identifier is rendered <c>"D"</c> — lower-case, hyphenated — and that spelling is the
    /// binding.</b> A client that sealed under one spelling and sent another would have both values stop
    /// opening permanently, with no error naming the cause; the server refuses every other spelling for
    /// exactly that reason.
    /// </para>
    /// <para>
    /// <b>One of three builders, never one builder taking a purpose.</b> See
    /// <see cref="PrivateKeyPurpose" />: drop this method's last field and what comes out is
    /// <see cref="EncapsulatedAccountKeysAssociatedData" />'s message exactly, to the byte.
    /// </para>
    /// <para>
    /// <b><paramref name="version" /> defaults to the grammar's own and exists for one caller.</b> At
    /// <c>0x01</c> a text composition of this same field list is byte-identical, so nothing can tell a
    /// byte-composed grammar from an interpolated one <em>at the version this scheme has</em>. The
    /// parameter is what lets <c>ClientKeyCustodyTests</c> drive this builder at the frozen <c>0x80</c>
    /// vector, where UTF-8 widens the version field into two bytes — so the guard sits on the builder a
    /// reader would actually rewrite, rather than on the join beneath it, which no production path
    /// calls. No caller in this file passes it, and none should.
    /// </para>
    /// </remarks>
    public static byte[] WrappedPrivateKeyAssociatedData(
        Guid factorId,
        byte version = FactorKeypairGrammarVersion) =>
        JoinFields(
            Utf8(FactorKeypairLabel),
            GrammarVersion(version),
            Utf8(CanonicalFactorId(factorId)),
            Utf8(PrivateKeyPurpose));

    /// <summary>
    /// The associated data a factor's encapsulated account keys are bound to:
    /// <c>label ‖ 0x1F ‖ version ‖ 0x1F ‖ factor id</c>.
    /// </summary>
    /// <remarks>
    /// A strict byte prefix of <see cref="WrappedPrivateKeyAssociatedData" />, which is why the two are
    /// pinned separately in <c>ClientKeyCustodyTests</c> rather than one derived from the other.
    /// <paramref name="version" /> is that method's parameter and carries its argument.
    /// </remarks>
    public static byte[] EncapsulatedAccountKeysAssociatedData(
        Guid factorId,
        byte version = FactorKeypairGrammarVersion) =>
        JoinFields(Utf8(FactorKeypairLabel), GrammarVersion(version), Utf8(CanonicalFactorId(factorId)));

    /// <summary>
    /// The associated data an account's factor manifest is bound to:
    /// <c>label ‖ 0x1F ‖ version ‖ 0x1F ‖ rotation epoch, decimal digits</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The epoch is decimal text, not a raw integer</b>, which is invisible at epoch 1 and visible at
    /// epoch 10 — the reason the vector file freezes both. <see cref="CultureInfo.InvariantCulture" />
    /// rather than the ambient one, because a culture with its own digits would render a number no other
    /// implementation will ever rebuild.
    /// </para>
    /// <para>
    /// <b>The epoch is authenticated here and nowhere else.</b> The server refuses anything that is not
    /// the stored epoch plus one, but it cannot read what a client sealed — so a manifest naming the
    /// right set at the wrong epoch stores, and comes back at a rotation that has since happened.
    /// Binding it is what turns that into a tag failure.
    /// </para>
    /// <para>
    /// <paramref name="version" /> is <see cref="WrappedPrivateKeyAssociatedData" />'s parameter and
    /// carries its argument.
    /// </para>
    /// </remarks>
    public static byte[] ManifestAssociatedData(
        int rotationEpoch,
        byte version = FactorKeypairGrammarVersion) =>
        JoinFields(
            Utf8(FactorKeypairLabel),
            GrammarVersion(version),
            Utf8(rotationEpoch.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The HKDF <c>info</c> a factor's encapsulation key is derived under:
    /// <c>label ‖ 0x1F ‖ version ‖ 0x1F ‖ factor id ‖ 0x1F ‖ ephemeral point ‖ 0x1F ‖ factor point</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both points raw and never re-encoded, ephemeral first.</b> Swap the two and two matched
    /// implementations still agree with each other and with nothing else — which is a class of defect no
    /// round trip can reach, and the reason the 198-byte message is frozen.
    /// </para>
    /// <para>
    /// Binding both points into the <c>info</c> is what makes a substituted factor public key derive a
    /// different key. No vector shows that: they pin that the bytes are present, never that they help.
    /// </para>
    /// <para>
    /// <b>The fourth message of the grammar, and the one that takes no version parameter.</b> The other
    /// three carry one so a test can drive them at the frozen <c>0x80</c> message; the vector file
    /// freezes no encapsulation <c>info</c> at a version this scheme does not have, so a parameter here
    /// would be a widening with no caller. What holds this message instead is its own 198-byte frozen
    /// value, where the two raw points make a text composition 129 bytes wider each rather than one.
    /// </para>
    /// </remarks>
    public static byte[] EncapsulationInfo(
        Guid factorId,
        byte[] ephemeralPublicKey,
        byte[] factorPublicKey) =>
        JoinFields(
            Utf8(FactorKeypairLabel),
            GrammarVersion(FactorKeypairGrammarVersion),
            Utf8(CanonicalFactorId(factorId)),
            ephemeralPublicKey,
            factorPublicKey);

    /// <summary>
    /// <c>HKDF-SHA-256(raw shared secret, salt = ∅, <paramref name="info" />)</c>, at
    /// <see cref="KeyBytes" /> bytes.
    /// </summary>
    /// <remarks>
    /// The raw agreement rather than .NET's hashed derivations, because the format names the KDF itself:
    /// a client running HKDF over something already hashed would agree with nothing.
    /// </remarks>
    public static byte[] EncapsulationKey(byte[] sharedSecret, byte[] info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, KeyBytes, salt: [], info);

    /// <summary>
    /// Seals <paramref name="plaintext" /> under <paramref name="key" />, bound to
    /// <paramref name="associatedData" />: <c>version ‖ nonce ‖ ciphertext ‖ tag</c>.
    /// </summary>
    public static byte[] Seal(byte[] key, byte[] plaintext, byte[] associatedData) =>
        SealUnderNonce(key, RandomNumberGenerator.GetBytes(NonceBytes), plaintext, associatedData);

    /// <summary>
    /// The same seal over a <paramref name="nonce" /> the caller chose.
    /// </summary>
    /// <remarks>
    /// <b>For known-answer tests and nothing else.</b> A drawn nonce is what makes two seals of one
    /// plaintext different bytes, and a caller that reused one across two messages under one key
    /// surrenders both plaintexts and the authentication subkey with them. It is exposed because a frozen
    /// vector is a fixed nonce by construction: without it the pin could only re-open what it wrote,
    /// which is the self-certification the vectors exist to break.
    /// </remarks>
    public static byte[] SealUnderNonce(
        byte[] key,
        byte[] nonce,
        byte[] plaintext,
        byte[] associatedData)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(associatedData);

        byte[] envelope = new byte[VersionBytes + NonceBytes + plaintext.Length + TagBytes];
        envelope[0] = EnvelopeVersion;
        nonce.CopyTo(envelope, VersionBytes);

        using AesGcm cipher = new(key, TagBytes);
        cipher.Encrypt(
            envelope.AsSpan(VersionBytes, NonceBytes),
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

    /// <summary>The uncompressed SEC1 encoding of <paramref name="publicKey" />.</summary>
    public static byte[] UncompressedPoint(ECDiffieHellmanPublicKey publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        ECPoint point = publicKey.ExportParameters().Q;

        return [0x04, .. point.X!, .. point.Y!];
    }

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
    public static byte[] WrapPrivateKey(
        byte[] keyEncryptionKey,
        ECDiffieHellman keyPair,
        Guid factorId) =>
        WrapPrivateKeyUnderNonce(
            keyEncryptionKey, keyPair, factorId, RandomNumberGenerator.GetBytes(NonceBytes));

    /// <summary>The same wrap over a <paramref name="nonce" /> the caller chose.</summary>
    /// <remarks><see cref="SealUnderNonce" />'s: known-answer tests and nothing else.</remarks>
    public static byte[] WrapPrivateKeyUnderNonce(
        byte[] keyEncryptionKey,
        ECDiffieHellman keyPair,
        Guid factorId,
        byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(keyPair);

        return SealUnderNonce(
            keyEncryptionKey,
            nonce,
            keyPair.ExportPkcs8PrivateKey(),
            WrappedPrivateKeyAssociatedData(factorId));
    }

    /// <summary>
    /// The account's content key and index key as one 64-byte plaintext, <b>content key first</b>,
    /// encapsulated to <paramref name="recipient" /> and bound to <paramref name="factorId" />:
    /// <c>version ‖ ephemeral public key ‖ nonce ‖ ciphertext ‖ tag</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order of the two keys is a contract between clients and this server can never check
    /// it.</b> Both are 32 bytes, so a pair the other way round produces a value of exactly the right
    /// width carrying exactly the right version, which stores, reads back and opens — and yields an
    /// index key used to seal narrative text and a content key used to compute blind indexes. Nothing
    /// on the server side has a symptom, and no vector in the frozen file has one either: the 158-byte
    /// value is pinned <em>opening to</em> the two frozen keys in that order, which is the only place
    /// the halves are held at all.
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
        using ECDiffieHellman ephemeral = CreateFactorKeyPair();

        return EncapsulateAccountKeysUnder(
            ephemeral,
            RandomNumberGenerator.GetBytes(NonceBytes),
            recipient,
            contentKey,
            indexKey,
            factorId);
    }

    /// <summary>
    /// The same encapsulation over an <paramref name="ephemeral" /> pair and a
    /// <paramref name="nonce" /> the caller chose.
    /// </summary>
    /// <remarks>
    /// <see cref="SealUnderNonce" />'s argument, doubled: a reused ephemeral pair <em>and</em> a reused
    /// nonce under one recipient hand two factors the same <c>(key, nonce)</c>. Known-answer tests only.
    /// </remarks>
    public static byte[] EncapsulateAccountKeysUnder(
        ECDiffieHellman ephemeral,
        byte[] nonce,
        ECDiffieHellmanPublicKey recipient,
        byte[] contentKey,
        byte[] indexKey,
        Guid factorId)
    {
        ArgumentNullException.ThrowIfNull(ephemeral);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(contentKey);
        ArgumentNullException.ThrowIfNull(indexKey);

        byte[] ephemeralPoint = UncompressedPoint(ephemeral.PublicKey);
        byte[] key = EncapsulationKey(
            ephemeral.DeriveRawSecretAgreement(recipient),
            EncapsulationInfo(factorId, ephemeralPoint, UncompressedPoint(recipient)));

        byte[] plaintext = [.. contentKey, .. indexKey];

        byte[] value =
            new byte[VersionBytes + PublicKeyBytes + NonceBytes + plaintext.Length + TagBytes];
        value[0] = EncapsulationVersion;
        ephemeralPoint.CopyTo(value.AsSpan(VersionBytes));
        nonce.CopyTo(value.AsSpan(VersionBytes + PublicKeyBytes));

        int ciphertextOffset = VersionBytes + PublicKeyBytes + NonceBytes;
        using AesGcm cipher = new(key, TagBytes);
        cipher.Encrypt(
            value.AsSpan(VersionBytes + PublicKeyBytes, NonceBytes),
            plaintext,
            value.AsSpan(ciphertextOffset, plaintext.Length),
            value.AsSpan(ciphertextOffset + plaintext.Length, TagBytes),
            EncapsulatedAccountKeysAssociatedData(factorId));

        return value;
    }

    /// <summary>
    /// Opens a factor's stored pair the way a client does: unwrap the private key under
    /// <paramref name="keyEncryptionKey" />, then decapsulate with the private half it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two steps and not one, and the order is the design.</b> The first needs a key derived from a
    /// recovery factor the person is holding; the second needs only the output of the first. A value
    /// that fails at either step yields nothing, which is what lets a caller assert that another
    /// factor's key opens nothing here without having to say which step refused it.
    /// </para>
    /// <para>
    /// <b>The recipient's own point is recomputed from the unwrapped private key, never carried beside
    /// the envelope.</b> That is the thing a reader reaches for on meeting an <c>info</c> naming two
    /// points — a second copy of the factor's public key on the wire, or a parameter here — and it is
    /// unnecessary: <c>ExportParameters(includePrivateParameters: false).Q</c> after
    /// <c>ImportPkcs8PrivateKey</c> returns a populated <c>Q</c>, byte-identical to the original, so the
    /// point the <c>info</c> needs is already inside the value the first step produced. A copy on the
    /// wire would also be a copy something could disagree with.
    /// </para>
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
                WrappedPrivateKeyAssociatedData(factorId),
                out byte[] pkcs8))
        {
            return false;
        }

        int floor = VersionBytes + PublicKeyBytes + NonceBytes + TagBytes;

        if (encapsulatedAccountKeys.Length < floor
            || encapsulatedAccountKeys[0] != EncapsulationVersion)
        {
            return false;
        }

        using ECDiffieHellman recipient = CreateFactorKeyPair();
        recipient.ImportPkcs8PrivateKey(pkcs8, out _);

        byte[] ephemeralPoint =
            encapsulatedAccountKeys.AsSpan(VersionBytes, PublicKeyBytes).ToArray();

        using ECDiffieHellman ephemeral = PublicKeyFrom(ephemeralPoint);
        byte[] key = EncapsulationKey(
            recipient.DeriveRawSecretAgreement(ephemeral.PublicKey),
            EncapsulationInfo(factorId, ephemeralPoint, UncompressedPoint(recipient.PublicKey)));

        int ciphertextOffset = VersionBytes + PublicKeyBytes + NonceBytes;
        int ciphertextLength = encapsulatedAccountKeys.Length - floor;
        byte[] opened = new byte[ciphertextLength];

        try
        {
            using AesGcm cipher = new(key, TagBytes);
            cipher.Decrypt(
                encapsulatedAccountKeys.AsSpan(VersionBytes + PublicKeyBytes, NonceBytes),
                encapsulatedAccountKeys.AsSpan(ciphertextOffset, ciphertextLength),
                encapsulatedAccountKeys.AsSpan(ciphertextOffset + ciphertextLength, TagBytes),
                opened,
                EncapsulatedAccountKeysAssociatedData(factorId));
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
    /// An account's factor set as the manifest's plaintext spells it:
    /// <c>count ‖ 0x1F ‖ factor id ‖ 0x1F ‖ public key ‖ 0x1F ‖ …</c>, entries ascending by the
    /// canonical spelling of the identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The count leads, as decimal text.</b> Without it, chopping the tail off a manifest produces a
    /// shorter manifest that parses, and the entry it drops is the authenticator somebody still has. As
    /// text and not a byte because eleven factors — a passkey and a card of ten, which is what one
    /// registration writes — is the two characters <c>11</c>.
    /// </para>
    /// <para>
    /// <b>The sort is over the identifier's canonical TEXT, never over its bytes.</b>
    /// <see cref="Guid.ToByteArray()" /> is mixed-endian, so a sort over raw UUID bytes agrees with this
    /// one on most sets and disagrees on some — and the frozen three-factor vector is deliberately one of
    /// the ones it disagrees on, which is the only place in either implementation that disagreement is
    /// visible. <see cref="StringComparer.Ordinal" /> and never a culture-aware comparison, for the same
    /// reason the client refuses a collator: these bytes are frozen and a collation is not.
    /// </para>
    /// <para>
    /// The caller's order is the order it happened to build the set in, and a set has no order — so the
    /// ordering is done here rather than asked for.
    /// </para>
    /// </remarks>
    public static byte[] ManifestPlaintext(IEnumerable<FactorPublicKey> factors)
    {
        ArgumentNullException.ThrowIfNull(factors);

        FactorPublicKey[] ordered =
            [.. factors.OrderBy(factor => CanonicalFactorId(factor.FactorId), StringComparer.Ordinal)];

        List<byte[]> fields = [Utf8(ordered.Length.ToString(CultureInfo.InvariantCulture))];

        foreach (FactorPublicKey factor in ordered)
        {
            fields.Add(Utf8(CanonicalFactorId(factor.FactorId)));
            fields.Add(factor.PublicKey);
        }

        return JoinFields([.. fields]);
    }

    /// <summary>
    /// The account's manifest at <paramref name="rotationEpoch" />, sealed under its
    /// <b>content key</b>.
    /// </summary>
    /// <remarks>
    /// <b>The content key and not the index key, and nothing on either side of the wire says so.</b> The
    /// server enforces presence, framing and epoch and can never read a byte of it, so a manifest sealed
    /// under the wrong half of the account's material stores, comes back, and fails to open on the day
    /// somebody rotates. It is the same class of silence as the encapsulated plaintext's half order, and
    /// it is held here by the frozen <c>manifestSealed</c> vector and by nothing else.
    /// </remarks>
    public static byte[] SealManifest(
        byte[] contentKey,
        IEnumerable<FactorPublicKey> factors,
        int rotationEpoch) =>
        SealManifestUnderNonce(
            contentKey, factors, rotationEpoch, RandomNumberGenerator.GetBytes(NonceBytes));

    /// <summary>The same seal over a <paramref name="nonce" /> the caller chose.</summary>
    /// <remarks><see cref="SealUnderNonce" />'s: known-answer tests and nothing else.</remarks>
    public static byte[] SealManifestUnderNonce(
        byte[] contentKey,
        IEnumerable<FactorPublicKey> factors,
        int rotationEpoch,
        byte[] nonce) =>
        SealUnderNonce(
            contentKey,
            nonce,
            ManifestPlaintext(factors),
            ManifestAssociatedData(rotationEpoch));

    /// <summary>
    /// Opens a sealed manifest under <paramref name="contentKey" /> at
    /// <paramref name="rotationEpoch" />, or returns <see langword="false" />.
    /// </summary>
    /// <remarks>
    /// <b>Hands back the plaintext bytes and stops there</b>, the boundary the client's own reader keeps:
    /// what a caller needs from a manifest today is that the tag verified, which is how it confirms that
    /// the content key it just obtained really is the account's. Parsing the named set back out is a
    /// different question and would be a parser with no caller.
    /// </remarks>
    public static bool TryOpenManifest(
        byte[] contentKey,
        byte[] sealedManifest,
        int rotationEpoch,
        out byte[] plaintext) =>
        TryOpen(contentKey, sealedManifest, ManifestAssociatedData(rotationEpoch), out plaintext);

    /// <summary>
    /// Joins <paramref name="fields" /> with one <see cref="UnitSeparatorByte" /> between them and none
    /// at either end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bytes and not an interpolated string, and that is a correctness decision rather than a
    /// style.</b> Two of the fields this grammar carries are not text: the version is a raw byte, and
    /// the <c>info</c> carries two raw 65-byte points. A point pushed through UTF-8 comes out 129 bytes
    /// long, and a version byte composed as text is one byte at <c>0x01</c> and <em>two</em> from
    /// <c>0x80</c> up — silently, with no error anywhere. The frozen vector at version <c>0x80</c> exists
    /// so that a text-composed message is caught by its length before such a version could ship.
    /// </para>
    /// <para>
    /// <b>The separator goes between the fields, never around them, and the test is the field's
    /// position.</b> <c>at &gt; 0</c> reads identically and is wrong in one place: a leading empty field
    /// leaves nothing written, so the second field is taken for the first and its separator is dropped.
    /// Every field is kept, empty ones included — dropping one seals two different field lists to the
    /// same bytes, which is the one ambiguity a separator exists to remove.
    /// </para>
    /// <para>
    /// <b><see langword="private" />, and it used to be <c>public</c> with a sentence asking nobody to
    /// call it.</b> The reason for the widening was that a test had to reach a version field this scheme
    /// does not have, which made the <c>0x80</c> pin a guard over <em>this helper</em> — leaving nothing
    /// to say that the three builders route through it at all. Rewrite any one of them as an
    /// interpolated string and that arrangement stayed green. The builders now take the version
    /// themselves, so the frozen <c>0x80</c> message is reached through
    /// <see cref="WrappedPrivateKeyAssociatedData" /> and the guard sits where the defect can occur; a
    /// rule held by a request in a doc comment is replaced by an accessibility keyword.
    /// </para>
    /// </remarks>
    private static byte[] JoinFields(params byte[][] fields)
    {
        int width = Math.Max(fields.Length - 1, 0) + fields.Sum(field => field.Length);
        byte[] message = new byte[width];
        int at = 0;

        for (int index = 0; index < fields.Length; index++)
        {
            if (index > 0)
            {
                message[at] = UnitSeparatorByte;
                at++;
            }

            fields[index].CopyTo(message, at);
            at += fields[index].Length;
        }

        return message;
    }

    /// <summary>
    /// <paramref name="version" /> as the one raw byte it is, fresh per call.
    /// </summary>
    /// <remarks>
    /// A fresh array rather than a shared one, the rule the client keeps: a shared buffer is one a caller
    /// could reach into and change under the next caller's message. Raw, and never pushed through UTF-8:
    /// that is the whole reason the three builders compose from bytes, and it is invisible below
    /// <c>0x80</c>.
    /// </remarks>
    private static byte[] GrammarVersion(byte version) => [version];

    /// <summary>
    /// The <c>"D"</c> spelling of <paramref name="factorId" /> — lower-case, hyphenated — which is the
    /// only spelling any message of this grammar is built from.
    /// </summary>
    public static string CanonicalFactorId(Guid factorId) => factorId.ToString("D");

    /// <summary>UTF-8, which is the encoding every text field of this grammar is written in.</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

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
            Utf8(Canonical(code)),
            KeyBytes,
            salt: [],
            Utf8(info));
}
