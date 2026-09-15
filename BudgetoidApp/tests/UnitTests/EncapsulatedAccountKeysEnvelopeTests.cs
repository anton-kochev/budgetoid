using Application.Passkeys;
using Domain.Security;
using Domain.Users;
using TestSupport;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The decode-and-validate step the three write paths share for a factor's
/// <b>encapsulated account keys</b>: base64url text within a ceiling, exactly one legal width, and the
/// one <b>encapsulation</b> framing version defined today.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file per framing, and the split is the point rather than a tidy-up.</b> This file and
/// <see cref="WrappedPrivateKeyEnvelopeTests" /> replace a single suite that covered both members of a
/// pair, back when both were AEAD envelopes of one width carrying one version constant. They are not
/// that any more. The value this file is about is
/// <c>version(1) ‖ ephemeral public key(65) ‖ nonce(12) ‖ ciphertext ‖ tag(16)</c> over a 64-byte
/// plaintext holding the content key and the index key, at
/// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" /> bytes; the neighbour's is
/// <c>version(1) ‖ nonce(12) ‖ ciphertext ‖ tag(16)</c> over a private key, at
/// <see cref="WrappedAccountKeys.WrappedPrivateKeyLength" />. A file arguing about both would have to
/// say "the width" and "the version" without saying which, which is precisely the confusion the two
/// types exist to prevent.
/// </para>
/// <para>
/// <b>Three verbs, and this file is about one of them.</b> <em>Sealed under</em> a key over data,
/// <em>wrapped under</em> a key over another key, <em>encapsulated to</em> a public key. What arrives
/// in this member was encapsulated to a factor's public half, which is why the framing carries an
/// ephemeral point the other does not and why its floor is 65 bytes higher. Nothing here opens one: the
/// server holds no private key of any kind, so the ECDH this format names cannot be run on this side
/// and the questions answerable are the framing's, not the cryptography's.
/// </para>
/// <para>
/// <b>Every bound is read from the production constant that owns it, not restated</b>, for the reason
/// the neighbour gives: this type defines no number, it applies the domain's at the edge, so a literal
/// here would let the edge and the entity drift apart and stay green.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a refusal test with no accepting twin can be
/// passed by a function that returns <see langword="false" /> unconditionally:
/// </para>
/// <list type="bullet">
/// <item>
/// "exactly <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" /> bytes, a width and not a
/// cap" — <see cref="TryDecode_WithAnEncapsulatedValueOfTheWrongWidth_Refuses" /> asserts both sides
/// and, exactly as next door, only the short one is this type's to catch: the 159-byte value is refused
/// by the ceiling inside <see cref="PasskeyEncoding.TryDecode" /> before this width is consulted. The
/// band this width owns is 94 to 157 bytes — values that clear
/// <see cref="EncapsulatedValueEnvelope.MinimumLength" />, carry the right version, and are still not
/// an encapsulation of two account keys.
/// </item>
/// <item>
/// "and the floor beneath it is <em>this</em> suite's floor" —
/// <see cref="TryDecode_WithAValueBelowTheEncapsulationFloor_Refuses" />. The AEAD framing's floor is
/// 29 bytes and this one's is 94, so a value in between is well-formed by the neighbour's rules and
/// malformed by these. That case is what would redden if
/// <c>EncapsulatedValueEnvelopeText</c> were "simplified" to delegate to
/// <c>CiphertextEnvelope.IsWellFormed</c>, which is the reuse the format type warns about in its own
/// remarks — and which nothing else in either suite would notice, because at the exact width the two
/// checks agree.
/// </item>
/// <item>
/// "the leading byte is the encapsulation framing's version" — the pair
/// <see cref="TryDecode_WithAnAllZeroValueOfTheRightWidth_Refuses" /> and
/// <see cref="TryDecode_WithTheVersionByteAloneSetOnAnOtherwiseEmptyValue_Decodes" />. Both values are
/// the legal width and differ in one bit of one byte.
/// </item>
/// <item>
/// "and it is the <em>other</em> suite's value that is refused, not merely a mis-sized buffer" —
/// <see cref="TryDecode_WithAWellFormedWrappedPrivateKey_Refuses" />.
/// </item>
/// <item>
/// "not standard base64's two extra characters" —
/// <see cref="TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses" /> against
/// <see cref="TryDecode_WithAPaddedEncodingOfAWellFormedValue_Decodes" />. The claim is as narrow as
/// the neighbour's and the same measurement backs it: the substitution is off the leading group,
/// because inside it the damaged bytes are the version and the case would pass for the wrong reason.
/// </item>
/// </list>
/// </remarks>
public sealed class EncapsulatedAccountKeysEnvelopeTests
{
    /// <summary>
    /// A well-formed encapsulated value decodes to exactly the bytes that were encoded.
    /// </summary>
    /// <remarks>
    /// The content is asserted, not the length. What this member carries is the only copy of the
    /// account's two keys that factor can reach, so a decoder returning a fresh buffer of the right
    /// size, or the caller's text truncated to it, has to be caught here rather than by the person who
    /// needs the keys.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAWellFormedValue_ReturnsExactlyThoseBytes()
    {
        // Arrange
        byte[] expected = EncapsulatedValue(0x1D);

        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(
            Base64UrlText.Encode(expected), out byte[]? encapsulated);

        // Assert — CollectionOrdering.Matching, because TUnit's IsEquivalentTo defaults to
        // CollectionOrdering.Any and a permuted buffer is exactly what a reuse would produce.
        await Assert.That(accepted).IsTrue();
        await Assert.That(encapsulated).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The same value carrying base64's <c>=</c> padding is accepted.
    /// </summary>
    /// <remarks>
    /// Base64url omits the padding and <see cref="PasskeyEncoding" /> accepts it anyway, deliberately —
    /// its ceiling is computed on the padded form so that a client which pads is never refused for a
    /// value it legitimately encoded. This case sits exactly on that ceiling: 158 bytes is 211
    /// characters unpadded and 212 padded, and 212 is the whole allowance
    /// <c>PasskeyEncoding.MaxEncodedLength(158)</c> returns. An implementation that measured the
    /// allowance against the unpadded length would refuse this and nothing else.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAPaddedEncodingOfAWellFormedValue_Decodes()
    {
        // Arrange
        byte[] expected = EncapsulatedValue(0x1D);
        string padded = Base64UrlText.Encode(expected) + "=";

        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(padded, out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(encapsulated).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// An absent member is refused rather than treated as an absent value.
    /// </summary>
    /// <remarks>
    /// The member is required on all three write paths, and a missing JSON property arrives here as
    /// <see langword="null" /> — the shape a client written against the older contract sends, where the
    /// account keys were wrapped symmetrically and this member did not exist.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithNoValue_Refuses()
    {
        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(null, out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// Empty text is refused.
    /// </summary>
    /// <remarks>
    /// Separate from the null case because it is a different mistake with the same answer: an unbound
    /// form control sends <c>""</c>, not <c>null</c>, and an empty buffer has no leading byte for the
    /// version check to read — which is why the format type judges length before version and says so.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithEmptyText_Refuses()
    {
        // Act
        bool accepted =
            EncapsulatedAccountKeysEnvelope.TryDecode(string.Empty, out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// Text that is not an encoding at all is refused.
    /// </summary>
    [Test]
    public async Task TryDecode_WithTextThatIsNotAnEncoding_Refuses()
    {
        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(
            "not base64url at all!", out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// Standard base64's two extra characters are refused, even in text of the right length.
    /// </summary>
    /// <remarks>
    /// <c>+</c> and <c>/</c> are substituted into an otherwise perfect encoding rather than produced by
    /// re-encoding: a random value run through <c>Convert.ToBase64String</c> need contain neither
    /// character. The length is untouched, so the width cannot be what refuses this. The indices are 4
    /// and 5 rather than 0 and 1 for the reason the neighbour measures and records: inside the leading
    /// group the substitution changes the decoded version byte, and the value would come back refused
    /// for its version with the alphabet tested by nothing.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses()
    {
        // Arrange
        char[] mangled = [.. Base64UrlText.Encode(EncapsulatedValue(0x1D))];
        mangled[4] = '+';
        mangled[5] = '/';

        // Act
        bool accepted =
            EncapsulatedAccountKeysEnvelope.TryDecode(new string(mangled), out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// A value that is not exactly <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" /> bytes
    /// is refused, from both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AES-GCM ciphertext is the length of its plaintext, and the plaintext here is fixed at 64 bytes —
    /// two 32-byte account keys as one value, content key first — so the encapsulation has one legal
    /// size and both sides of it are wrong. Refused rather than padded or truncated: either repair
    /// stores a well-formed row whose tag cannot verify, and the account looks registered until
    /// somebody needs the keys.
    /// </para>
    /// <para>
    /// The upper side is unreachable by construction, exactly as next door:
    /// <see cref="PasskeyPayloadLimits.EncapsulatedAccountKeysBytes" /> is declared as the domain width
    /// rather than set beside it, so the ceiling refuses the wider value before this width is reached.
    /// The case keeps asserting both sides so that a ceiling later written as a literal and widened has
    /// something to fall back on. <see cref="EncapsulatedAccountKeysCeiling_IsTheDomainsOwnWidth" /> is
    /// what makes that parting visible.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(WrappedAccountKeys.EncapsulatedAccountKeysLength - 1)]
    [Arguments(WrappedAccountKeys.EncapsulatedAccountKeysLength + 1)]
    public async Task TryDecode_WithAnEncapsulatedValueOfTheWrongWidth_Refuses(int width)
    {
        // Arrange
        string misshapen = Base64UrlText.Encode(EncapsulatedValue(0x1D, width));

        // Act
        bool accepted =
            EncapsulatedAccountKeysEnvelope.TryDecode(misshapen, out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// A value below this suite's own floor is refused, including one that clears the neighbour's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two floors are 29 and 94, and the gap between them is what this case occupies.</b> A
    /// buffer of <see cref="CiphertextEnvelope.MinimumLength" /> bytes is a well-formed AEAD envelope by
    /// every rule that framing owns; it has no room for a 65-byte ephemeral point, so it is not an
    /// encapsulation of anything. The refusal comes from the exact-width check here rather than from the
    /// floor, because both are on the path — which is why the argument is written down: the floor's own
    /// contribution is invisible at this call site and is what would vanish if
    /// <c>EncapsulatedValueEnvelopeText</c> were rewritten to delegate to the neighbour's
    /// <c>IsWellFormed</c>, a change that reads as reuse and admits every buffer 65 bytes too short.
    /// </para>
    /// <para>
    /// The second row sits one byte under the encapsulation floor: 93 bytes has room for a version, a
    /// point and a nonce but not a tag, so no AEAD result of any length could occupy it.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(CiphertextEnvelope.MinimumLength)]
    [Arguments(EncapsulatedValueEnvelope.MinimumLength - 1)]
    [Arguments(EncapsulatedValueEnvelope.MinimumLength)]
    public async Task TryDecode_WithAValueBelowTheEncapsulationFloor_Refuses(int width)
    {
        // Arrange
        string tooShort = Base64UrlText.Encode(EncapsulatedValue(0x1D, width));

        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(tooShort, out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// A perfectly well-formed value of the <em>other</em> suite is refused here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case the split bought.</b> A wrapped private key is
    /// <see cref="WrappedAccountKeys.WrappedPrivateKeyLength" /> bytes leading with
    /// <see cref="WrappedAccountKeys.WrappedPrivateKeyVersion" /> — the same byte this suite's version
    /// constant holds, so the version check passes it, and the encapsulation floor passes it too since
    /// 167 is comfortably over 94. What refuses it is the width alone.
    /// </para>
    /// <para>
    /// A client that sent its two payloads the wrong way round is what this is about, and until the two
    /// widths diverged nothing on this server could have noticed. The refusal is therefore a property
    /// of the numbers rather than of anybody's diligence, which is why the case is written against the
    /// <em>other</em> suite's constants rather than against 167: make the two widths equal again and
    /// this reddens, instead of quietly becoming a test of nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAWellFormedWrappedPrivateKey_Refuses()
    {
        // Arrange — the other suite's exact width and the other suite's own version byte.
        byte[] otherSuite = new byte[WrappedAccountKeys.WrappedPrivateKeyLength];
        Array.Fill(otherSuite, (byte)0xC0);
        otherSuite[0] = WrappedAccountKeys.WrappedPrivateKeyVersion;

        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(
            Base64UrlText.Encode(otherSuite), out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// A value whose leading byte is not the one encapsulation framing version defined today is refused,
    /// from both sides of it.
    /// </summary>
    /// <remarks>
    /// Version 2 is a client claiming a suite this deployment has never implemented; version 0 is a
    /// field nobody set. Both are the legal width, so only a version check can tell either of them from
    /// a value this system can interpret. Both bounds are read off
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysVersion" /> and never off the AEAD suite's
    /// constant beside it — the two hold the same number today, so a cross-read would render identical
    /// bytes and go on passing, which is exactly the mistake
    /// <c>EncapsulatedValueEnvelope.Version</c>'s own remarks say only prose can hold.
    /// </remarks>
    [Test]
    [Arguments((byte)(WrappedAccountKeys.EncapsulatedAccountKeysVersion - 1))]
    [Arguments((byte)(WrappedAccountKeys.EncapsulatedAccountKeysVersion + 1))]
    public async Task TryDecode_WithAnUnrecognisedFramingVersion_Refuses(byte version)
    {
        // Arrange
        byte[] misversioned = EncapsulatedValue(0x1D);
        misversioned[0] = version;

        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(
            Base64UrlText.Encode(misversioned), out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// A buffer of the legal width holding nothing but zeros is refused, for its version byte.
    /// </summary>
    /// <remarks>
    /// Half of the pair that proves the version check exists, and the more valuable half: an all-zero
    /// buffer of the right size is what an uninitialised field, a zero-filled allocation and a client
    /// that has stubbed the member all send, and it satisfies the width rule exactly. Its twin below
    /// differs from it in one bit of one byte and must be accepted.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAnAllZeroValueOfTheRightWidth_Refuses()
    {
        // Arrange
        byte[] unset = new byte[WrappedAccountKeys.EncapsulatedAccountKeysLength];

        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(
            Base64UrlText.Encode(unset), out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// The same buffer with only the version byte set is accepted.
    /// </summary>
    /// <remarks>
    /// The other half of the pair, and it is a stronger statement of what this layer cannot check than
    /// the neighbour's twin is. Sixty-five zero bytes are <b>not</b> a point on P-256, and this value is
    /// accepted anyway: answering otherwise would need the curve arithmetic the server has no key and no
    /// reason to run. The format type says so in its own remarks; this is that sentence executed.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTheVersionByteAloneSetOnAnOtherwiseEmptyValue_Decodes()
    {
        // Arrange
        byte[] expected = new byte[WrappedAccountKeys.EncapsulatedAccountKeysLength];
        expected[0] = WrappedAccountKeys.EncapsulatedAccountKeysVersion;

        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(
            Base64UrlText.Encode(expected), out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(encapsulated).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Text longer than the ceiling allows is refused, and the caller is told nothing but that.
    /// </summary>
    /// <remarks>
    /// The ceiling is judged before the alphabet is validated and before anything is allocated, which is
    /// the argument <see cref="PasskeyEncoding.TryDecode" /> makes for taking one at all.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextLongerThanTheCeiling_Refuses()
    {
        // Arrange
        string oversized =
            Base64UrlText.Encode(new byte[PasskeyPayloadLimits.EncapsulatedAccountKeysBytes * 2]);

        // Act
        bool accepted = EncapsulatedAccountKeysEnvelope.TryDecode(oversized, out byte[]? encapsulated);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(encapsulated).IsNull();
    }

    /// <summary>
    /// The ceiling is this suite's own domain width, not a number of its own and not the neighbour's.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="PasskeyPayloadLimits.CredentialIdBytes" /> already keeps: a member the wire
    /// accepted but the entity could never store is text decoded for nothing. <b>The inequality against
    /// the neighbour is the half that is new with the split.</b> With two ceilings and two widths in one
    /// static class, a ceiling wired to the other suite's width is a single well-typed token — and this
    /// is the one direction where that mistake is not caught by anything else in the file, because 167
    /// is <em>above</em> 158: every value this suite legitimately accepts would still clear the widened
    /// ceiling, and the exact-width check behind it would answer every case here the same way.
    /// </remarks>
    [Test]
    public async Task EncapsulatedAccountKeysCeiling_IsTheDomainsOwnWidth()
    {
        // Act, Assert
        await Assert.That(PasskeyPayloadLimits.EncapsulatedAccountKeysBytes)
            .IsEqualTo(WrappedAccountKeys.EncapsulatedAccountKeysLength);
        await Assert.That(PasskeyPayloadLimits.EncapsulatedAccountKeysBytes)
            .IsNotEqualTo(WrappedAccountKeys.WrappedPrivateKeyLength);
    }

    /// <summary>
    /// A well-formed encapsulated value: the encapsulation framing's version byte, then filler.
    /// </summary>
    /// <remarks>
    /// The filler is not an ephemeral point, not a nonce and not a ciphertext, and nothing at this layer
    /// inspects any of them. The width is a parameter only so the refusal cases can step off the bound.
    /// </remarks>
    private static byte[] EncapsulatedValue(
        byte filler,
        int width = WrappedAccountKeys.EncapsulatedAccountKeysLength)
    {
        byte[] value = new byte[width];
        Array.Fill(value, filler);

        if (width > 0)
        {
            value[0] = WrappedAccountKeys.EncapsulatedAccountKeysVersion;
        }

        return value;
    }
}
