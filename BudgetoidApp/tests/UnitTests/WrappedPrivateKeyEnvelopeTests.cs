using Application.Passkeys;
using Domain.Users;
using TestSupport;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The decode-and-validate step the three write paths share for a factor's <b>wrapped private key</b>:
/// base64url text within a ceiling, exactly one legal width, and the one <b>AEAD</b> framing version
/// defined today.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file per framing, and the split is the point rather than a tidy-up.</b> This file and
/// <see cref="EncapsulatedAccountKeysEnvelopeTests" /> replace a single suite that covered both members
/// of a pair, back when both were AEAD envelopes of one width carrying one version constant. They are
/// not that any more: this one is <c>version(1) ‖ nonce(12) ‖ ciphertext ‖ tag(16)</c> over a PKCS#8
/// P-256 private key, at <see cref="WrappedAccountKeys.WrappedPrivateKeyLength" /> bytes; the other is
/// <c>version(1) ‖ ephemeral public key(65) ‖ nonce(12) ‖ ciphertext ‖ tag(16)</c> over both account
/// keys, at <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" />. A file arguing about both
/// would have to say "the width" and "the version" without saying which, which is precisely the
/// confusion the two types exist to prevent — and every number below would become a parameter a reader
/// has to trace to know which suite a case is about.
/// </para>
/// <para>
/// <b>A <c>Try</c> shape, for the reason <see cref="PasskeyEncoding.TryDecode" /> gives for its own.</b>
/// All three call sites — registration, passkey registration and recovery-code generation — already turn
/// a malformed member into a refusal sentence of their own, and those sentences differ, so nothing here
/// throws and nothing here reports <em>why</em>. What every refusal below asserts instead is that the
/// <c>out</c> parameter came back <see langword="null" />: a caller that reads the buffer without
/// reading the result must find nothing to work with.
/// </para>
/// <para>
/// <b>Every bound is read from the production constant that owns it, not restated.</b> That is the
/// opposite of the choice <c>WrappedAccountKeysTests</c> makes, and deliberately so: that suite tests
/// the type that <em>defines</em> the width, where a test taking its bound from the type under test
/// agrees with any bound that type later chooses. This type defines nothing — it exists to apply the
/// domain's numbers at the edge — so a literal here would let the edge and the entity drift apart and
/// stay green, which is precisely the failure it is being written to prevent.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a refusal test with no accepting twin can be
/// passed by a function that returns <see langword="false" /> unconditionally:
/// </para>
/// <list type="bullet">
/// <item>
/// "exactly <see cref="WrappedAccountKeys.WrappedPrivateKeyLength" /> bytes, a width and not a cap" —
/// <see cref="TryDecode_WithAnEnvelopeOfTheWrongWidth_Refuses" /> asserts both sides, but only one of
/// them is covered here, and the split is measured rather than reasoned. Write the width so that it
/// admits an envelope that is too <em>short</em> and exactly one case reddens, the 166-byte one. Write
/// it so that it admits one that is too <em>long</em> and <b>nothing reddens at all</b>: the 168-byte
/// value is refused by the ceiling inside <see cref="PasskeyEncoding.TryDecode" /> — the
/// <c>bytes.Length &gt; maxDecodedBytes</c> comparison it makes on the buffer it has just decoded —
/// before this type's width is consulted. <c>CiphertextEnvelopeText</c> is only on the path and says so
/// itself: "Nothing is re-applied here: a second comparison against maxDecodedBytes would be one rule
/// with two owners." So that half of the claim is held two types away and not by this file, and a
/// reviewer who reads the case as covering both would skip a mutation that catches nothing.
/// </item>
/// <item>
/// "and the width stays an inequality" — the upper half is unreachable <em>by construction</em>, and
/// the honest version of that is a weaker risk than a disagreement between two rings.
/// <see cref="PasskeyPayloadLimits.WrappedPrivateKeyBytes" /> is not a second number that happens to
/// equal <see cref="WrappedAccountKeys.WrappedPrivateKeyLength" />; it is declared as it, in one line,
/// so the ceiling cannot drift away from the width. What the inequality is kept for is that the
/// derivation is a single line: replace it with a literal and widen that literal, and the over-long
/// envelope reaches the width again. That is a smaller risk than two rings disagreeing, and it is
/// stated as the smaller one — the first of those two edits changes no number and would be caught by
/// nothing, but the second parts the two constants and reddens
/// <see cref="WrappedPrivateKeyCeiling_IsTheDomainsOwnWidth" />, which is the one place that asserts
/// they agree.
/// </item>
/// <item>
/// "the leading byte is the AEAD framing's version" — the pair
/// <see cref="TryDecode_WithAnAllZeroEnvelopeOfTheRightWidth_Refuses" /> and
/// <see cref="TryDecode_WithTheVersionByteAloneSetOnAnOtherwiseEmptyEnvelope_Decodes" />. Both values
/// are the legal width and differ in one bit of one byte, which is what makes them proof that the
/// version check is a real check rather than the width check wearing another name.
/// </item>
/// <item>
/// "and it is the <em>other</em> suite's value that is refused, not merely a mis-sized buffer" —
/// <see cref="TryDecode_WithAWellFormedEncapsulatedValue_Refuses" />. That case is new with the split
/// and it is the one a single shared file could not have held.
/// </item>
/// <item>
/// "not standard base64's two extra characters" —
/// <see cref="TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses" />
/// against <see cref="TryDecode_WithAPaddedEncodingOfAWellFormedEnvelope_Decodes" />, which pins the
/// half of the alphabet rule that is easy to over-tighten. The claim is deliberately that narrow. The
/// refusing case substitutes <c>+</c> and <c>/</c>, so what it proves is that those two are refused —
/// not that the decoder accepts base64url and nothing else, which is a broader claim than any case
/// here supports. Measured, the decoder is looser than the broader claim would be:
/// <c>System.Buffers.Text.Base64Url.IsValid</c> skips whitespace wherever it appears, so
/// <c>AAAA AAAA</c>, a leading space, an embedded tab and a trailing newline all validate and decode to
/// the same bytes as <c>AAAAAAAA</c>. Nothing here covers that and no case is being added for it: the
/// statement that needs narrowing is the normative one, and it is being corrected where it lives.
/// </item>
/// </list>
/// </remarks>
public sealed class WrappedPrivateKeyEnvelopeTests
{
    /// <summary>
    /// A well-formed envelope decodes to exactly the bytes that were encoded.
    /// </summary>
    /// <remarks>
    /// The content is asserted, not the length, because the value handed back is a key nobody will try
    /// to unwrap until the day they need it: a decoder returning a fresh buffer of the right size, or
    /// the caller's text truncated to it, has to be caught here rather than by the person who needs the
    /// key. It is not the only case that would catch one —
    /// <see cref="TryDecode_WithAPaddedEncodingOfAWellFormedEnvelope_Decodes" /> and
    /// <see cref="TryDecode_WithTheVersionByteAloneSetOnAnOtherwiseEmptyEnvelope_Decodes" /> assert
    /// their own bytes too, so all three redden together on that mutation. The refusal cases carry the
    /// other half — each asserts the <c>out</c> parameter came back <see langword="null" /> — so between
    /// them what a refusal hands back and what an acceptance hands back are both pinned.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAWellFormedEnvelope_ReturnsExactlyThoseBytes()
    {
        // Arrange
        byte[] expected = Envelope(0xC0);

        // Act
        bool accepted =
            WrappedPrivateKeyEnvelope.TryDecode(Base64UrlText.Encode(expected), out byte[]? envelope);

        // Assert — CollectionOrdering.Matching, because TUnit's IsEquivalentTo defaults to
        // CollectionOrdering.Any and a permuted envelope is exactly what a buffer reuse would produce.
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The same envelope carrying base64's <c>=</c> padding is accepted.
    /// </summary>
    /// <remarks>
    /// Base64url omits the padding and <see cref="PasskeyEncoding" /> accepts it anyway, deliberately —
    /// its ceiling is computed on the padded form so that a client which pads is never refused for a
    /// value it legitimately encoded. This case sits exactly on that ceiling: 167 bytes is 223
    /// characters unpadded and 224 padded, and 224 is the whole allowance
    /// <c>PasskeyEncoding.MaxEncodedLength(167)</c> returns. An implementation that measured the
    /// allowance against the unpadded length would refuse this and nothing else.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAPaddedEncodingOfAWellFormedEnvelope_Decodes()
    {
        // Arrange
        byte[] expected = Envelope(0xC0);
        string padded = Base64UrlText.Encode(expected) + "=";

        // Act
        bool accepted = WrappedPrivateKeyEnvelope.TryDecode(padded, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// An absent member is refused rather than treated as an absent envelope.
    /// </summary>
    /// <remarks>
    /// The member is required on all three write paths, and a missing JSON property arrives here as
    /// <see langword="null" /> — the shape a client that has not implemented wrapping yet sends.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithNoValue_Refuses()
    {
        // Act
        bool accepted = WrappedPrivateKeyEnvelope.TryDecode(null, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// Empty text is refused.
    /// </summary>
    /// <remarks>
    /// Separate from the null case because it is a different mistake with the same answer: an unbound
    /// form control sends <c>""</c>, not <c>null</c>, and an empty buffer has no leading byte for the
    /// version check to read.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithEmptyText_Refuses()
    {
        // Act
        bool accepted = WrappedPrivateKeyEnvelope.TryDecode(string.Empty, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// Text that is not an encoding at all is refused.
    /// </summary>
    [Test]
    public async Task TryDecode_WithTextThatIsNotAnEncoding_Refuses()
    {
        // Act
        bool accepted =
            WrappedPrivateKeyEnvelope.TryDecode("not base64url at all!", out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// Standard base64's two extra characters are refused, even in text of the right length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>+</c> and <c>/</c> are what base64url replaces with <c>-</c> and <c>_</c>, and they are
    /// substituted into an otherwise perfect encoding rather than produced by re-encoding: a random
    /// value run through <c>Convert.ToBase64String</c> need contain neither character, so that
    /// arrangement would pass against a decoder with no alphabet check at all. The length is untouched,
    /// so the width cannot be what refuses this.
    /// </para>
    /// <para>
    /// <b>The indices are 4 and 5 because anything inside the leading group makes this case prove
    /// nothing.</b> Base64 carries three decoded bytes per four characters, so characters 0-3 hold
    /// bytes 0-2 — the version among them. Measured on this fixture: substitute at 0 and 1, and a
    /// decoder that skipped the alphabet check reads the text as standard base64, where <c>+</c> is 62
    /// and <c>/</c> is 63, and gets a leading byte of <b>251</b>. That value comes back refused for its
    /// version, this case stays green, and nothing about the alphabet has been tested. At 4 and 5 the
    /// damage lands in bytes 3-5, which are filler: the same decoder reads the right number of bytes
    /// leading with 1 and <em>accepts</em> the value, so a refusal has exactly one source left.
    /// <c>CiphertextEnvelopeTextTests</c> keeps its own substitution off the leading group for the same
    /// reason and argues it there.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses()
    {
        // Arrange
        char[] mangled = [.. Base64UrlText.Encode(Envelope(0xC0))];
        mangled[4] = '+';
        mangled[5] = '/';

        // Act
        bool accepted = WrappedPrivateKeyEnvelope.TryDecode(new string(mangled), out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// An envelope that is not exactly <see cref="WrappedAccountKeys.WrappedPrivateKeyLength" /> bytes
    /// is refused, from both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AES-GCM ciphertext is the length of its plaintext and the plaintext is one PKCS#8 P-256 private
    /// key, so the envelope has one legal size and both sides of it are wrong. Refused rather than
    /// padded or truncated: either repair stores a well-formed row holding an envelope whose tag cannot
    /// verify, and the account looks registered until somebody needs the keys.
    /// </para>
    /// <para>
    /// <b>The two cases no longer fail for the same reason, and the narrow one is what this width is
    /// for.</b> The shared decode step refuses anything over the ceiling it was handed, and here that
    /// ceiling and this width are the same 167 bytes, so the wider value is gone before the width is
    /// ever consulted. What no shared rule can see is the band beneath it: an envelope of 29 to 166
    /// bytes clears the AEAD format's floor, carries the right version byte, and is still not a wrapped
    /// private key.
    /// </para>
    /// <para>
    /// The upper side is unreachable by construction rather than by coincidence:
    /// <see cref="PasskeyPayloadLimits.WrappedPrivateKeyBytes" /> is declared as
    /// <see cref="WrappedAccountKeys.WrappedPrivateKeyLength" />, not set to the same value beside it,
    /// so the ceiling has no way to drift away from the width. The check stays an inequality against the
    /// width anyway, for a narrower reason than drift: that derivation is one line, and a ceiling
    /// written as a literal and then widened would hand the upper side back to this check. Narrowed to
    /// a lower bound of its own, the width would have nothing left to catch it with — which is why this
    /// case keeps asserting both sides while one of them proves nothing. The widening itself is not
    /// silent: it parts the two constants, and
    /// <see cref="WrappedPrivateKeyCeiling_IsTheDomainsOwnWidth" /> asserts they agree.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(WrappedAccountKeys.WrappedPrivateKeyLength - 1)]
    [Arguments(WrappedAccountKeys.WrappedPrivateKeyLength + 1)]
    public async Task TryDecode_WithAnEnvelopeOfTheWrongWidth_Refuses(int width)
    {
        // Arrange
        string misshapen = Base64UrlText.Encode(Envelope(0xC0, width));

        // Act
        bool accepted = WrappedPrivateKeyEnvelope.TryDecode(misshapen, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// A perfectly well-formed value of the <em>other</em> suite is refused here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case the split bought, and it could not have been written while one file covered
    /// both members.</b> An <c>EncapsulatedValueEnvelope</c> of the account's two keys is
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" /> bytes leading with
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysVersion" /> — which is the same byte this
    /// suite's version constant holds, so the <em>version</em> check passes it. Nothing in the bytes
    /// says which framing they are; the member they arrived in is the only discriminator. What refuses
    /// this value is the width, and the width alone.
    /// </para>
    /// <para>
    /// A client that sent its two payloads the wrong way round is what this is about, and until the two
    /// widths diverged nothing on this server could have noticed: both members were 61 bytes carrying
    /// the same version byte, so a transposition satisfied every rule at every layer and surfaced in a
    /// browser months later as an authentication failure naming nothing. The refusal is therefore a
    /// property of the numbers rather than of anybody's diligence, and a later change that made the two
    /// widths equal again would silently delete this guarantee while keeping this test green — which is
    /// why the case is written against the <em>other</em> suite's constants rather than against 158.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAWellFormedEncapsulatedValue_Refuses()
    {
        // Arrange — the other suite's exact width and the other suite's own version byte.
        byte[] otherSuite = new byte[WrappedAccountKeys.EncapsulatedAccountKeysLength];
        Array.Fill(otherSuite, (byte)0x1D);
        otherSuite[0] = WrappedAccountKeys.EncapsulatedAccountKeysVersion;

        // Act
        bool accepted = WrappedPrivateKeyEnvelope.TryDecode(
            Base64UrlText.Encode(otherSuite), out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// An envelope whose leading byte is not the one AEAD framing version defined today is refused,
    /// from both sides of it.
    /// </summary>
    /// <remarks>
    /// IFR-007 puts this on the server because the successor does not exist: version 2 is a client
    /// claiming a contract this deployment has never implemented, and version 0 is a field nobody set.
    /// Both are the legal width, so only a version check can tell either of them from an envelope this
    /// system can interpret. Both bounds are read off
    /// <see cref="WrappedAccountKeys.WrappedPrivateKeyVersion" /> and never off the encapsulation
    /// suite's constant beside it — the two hold the same number today, so a cross-read would render
    /// identical bytes and go on passing.
    /// </remarks>
    [Test]
    [Arguments((byte)(WrappedAccountKeys.WrappedPrivateKeyVersion - 1))]
    [Arguments((byte)(WrappedAccountKeys.WrappedPrivateKeyVersion + 1))]
    public async Task TryDecode_WithAnUnrecognisedFramingVersion_Refuses(byte version)
    {
        // Arrange
        byte[] misversioned = Envelope(0xC0);
        misversioned[0] = version;

        // Act
        bool accepted = WrappedPrivateKeyEnvelope.TryDecode(
            Base64UrlText.Encode(misversioned), out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// A buffer of the legal width holding nothing but zeros is refused, for its version byte.
    /// </summary>
    /// <remarks>
    /// Half of the pair that proves the version check exists, and the more valuable half: an all-zero
    /// buffer of the right size is what an uninitialised field, a zero-filled allocation and a client
    /// that has stubbed the member all send, and it satisfies the width rule exactly. Its twin below
    /// differs from it in one bit of one byte and must be accepted — a decoder that passed both, or
    /// refused both, would be measuring width and calling it version.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnAllZeroEnvelopeOfTheRightWidth_Refuses()
    {
        // Arrange
        byte[] unset = new byte[WrappedAccountKeys.WrappedPrivateKeyLength];

        // Act
        bool accepted =
            WrappedPrivateKeyEnvelope.TryDecode(Base64UrlText.Encode(unset), out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// The same buffer with only the version byte set is accepted.
    /// </summary>
    /// <remarks>
    /// The other half of the pair. Nothing at this layer inspects a nonce, a ciphertext or a tag —
    /// the server holds no value that could open the envelope — so an otherwise empty envelope
    /// carrying the right version is well-formed by every rule this type owns, and saying so is what
    /// keeps the refusal above attributable to the byte that was changed.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTheVersionByteAloneSetOnAnOtherwiseEmptyEnvelope_Decodes()
    {
        // Arrange
        byte[] expected = new byte[WrappedAccountKeys.WrappedPrivateKeyLength];
        expected[0] = WrappedAccountKeys.WrappedPrivateKeyVersion;

        // Act
        bool accepted =
            WrappedPrivateKeyEnvelope.TryDecode(Base64UrlText.Encode(expected), out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Text longer than the ceiling allows is refused, and the caller is told nothing but that.
    /// </summary>
    /// <remarks>
    /// The ceiling is judged before the alphabet is validated and before anything is allocated, which
    /// is the argument <see cref="PasskeyEncoding.TryDecode" /> makes for taking one at all: validation
    /// is a full pass over the text and decoding is a second plus a buffer the size of the result, so a
    /// decode performed first has already cost what the ceiling exists to refuse. All three call sites
    /// are authenticated, so this is not the anonymous leg — the ceiling is here because the member has
    /// a known size and there is no reason to read more of it than that.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextLongerThanTheCeiling_Refuses()
    {
        // Arrange
        string oversized =
            Base64UrlText.Encode(new byte[PasskeyPayloadLimits.WrappedPrivateKeyBytes * 2]);

        // Act
        bool accepted = WrappedPrivateKeyEnvelope.TryDecode(oversized, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// The ceiling is this suite's own domain width, not a number of its own and not the neighbour's.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="PasskeyPayloadLimits.CredentialIdBytes" /> already keeps: a member the wire
    /// accepted but the entity could never store is text decoded for nothing, and a separate number
    /// here would only be a way for the two to disagree. Pinned rather than left implicit because the
    /// derivation is invisible at every call site, where the ceiling reads as a limit somebody may
    /// tune. <b>What this catches is the two numbers parting, not the derivation being replaced.</b>
    /// Write <c>167</c> in place of the derivation and this stays green — the assertion is still true —
    /// and only widening that literal afterwards turns it red. Which is the right half to guard: the
    /// second edit is the one that changes what the API accepts, and it is also the one that hands the
    /// upper side of <see cref="TryDecode_WithAnEnvelopeOfTheWrongWidth_Refuses" /> something to do.
    /// <b>The inequality against the neighbour is the second half and it is new.</b> With two ceilings
    /// and two widths in one static class, a ceiling wired to the other suite's width would be a single
    /// well-typed token — and everything else in this file would go on passing, because 158 is below
    /// 167 and the width check behind it would answer every case the same way.
    /// </remarks>
    [Test]
    public async Task WrappedPrivateKeyCeiling_IsTheDomainsOwnWidth()
    {
        // Act, Assert
        await Assert.That(PasskeyPayloadLimits.WrappedPrivateKeyBytes)
            .IsEqualTo(WrappedAccountKeys.WrappedPrivateKeyLength);
        await Assert.That(PasskeyPayloadLimits.WrappedPrivateKeyBytes)
            .IsNotEqualTo(WrappedAccountKeys.EncapsulatedAccountKeysLength);
    }

    /// <summary>
    /// A well-formed wrapped private key: the AEAD framing's version byte, then filler.
    /// </summary>
    /// <remarks>
    /// The filler is not a nonce and not a ciphertext, and nothing at this layer inspects either. The
    /// width is a parameter only so the refusal cases can step one byte off the bound.
    /// </remarks>
    private static byte[] Envelope(byte filler, int width = WrappedAccountKeys.WrappedPrivateKeyLength)
    {
        byte[] envelope = new byte[width];
        Array.Fill(envelope, filler);

        if (width > 0)
        {
            envelope[0] = WrappedAccountKeys.WrappedPrivateKeyVersion;
        }

        return envelope;
    }
}
