using System.Buffers.Text;
using Application.Passkeys;
using Application.Security;
using Domain.Security;

namespace UnitTests;

/// <summary>
/// The wire-side step for a blind index a client sends as text: base64url through the shared decoder,
/// then the exact width <see cref="IndexedName.BlindIndexLength"/> names.
/// </summary>
/// <remarks>
/// <para>
/// <b>A <c>Try</c> shape, for the reason <see cref="PasskeyEncoding.TryDecode"/> gives for its own.</b>
/// Every call site already turns a malformed member into a refusal sentence of its own and those
/// sentences differ, so nothing here throws and nothing here reports <em>why</em>. What every refusal
/// below asserts instead is that the <c>out</c> parameter came back <see langword="null"/>: a caller
/// that reads the buffer without reading the result must find nothing to work with.
/// </para>
/// <para>
/// <b>The width is read from <see cref="IndexedName.BlindIndexLength"/>, never restated.</b> This type
/// declares no number of its own — it exists to apply the domain's width at the edge — so a literal
/// <c>32</c> here would let the edge and the column drift apart while staying green, which is exactly
/// the failure the arrangement exists to prevent. That is the opposite of the choice
/// <c>CiphertextEnvelopeTests</c> makes for its pinning cases, and the distinction is the one
/// <c>WrappedKeyEnvelopeTests</c> and <c>CiphertextEnvelopeTextTests</c> both record: a test that pins
/// a number <em>itself</em> has to restate it, because reading the constant it is checking agrees with
/// whatever that constant becomes.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a refusal test with no accepting twin is passed by
/// a function that returns <see langword="false"/> unconditionally:
/// </para>
/// <list type="bullet">
/// <item>
/// "exactly <see cref="IndexedName.BlindIndexLength"/> bytes, and the short side is this type's own
/// work" — <see cref="TryDecode_WithAnIndexOneByteShortOfTheWidth_Refuses"/> against
/// <see cref="TryDecode_WithAnIndexOfTheLegalWidth_ReturnsExactlyThoseBytes"/>. The short case is the
/// only one in this file that nothing beneath this type can refuse, and that is measured rather than
/// reasoned: run the shared decoder over a 31-byte value under a ceiling of 32 and it returns
/// <see langword="true"/> with 31 bytes in hand. So a width written as a ceiling — or omitted
/// altogether, on the reading that the decoder's own comparison already covers it — reddens exactly
/// this case.
/// </item>
/// <item>
/// "and the wide side is not this type's, though it is still refused" —
/// <see cref="TryDecode_WithAnIndexOneByteWiderThanTheWidth_Refuses"/> is held two types away, by the
/// <c>bytes.Length &gt; maxDecodedBytes</c> comparison <see cref="PasskeyEncoding.TryDecode"/> makes on
/// the buffer it has just decoded. Measured: 33 bytes encodes to 44 characters and the allowance for a
/// ceiling of 32 is also 44, so the value clears every check the text can carry and is refused after
/// decoding. The case is kept because that is the behaviour the docblock promises a caller — "33 bytes
/// does not slip through the padded allowance" — but a reviewer should know that mutating this type's
/// width check catches nothing here, exactly as <c>WrappedKeyEnvelopeTests</c> records for its own
/// upper bound.
/// </item>
/// <item>
/// "not the envelope's rules" — <see cref="TryDecode_WithAnIndexWhoseLeadingByteIsNotTheVersion_Decodes"/>
/// and <see cref="TryDecode_WithAnAllZeroIndexOfTheLegalWidth_Decodes"/>. These are the cases that
/// separate this type from <see cref="CiphertextEnvelopeText"/>, which is the near miss its docblock
/// names: a blind index has no version byte, no nonce and no tag, and its first byte is whatever
/// HMAC-SHA-256 produced. Built on the envelope decoder instead, this member would admit roughly one
/// value in 256 and refuse the rest — and every other case in this file would still pass, because the
/// fixture's leading byte happens to be legal.
/// </item>
/// <item>
/// "not standard base64's two extra characters" —
/// <see cref="TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses"/> against
/// <see cref="TryDecode_WithAPaddedEncoding_Decodes"/>, which pins the half of the alphabet rule that
/// is easy to over-tighten. The claim is deliberately that narrow: the refusing case substitutes
/// <c>+</c> and <c>/</c>, so what it proves is that those two are refused — not that the decoder
/// accepts base64url and nothing else, which is a broader claim than any case here supports. Its two
/// siblings record the same limit and the same measurement, that
/// <see cref="Base64Url.IsValid(ReadOnlySpan{char})"/> skips whitespace wherever it appears.
/// </item>
/// </list>
/// <para>
/// <b>What no case here can cover, and it is most of what matters.</b> This side holds no index key, so
/// it can never say a value is the index <em>of</em> the name beside it. A correct-width value computed
/// over the wrong text, under the wrong key, or straight out of a random number generator is accepted
/// by every assertion below and is wrong for the life of the account, silently. The width is the only
/// shape check available, which is why it is pinned rather than left out as the small one.
/// </para>
/// </remarks>
public sealed class BlindIndexTextTests
{
    /// <summary>
    /// An index of the one legal width decodes to exactly the bytes that were encoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The accepting control the whole file rests on, and content is asserted rather than
    /// length.</b> A decoder returning a fresh buffer of the right size, or the caller's text truncated
    /// to it, satisfies every other case here — and what it hands back is a keyed digest nobody can
    /// recompute, so the row it lands in matches nothing for the life of the account and no later check
    /// can notice. The filler varies per index for the same reason: a uniform payload is one
    /// <c>Array.Fill</c> away from being reproduced by a decoder that read nothing.
    /// </para>
    /// <para>
    /// It also states the width's polarity from the accepting side. Sitting exactly on the bound, an
    /// implementation off by one in either direction reddens here rather than nowhere.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnIndexOfTheLegalWidth_ReturnsExactlyThoseBytes()
    {
        // Arrange
        byte[] expected = Index(IndexedName.BlindIndexLength);

        // Act
        bool accepted = BlindIndexText.TryDecode(Encode(expected), out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(blindIndex).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An index one byte short of the legal width is refused.
    /// </summary>
    /// <remarks>
    /// <b>The one case in this file that nothing beneath this type refuses, which is the whole reason
    /// the type exists.</b> Measured against the shared decoder: 31 bytes encodes to 42 characters, the
    /// allowance for a ceiling of 32 is 44, and the post-decode comparison is a <c>&gt;</c>, so the
    /// value clears every gate below and arrives with 31 bytes in hand. A ceiling of any tightness is
    /// blind to the short side. It is refused rather than padded into shape, for the reason
    /// <see cref="Application.Passkeys.WrappedKeyEnvelope"/> gives about its own repair: a padded index
    /// stores a well-formed row holding a value that matches nothing, and the row looks correct until
    /// somebody searches for the name it was supposed to find.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnIndexOneByteShortOfTheWidth_Refuses()
    {
        // Arrange
        string truncated = Encode(Index(IndexedName.BlindIndexLength - 1));

        // Act
        bool accepted = BlindIndexText.TryDecode(truncated, out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(blindIndex).IsNull();
    }

    /// <summary>
    /// An index one byte wider than the legal width is refused, though its encoding fits inside the
    /// allowance.
    /// </summary>
    /// <remarks>
    /// The behaviour the docblock promises — "the ceiling and the width are the same number, and both
    /// are applied" — pinned from the wide side. What refuses it is not this type: 33 bytes encodes to
    /// exactly 44 characters and the allowance for a ceiling of 32 is exactly 44, so the text clears
    /// the encoded-length gate and the refusal comes from the <c>bytes.Length &gt; maxDecodedBytes</c>
    /// comparison <see cref="PasskeyEncoding.TryDecode"/> makes afterwards. Kept anyway, because it is
    /// the caller-visible contract; recorded as covering nothing this type owns, because a reviewer who
    /// read it as covering the width check would skip a mutation it cannot catch.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnIndexOneByteWiderThanTheWidth_Refuses()
    {
        // Arrange
        string overlong = Encode(Index(IndexedName.BlindIndexLength + 1));

        // Act
        bool accepted = BlindIndexText.TryDecode(overlong, out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(blindIndex).IsNull();
    }

    /// <summary>
    /// An index of the legal width whose leading byte is not the envelope version is accepted.
    /// </summary>
    /// <remarks>
    /// <b>The case that separates this type from <see cref="CiphertextEnvelopeText"/>, which its own
    /// docblock names as the near miss.</b> A blind index is a keyed digest: no version, no nonce, no
    /// tag, and a first byte that is whatever HMAC-SHA-256 produced. Built on the envelope decoder — the
    /// neighbour that sits beside it in the same request and the same row — this member would require
    /// <c>0x01</c> and refuse roughly 255 values in 256 as malformed, and the client would find out by
    /// having most of its names rejected at random. The leading byte here is deliberately
    /// <see cref="CiphertextEnvelope.Version"/> plus one, so nothing but the absence of a framing rule
    /// admits it.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnIndexWhoseLeadingByteIsNotTheVersion_Decodes()
    {
        // Arrange
        byte[] expected = Index(IndexedName.BlindIndexLength);
        expected[0] = (byte)(CiphertextEnvelope.Version + 1);

        // Act
        bool accepted = BlindIndexText.TryDecode(Encode(expected), out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(blindIndex).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An all-zero buffer of the legal width is accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the "not the envelope's rules" pair, and the one a reader will want to
    /// tighten. Beside the envelope types an all-zero payload is the shape an uninitialised member, a
    /// zero-filled allocation and a stubbed client all send, and every one of them refuses it — for its
    /// <em>version byte</em>, a rule a digest has nothing to answer with.
    /// </para>
    /// <para>
    /// This side cannot tell that buffer from a legitimate index, and no addition to this type would
    /// change that: an index is 32 bytes of keyed output and all-zeros is as plausible an output as any
    /// other. Refusing it would be a rule keyed on one value out of 2^256 and would refuse a correct
    /// index on the day HMAC produced it. What catches a client that never computed one is that the
    /// name it filed will not be found — which nothing on this server can see, and which is stated here
    /// so the omission reads as a decision rather than a gap.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnAllZeroIndexOfTheLegalWidth_Decodes()
    {
        // Arrange
        byte[] expected = new byte[IndexedName.BlindIndexLength];

        // Act
        bool accepted = BlindIndexText.TryDecode(Encode(expected), out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(blindIndex).IsEquivalentTo(expected);
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
    /// The length is untouched, so neither the width nor the ceiling can be what refuses this. Unlike
    /// its two siblings, the position of the substitution carries no argument here: a blind index has no
    /// leading byte anything inspects, so there is no group whose damage a version check would mask.
    /// Characters 4 and 5 are used anyway, so that the three files read the same way.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses()
    {
        // Arrange
        char[] mangled = [.. Encode(Index(IndexedName.BlindIndexLength))];
        mangled[4] = '+';
        mangled[5] = '/';

        // Act
        bool accepted = BlindIndexText.TryDecode(new string(mangled), out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(blindIndex).IsNull();
    }

    /// <summary>
    /// An index carrying base64's <c>=</c> padding is accepted, and yields the same bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Settled by measurement rather than by reading, because the arithmetic lands exactly on the
    /// bound.</b> Base64url omits the padding and <see cref="PasskeyEncoding"/> accepts it anyway,
    /// deliberately — its ceiling is computed on the padded form so that a client which pads is never
    /// refused for a value it legitimately encoded. 32 bytes is 43 characters unpadded and 44 padded,
    /// and 44 is the whole allowance for a ceiling of 32, so this text sits on the last character the
    /// gate admits. An implementation measuring the allowance against the unpadded length would refuse
    /// this and nothing else.
    /// </para>
    /// <para>
    /// One <c>=</c> and not two, and that is not a stylistic choice: 32 bytes leaves a two-byte
    /// remainder, which is three base64 characters and a single pad. A second pad is text no encoder
    /// produces for this width, it makes the string 45 characters — one past the allowance — and it is
    /// refused. So a two-pad case would be green for the length rule while claiming to be about
    /// padding, leaving the claim in the summary untested.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAPaddedEncoding_Decodes()
    {
        // Arrange
        byte[] expected = Index(IndexedName.BlindIndexLength);
        string padded = Encode(expected) + "=";

        // Act
        bool accepted = BlindIndexText.TryDecode(padded, out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(blindIndex).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An absent member is refused rather than treated as an absent index.
    /// </summary>
    /// <remarks>
    /// A missing JSON property arrives here as <see langword="null"/> — the shape a client that has not
    /// implemented blind indexing yet sends. There is no such thing as a name without an index: the
    /// column pair is <c>NOT NULL</c> on both sides and <see cref="IndexedName"/> refuses a half, so an
    /// absent index has no reading but a refusal.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithNoValue_Refuses()
    {
        // Act
        bool accepted = BlindIndexText.TryDecode(null, out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(blindIndex).IsNull();
    }

    /// <summary>
    /// Empty text is refused.
    /// </summary>
    /// <remarks>
    /// Separate from the null case because it is a different mistake with the same answer: an unbound
    /// form control sends <c>""</c>, not <see langword="null"/>. HMAC-SHA-256 has no empty output — the
    /// digest is 32 bytes whatever it was taken over — so empty text is never how an index arrives.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithEmptyText_Refuses()
    {
        // Act
        bool accepted = BlindIndexText.TryDecode(string.Empty, out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(blindIndex).IsNull();
    }

    /// <summary>
    /// Text that is not an encoding at all is refused.
    /// </summary>
    /// <remarks>
    /// The blunt case its sibling <c>WrappedKeyEnvelopeTests</c> also keeps: a member that was never
    /// encoded, as opposed to one encoded in the wrong alphabet. Its length is well inside the
    /// allowance, so the refusal is the alphabet's and not the ceiling's.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextThatIsNotAnEncoding_Refuses()
    {
        // Act
        bool accepted = BlindIndexText.TryDecode("not base64url at all!", out byte[]? blindIndex);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(blindIndex).IsNull();
    }

    /// <summary>
    /// A blind index of <paramref name="width"/> bytes: filler that varies per position.
    /// </summary>
    /// <remarks>
    /// Not a real digest, and nothing at this layer could tell — the server holds no index key, so a
    /// value of the right width is indistinguishable from one HMAC-SHA-256 produced. It varies rather
    /// than repeating so that the content assertions cannot be satisfied by a buffer of the right size
    /// filled with one byte, and the multiplier is odd so no run of it is period-aligned to the width.
    /// The leading byte is deliberately left as filler: unlike an envelope, an index has no reserved
    /// position, and fixing one here would hide an implementation that borrowed the envelope's rules.
    /// </remarks>
    private static byte[] Index(int width)
    {
        byte[] index = new byte[width];

        for (int position = 0; position < width; position++)
        {
            index[position] = (byte)(position * 37 + 11);
        }

        return index;
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
