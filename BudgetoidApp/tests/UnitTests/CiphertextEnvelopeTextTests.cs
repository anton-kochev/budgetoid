using System.Buffers.Text;
using Application.Security;
using Domain.Security;

namespace UnitTests;

/// <summary>
/// The wire-side step for every AEAD envelope a client sends as text: base64url within a ceiling the
/// caller names, then the framing rules
/// <see cref="CiphertextEnvelope"/> owns — at least <see cref="CiphertextEnvelope.MinimumLength"/>
/// bytes, leading with <see cref="CiphertextEnvelope.Version"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A <c>Try</c> shape, for the reason <see cref="Application.Passkeys.PasskeyEncoding.TryDecode"/>
/// gives for its own.</b> Every call site already turns a malformed member into a refusal sentence of
/// its own and those sentences differ, so nothing here throws and nothing here reports <em>why</em>.
/// What every refusal below asserts instead is that the <c>out</c> parameter came back
/// <see langword="null"/>: a caller that reads the buffer without reading the result must find nothing
/// to work with.
/// </para>
/// <para>
/// <b>Every bound is read from the production constant that owns it, not restated.</b> This type
/// <em>applies</em> the format's rules and defines none of them, so a literal here would let the edge
/// and the format drift apart while staying green — which is precisely the failure it is being written
/// to prevent. That is the opposite of the choice <c>CiphertextEnvelopeTests</c> makes for its two
/// pinning cases, and the distinction is deliberate: a test that pins a number <em>itself</em> has to
/// restate it, because reading the constant it is checking agrees with whatever that constant becomes.
/// <c>WrappedKeyEnvelopeTests</c> records the same argument for itself.
/// </para>
/// <para>
/// <b>The ceiling is a parameter, and that is what makes it testable at all.</b> Story 12.2 names no
/// per-field number — those arrive with the fields — so what can be pinned today is not a value but a
/// relation: the same bytes are accepted under a ceiling that admits them and refused under one that
/// does not. <see cref="TryDecode_WithAnEnvelopeLongerThanTheMinimum_ReturnsExactlyThoseBytes"/> and
/// <see cref="TryDecode_WithTextLongerThanTheCeiling_Refuses"/> hand the decoder the same encoding and
/// differ only in the ceiling, so an implementation that ignored the argument and reached for a
/// constant of its own fails exactly one of them.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a refusal test with no accepting twin is passed by
/// a function that returns <see langword="false"/> unconditionally:
/// </para>
/// <list type="bullet">
/// <item>
/// "at least the fixed parts" —
/// <see cref="TryDecode_WithAnEnvelopeOneByteShorterThanTheMinimum_Refuses"/> against
/// <see cref="TryDecode_WithTheShortestPossibleEnvelope_Decodes"/>, which sits exactly on the bound, so
/// a floor written <c>&gt;</c> reddens exactly one case rather than none.
/// </item>
/// <item>
/// "a floor, not a width" —
/// <see cref="TryDecode_WithAnEnvelopeLongerThanTheMinimum_ReturnsExactlyThoseBytes"/>, the only case
/// here that a decoder still measuring one exact width would fail.
/// </item>
/// <item>
/// "the leading byte is the version" —
/// <see cref="TryDecode_WithAnUnrecognisedVersion_Refuses"/> and
/// <see cref="TryDecode_WithAnAllZeroPayloadOfALegalLength_Refuses"/>, both over buffers of a legal
/// length, so nothing but a version check separates them from one this deployment can interpret.
/// </item>
/// <item>
/// "not standard base64's two extra characters" —
/// <see cref="TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses"/> against
/// <see cref="TryDecode_WithAPaddedEncoding_Decodes"/>, which pins the half of the alphabet rule that
/// is easy to over-tighten. The claim is deliberately that narrow. The refusing case substitutes
/// <c>+</c> and <c>/</c>, so what it proves is that those two are refused — not that the decoder
/// accepts base64url and nothing else, which is a broader claim than any case here supports.
/// Measured, the decoder is looser than the broader claim would be:
/// <see cref="Base64Url.IsValid(ReadOnlySpan{char})"/> skips whitespace wherever it appears, so
/// <c>AAAA AAAA</c>, a leading space, an embedded tab and a trailing newline all validate and decode
/// to the same bytes as <c>AAAAAAAA</c>. Nothing here covers that and no case is being added for it:
/// the statement that needs narrowing is the normative one, and it is being corrected where it lives.
/// </item>
/// <item>
/// "the ceiling is a ceiling on <em>decoded</em> bytes" —
/// <see cref="TryDecode_WithAnEnvelopeOneByteOverTheCeiling_Refuses"/>, which nothing else here can
/// cover: it is the only case whose text passes the encoded-length gate and whose refusal therefore
/// has to come from a comparison made after decoding.
/// </item>
/// </list>
/// </remarks>
public sealed class CiphertextEnvelopeTextTests
{
    /// <summary>
    /// An envelope longer than the minimum decodes to exactly the bytes that were encoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case the whole type exists for, and it carries two claims no other case here can.</b>
    /// The first is that the length rule is a floor: write it as an equality against
    /// <see cref="CiphertextEnvelope.MinimumLength"/> — the shape
    /// <see cref="Application.Passkeys.WrappedKeyEnvelope"/> uses, correctly, over a key of one legal
    /// size — and every other test in this file still passes, over a type that quietly refuses every
    /// entry longer than an empty one. The person finds out by not being able to save what they typed.
    /// </para>
    /// <para>
    /// The second is that the bytes handed back are the bytes that were sent. Content is asserted, not
    /// length: a decoder returning a fresh buffer of the right size, the caller's text truncated to it,
    /// or the payload with its version byte already stripped satisfies every other case here, and what
    /// it hands back is ciphertext nobody will try to open until the day they need it. The filler
    /// varies per index for the same reason — a uniform payload is one <c>Array.Fill</c> away from
    /// being reproduced by a decoder that read nothing.
    /// </para>
    /// <para>
    /// The ceiling is the envelope's own width, so this case also states the bound's polarity: a value
    /// sitting exactly on the ceiling is admitted, and an implementation off by one in that comparison
    /// reddens here and nowhere else.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnEnvelopeLongerThanTheMinimum_ReturnsExactlyThoseBytes()
    {
        // Arrange
        byte[] expected = Envelope(CiphertextEnvelope.MinimumLength + 7);

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            Encode(expected), CiphertextEnvelope.MinimumLength + 7, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An envelope of exactly <see cref="CiphertextEnvelope.MinimumLength"/> bytes decodes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The accepting case that sits on the bound, and it carries two claims that are otherwise
    /// homeless.</b> The first is the floor's polarity. An empty plaintext is a legitimate value — a
    /// note with no text, a field somebody cleared — and AES-GCM ciphertext is exactly the length of
    /// its plaintext, so an empty plaintext seals to a version, a nonce and a tag and nothing else:
    /// exactly this length. A rule written <c>&gt;</c> rather than <c>&gt;=</c> refuses a value the
    /// format produces, and reddens here.
    /// </para>
    /// <para>
    /// The second is that the version check is a real check. This buffer and the one
    /// <see cref="TryDecode_WithAnAllZeroPayloadOfALegalLength_Refuses"/> hands over are the same
    /// length and differ in one bit of one byte, so no length rule can separate them and no
    /// implementation can pass both by measuring width and calling it a version check. That is why the
    /// payload here is left at zero rather than filled: the pair has to differ in the version byte and
    /// in nothing else.
    /// </para>
    /// <para>
    /// Written as its own case rather than folded into
    /// <see cref="TryDecode_WithAPaddedEncoding_Decodes"/>, which is the same width, because a single
    /// case carrying both the padding claim and the floor's polarity would fail without saying which
    /// of the two moved.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTheShortestPossibleEnvelope_Decodes()
    {
        // Arrange
        byte[] expected = new byte[CiphertextEnvelope.MinimumLength];
        expected[0] = CiphertextEnvelope.Version;

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            Encode(expected), CiphertextEnvelope.MinimumLength, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An envelope one byte shorter than <see cref="CiphertextEnvelope.MinimumLength"/> is refused.
    /// </summary>
    /// <remarks>
    /// The floor, judged after decoding rather than before. Because padding is accepted, the encoded
    /// length does not name the decoded one: 40 characters is 30 bytes unpadded and 29 with a single
    /// <c>=</c>, so a floor applied to the string would refuse values a client legitimately encoded
    /// while admitting ones it should not. The ceiling here comfortably admits this text — 38
    /// characters against an allowance of 40 — and the leading byte is the version this deployment
    /// recognises, which is what makes the refusal attributable to the length and to nothing else.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnEnvelopeOneByteShorterThanTheMinimum_Refuses()
    {
        // Arrange
        string truncated = Encode(Envelope(CiphertextEnvelope.MinimumLength - 1));

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            truncated, CiphertextEnvelope.MinimumLength, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// An envelope whose leading byte is not the one version defined today is refused, from both sides
    /// of it and at both widths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IFR-007 at the edge. Version 2 is a client claiming a contract this deployment has never
    /// implemented; version 0 is a field nobody set. Stored either way the symptom is silent and late —
    /// the row is well-formed and the bytes turn out to be uninterpretable on the day somebody needs
    /// them back.
    /// </para>
    /// <para>
    /// <b>The second axis is not padding.</b> A wrong version on a minimum-length buffer and a wrong
    /// version on a long one are two different paths the moment the length rule stops being an
    /// equality: an implementation that read the leading byte only after measuring one exact width
    /// would refuse the short pair for the right reason and let the long pair through, which is exactly
    /// the mistake the variable-length case exists to catch. 137 is odd and a multiple of neither 16
    /// nor 32, so no block- or key-aligned arithmetic admits it by luck.
    /// </para>
    /// <para>
    /// The ceiling is each case's own width, so neither refusal can be the ceiling's doing.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments((byte)(CiphertextEnvelope.Version - 1), CiphertextEnvelope.MinimumLength)]
    [Arguments((byte)(CiphertextEnvelope.Version + 1), CiphertextEnvelope.MinimumLength)]
    [Arguments((byte)(CiphertextEnvelope.Version - 1), CiphertextEnvelope.MinimumLength + 137)]
    [Arguments((byte)(CiphertextEnvelope.Version + 1), CiphertextEnvelope.MinimumLength + 137)]
    public async Task TryDecode_WithAnUnrecognisedVersion_Refuses(byte version, int width)
    {
        // Arrange
        byte[] misversioned = Envelope(width);
        misversioned[0] = version;

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            Encode(misversioned), width, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// A buffer of a legal length holding nothing but zeros is refused, for its version byte.
    /// </summary>
    /// <remarks>
    /// The more valuable half of the version pair: an all-zero buffer of a legal length is what an
    /// uninitialised member, a zero-filled allocation and a client that has stubbed the field all send,
    /// and it satisfies the length rule exactly. Its accepting counterpart —
    /// <see cref="TryDecode_WithTheShortestPossibleEnvelope_Decodes"/>, over a buffer of the same
    /// length — differs from it in one bit of one byte, so no length check can separate the two and no
    /// implementation can pass both by measuring width and calling it a version check.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnAllZeroPayloadOfALegalLength_Refuses()
    {
        // Arrange
        string unset = Encode(new byte[CiphertextEnvelope.MinimumLength]);

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            unset, CiphertextEnvelope.MinimumLength, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// Standard base64's two extra characters are refused, in text that is otherwise a perfect
    /// encoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>+</c> and <c>/</c> are what base64url replaces with <c>-</c> and <c>_</c>, and this API emits
    /// neither: admitting them would mean a value nothing here produces decodes anyway. They are
    /// substituted into a valid encoding rather than produced by re-encoding, because a random payload
    /// run through <c>Convert.ToBase64String</c> need contain neither character, and that arrangement
    /// would pass against a decoder with no alphabet check at all.
    /// </para>
    /// <para>
    /// The substitution deliberately lands past the leading group. Characters 0-3 carry the first three
    /// decoded bytes, the version among them, so mangling those would leave a decoder that skipped the
    /// alphabet check refusing the value for its version instead — the test would stay green while
    /// covering nothing it names. The length is untouched, which rules out the two length bounds the
    /// same way.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses()
    {
        // Arrange
        char[] mangled = [.. Encode(Envelope(CiphertextEnvelope.MinimumLength + 7))];
        mangled[4] = '+';
        mangled[5] = '/';

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            new string(mangled), CiphertextEnvelope.MinimumLength + 7, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// A well-formed envelope carrying base64's <c>=</c> padding is accepted, and yields the same
    /// bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Base64url omits the padding and <see cref="Application.Passkeys.PasskeyEncoding"/> accepts it
    /// anyway, deliberately — its ceiling is computed on the padded form so that a client which pads is
    /// never refused for a value it legitimately encoded. This is the half of the alphabet rule a
    /// reader will over-tighten, and it sits exactly on the ceiling: 29 bytes is 39 characters unpadded
    /// and 40 padded, and 40 is the whole allowance, so an implementation measuring the allowance
    /// against the unpadded length would refuse this and nothing else.
    /// </para>
    /// <para>
    /// One <c>=</c> and not two: 29 bytes leaves a two-byte remainder, which is three base64 characters
    /// and one pad. A second pad character is not a stricter version of this case — it is text no
    /// encoder produces for this width, and it would be refused for being malformed rather than
    /// accepted for being padded, leaving the claim in the summary untested.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAPaddedEncoding_Decodes()
    {
        // Arrange
        byte[] expected = Envelope(CiphertextEnvelope.MinimumLength);
        string padded = Encode(expected) + "=";

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            padded, CiphertextEnvelope.MinimumLength, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected);
    }

    /// <summary>
    /// A well-formed envelope that exceeds the ceiling the caller named is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The text is byte-for-byte the one
    /// <see cref="TryDecode_WithAnEnvelopeLongerThanTheMinimum_ReturnsExactlyThoseBytes"/> accepts, and
    /// only the ceiling differs. That pairing is the point: with a single oversized case, an
    /// implementation that ignored <c>maxDecodedBytes</c> and reached for a constant of its own would
    /// refuse this too and stay green, and the parameter would be decoration. Refused here and accepted
    /// there, the argument is provably what decides the answer.
    /// </para>
    /// <para>
    /// The ceiling is judged before the alphabet is validated and before anything is allocated, which
    /// is the argument <see cref="Application.Passkeys.PasskeyEncoding.TryDecode"/> makes for taking
    /// one at all: validation is a full pass over the text and decoding is a second plus a buffer the
    /// size of the result, so a decode performed first has already cost what the ceiling exists to
    /// refuse.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextLongerThanTheCeiling_Refuses()
    {
        // Arrange
        string oversized = Encode(Envelope(CiphertextEnvelope.MinimumLength + 7));

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            oversized, CiphertextEnvelope.MinimumLength, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// A well-formed envelope one byte over the ceiling is refused, though its encoding fits inside
    /// the allowance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one case here whose refusal can only come from a comparison made after decoding, and the
    /// hole the rest of this file leaves open.</b>
    /// <see cref="Application.Passkeys.PasskeyEncoding.TryDecode"/> bounds the <em>text</em>, against
    /// the padded form of the allowance, and that form overshoots by up to two characters. So the
    /// encoded-length gate does not imply the ceiling it is derived from: a 30-byte envelope encodes to
    /// 40 characters and the allowance for a ceiling of 29 is also 40, so this value arrives past every
    /// check the text can carry.
    /// </para>
    /// <para>
    /// <see cref="Application.Passkeys.WrappedKeyEnvelope"/> meets the same slack and keeps an exact
    /// width, but not for the wide side: its own comment says "the ceiling and the width are the same
    /// 61 bytes here", so an over-wide envelope is refused for the ceiling and is gone before the width
    /// is consulted. What that width earns its place on is the <em>short</em> side — the band its
    /// comment names, where "a 29- to 60-byte envelope clears the floor, clears the ceiling, carries
    /// the right version, and is still not a wrapped key". This type has no width to close such a band
    /// with, only a floor, so the wide side is the whole of what it can refuse, and what refuses it is
    /// the <c>bytes.Length &gt; maxDecodedBytes</c> comparison
    /// <see cref="Application.Passkeys.PasskeyEncoding.TryDecode"/> makes after decoding — reached
    /// through here rather than restated here, which is what that method's own remarks give as the
    /// reason it lives in the decoder. A ceiling that admits more than it names is not a ceiling, and
    /// the per-field limits arriving later are the numbers a field's own refusal will be written from.
    /// </para>
    /// <para>
    /// The envelope is well-formed in every other respect — a legal length, the version byte this
    /// deployment recognises — so neither framing rule can be what refuses it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnEnvelopeOneByteOverTheCeiling_Refuses()
    {
        // Arrange
        string overlong = Encode(Envelope(CiphertextEnvelope.MinimumLength + 1));

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            overlong, CiphertextEnvelope.MinimumLength, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// An absent member is refused rather than treated as an absent envelope.
    /// </summary>
    /// <remarks>
    /// A missing JSON property arrives here as <see langword="null"/> — the shape a client that has not
    /// implemented sealing yet sends. Refusing it is what keeps every call site's member required
    /// without each of them writing its own null check first.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithNoValue_Refuses()
    {
        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            null, CiphertextEnvelope.MinimumLength, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// Empty text is refused.
    /// </summary>
    /// <remarks>
    /// Separate from the null case because it is a different mistake with the same answer: an unbound
    /// form control sends <c>""</c>, not <see langword="null"/>. An empty plaintext is a legitimate
    /// value and seals to a full <see cref="CiphertextEnvelope.MinimumLength"/> bytes, so empty
    /// <em>text</em> is never how one arrives — it is a member nobody sealed.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithEmptyText_Refuses()
    {
        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(
            string.Empty, CiphertextEnvelope.MinimumLength, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// An envelope of exactly a narrative field's cap decodes, at each of the two caps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cap's polarity, and the reason it needs saying at this layer rather than only at the
    /// field's.</b> <see cref="NarrativeFieldLimits"/> bounds what a column stores, and this member is
    /// the step that turns a client's text into the bytes that bound is applied to. An implementation
    /// comparing <c>&lt;</c> rather than <c>&lt;=</c> refuses a value the limit names as legal, and
    /// nothing else in this file can see it: every other accepting case here sits at
    /// <see cref="CiphertextEnvelope.MinimumLength"/> or a few bytes above, hundreds of bytes below
    /// either cap.
    /// </para>
    /// <para>
    /// <b>Both caps are run, and that is what makes the ceiling parameter provably load-bearing at the
    /// sizes the fields actually use.</b>
    /// <see cref="TryDecode_WithAnEnvelopeLongerThanTheMinimum_ReturnsExactlyThoseBytes"/> and
    /// <see cref="TryDecode_WithTextLongerThanTheCeiling_Refuses"/> already prove the argument decides
    /// the answer, over a 36-byte envelope; a decoder holding one narrative-sized constant of its own
    /// would pass both of those and be wrong at one of these two.
    /// </para>
    /// <para>
    /// The numbers are read from the constants that own them and never typed out — this type applies
    /// the domain's limits and declares none, so a literal here would let the edge and the column drift
    /// apart while staying green. Content is asserted rather than length, for the reason
    /// <see cref="TryDecode_WithAnEnvelopeLongerThanTheMinimum_ReturnsExactlyThoseBytes"/> gives: at
    /// these widths a decoder handing back a fresh buffer of the right size is least likely to be
    /// noticed by eye.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(NarrativeFieldLimits.NameBytes)]
    [Arguments(NarrativeFieldLimits.DescriptionBytes)]
    public async Task TryDecode_WithAnEnvelopeExactlyAtANarrativeCap_Decodes(int cap)
    {
        // Arrange
        byte[] expected = Envelope(cap);

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(Encode(expected), cap, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An envelope one byte over a narrative field's cap is refused, at each of the two caps, though
    /// its encoding fits inside the allowance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The claim <see cref="TryDecode_WithAnEnvelopeOneByteOverTheCeiling_Refuses"/> makes at 29
    /// bytes, made again at the two widths the narrative columns will actually be given — and it is not
    /// a restatement, because the arithmetic that lets a value slip through is width-dependent.</b>
    /// <see cref="Application.Passkeys.PasskeyEncoding.TryDecode"/> gates the <em>text</em> against the
    /// padded form of the allowance, which overshoots by up to two characters; whether a given
    /// over-long value lands inside that overshoot depends on where the width falls modulo three.
    /// Measured, both of these do: for a cap of <see cref="NarrativeFieldLimits.NameBytes"/> the
    /// allowance is 1368 characters and a 1025-byte envelope encodes to 1367, and for
    /// <see cref="NarrativeFieldLimits.DescriptionBytes"/> the allowance is 3416 and a 2561-byte
    /// envelope encodes to 3415. So in both cases the text clears every gate it can be judged by, and
    /// the refusal has to come from the <c>bytes.Length &gt; maxDecodedBytes</c> comparison made on the
    /// decoded buffer.
    /// </para>
    /// <para>
    /// <b>That is the whole reason a cap on decoded bytes is worth a case at all.</b> A limit that
    /// admits more than it names is not a limit, and the excess here is silent: the row stores, the
    /// column's <c>CHECK</c> constraint is the only thing left to refuse it, and if that constraint were
    /// ever written from the same padded arithmetic the value would simply be a field larger than the
    /// product says a field may be.
    /// </para>
    /// <para>
    /// Each envelope is well-formed in every other respect — comfortably past
    /// <see cref="CiphertextEnvelope.MinimumLength"/>, leading with
    /// <see cref="CiphertextEnvelope.Version"/> — so neither framing rule can be what refuses it, and
    /// its accepting twin one byte below is the case above.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(NarrativeFieldLimits.NameBytes)]
    [Arguments(NarrativeFieldLimits.DescriptionBytes)]
    public async Task TryDecode_WithAnEnvelopeOneByteOverANarrativeCap_Refuses(int cap)
    {
        // Arrange
        string overlong = Encode(Envelope(cap + 1));

        // Act
        bool accepted = CiphertextEnvelopeText.TryDecode(overlong, cap, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// A well-formed envelope of <paramref name="width"/> bytes: the version byte, then a payload that
    /// varies per index.
    /// </summary>
    /// <remarks>
    /// The payload is not a nonce and not a ciphertext, and nothing at this layer inspects either — the
    /// server holds no value that could open the envelope. It varies rather than repeating so that the
    /// content assertion cannot be satisfied by a buffer of the right size filled with one byte, and
    /// the multiplier is odd so no run of it is period-aligned to the format's 12- and 16-byte parts.
    /// </remarks>
    private static byte[] Envelope(int width)
    {
        byte[] envelope = new byte[width];

        for (int index = 1; index < width; index++)
        {
            envelope[index] = (byte)(index * 37 + 11);
        }

        if (width > 0)
        {
            envelope[0] = CiphertextEnvelope.Version;
        }

        return envelope;
    }

    /// <summary>
    /// Encodes bytes the way a client hands them to the API: unpadded base64url.
    /// </summary>
    /// <remarks>
    /// The framework's encoder rather than a substitute-and-strip over <see cref="Convert"/>. A
    /// hand-rolled arrangement is how text the specification does not allow ends up in the one place
    /// that is supposed to be proving such text is refused.
    /// </remarks>
    private static string Encode(ReadOnlySpan<byte> value) => Base64Url.EncodeToString(value);
}
