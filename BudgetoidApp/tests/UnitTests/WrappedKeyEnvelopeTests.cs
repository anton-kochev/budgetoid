using Application.Passkeys;
using Domain.Users;
using TestSupport;

namespace UnitTests;

/// <summary>
/// The one decode-and-validate step both write paths that accept a wrapped account key share:
/// base64url text within a ceiling, exactly one legal width, and the one envelope version defined
/// today.
/// </summary>
/// <remarks>
/// <para>
/// <b>A <c>Try</c> shape, for the reason <see cref="PasskeyEncoding.TryDecode"/> gives for its own.</b>
/// Both call sites — passkey registration and recovery-code generation — already turn a malformed
/// member into a refusal sentence of their own, and those sentences differ, so nothing here throws and
/// nothing here reports <em>why</em>. What every refusal below asserts instead is that the
/// <c>out</c> parameter came back <see langword="null"/>: a caller that reads the buffer without
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
/// passed by a function that returns <see langword="false"/> unconditionally:
/// </para>
/// <list type="bullet">
/// <item>
/// "exactly 61 bytes, a width and not a cap" —
/// <see cref="TryDecode_WithAnEnvelopeOfTheWrongWidth_Refuses"/> asserts both sides, but only one of
/// them is covered here, and the split is measured rather than reasoned. Write the width so that it
/// admits an envelope that is too <em>short</em> and exactly one case reddens, the 60-byte one. Write
/// it so that it admits one that is too <em>long</em> and <b>nothing reddens at all</b>: the 62-byte
/// value is refused by the shared ceiling in <c>CiphertextEnvelopeText</c> before this type's width is
/// consulted, so that half of the claim is held by a neighbouring type and not by this file. A
/// reviewer who reads the case as covering both would skip a mutation that catches nothing.
/// </item>
/// <item>
/// "and the width stays an inequality" — the upper half is unreachable only while
/// <see cref="PasskeyPayloadLimits.WrappedKeyBytes"/> and
/// <see cref="WrappedAccountKeys.EnvelopeLength"/> hold the same number, which they do by agreement
/// across two rings rather than by construction. The day they part, the over-long envelope reaches the
/// width again — so the check is not narrowed to a lower bound of its own, and the case keeps
/// asserting the side that currently proves nothing.
/// </item>
/// <item>
/// "the leading byte is the version" — the pair
/// <see cref="TryDecode_WithAnAllZeroEnvelopeOfTheRightWidth_Refuses"/> and
/// <see cref="TryDecode_WithTheVersionByteAloneSetOnAnOtherwiseEmptyEnvelope_Decodes"/>. Both values
/// are the legal width and differ in one bit of one byte, which is what makes them proof that the
/// version check is a real check rather than the width check wearing another name.
/// </item>
/// <item>
/// "base64url, and only base64url" — <see cref="TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses"/>
/// against <see cref="TryDecode_WithAPaddedEncodingOfAWellFormedEnvelope_Decodes"/>, which pins the
/// half of the alphabet rule that is easy to over-tighten.
/// </item>
/// </list>
/// </remarks>
public sealed class WrappedKeyEnvelopeTests
{
    /// <summary>
    /// A well-formed envelope decodes to exactly the bytes that were encoded.
    /// </summary>
    /// <remarks>
    /// The content is asserted, not the length. A decoder that returned a fresh buffer of the right
    /// size — or the caller's text truncated to it — satisfies every other test in this file, and the
    /// value it hands back is a key nobody will try to unwrap until the day they need it.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAWellFormedEnvelope_ReturnsExactlyThoseBytes()
    {
        // Arrange
        byte[] expected = Envelope(0xC0);

        // Act
        bool accepted = WrappedKeyEnvelope.TryDecode(Base64UrlText.Encode(expected), out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected);
    }

    /// <summary>
    /// The same envelope carrying base64's <c>=</c> padding is accepted.
    /// </summary>
    /// <remarks>
    /// Base64url omits the padding and <see cref="PasskeyEncoding"/> accepts it anyway, deliberately —
    /// its ceiling is computed on the padded form so that a client which pads is never refused for a
    /// value it legitimately encoded. This case sits exactly on that ceiling: 61 bytes is 82 characters
    /// unpadded and 84 padded, and 84 is the whole allowance. An implementation that measured the
    /// allowance against the unpadded length would refuse this and nothing else.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithAPaddedEncodingOfAWellFormedEnvelope_Decodes()
    {
        // Arrange
        byte[] expected = Envelope(0xC0);
        string padded = Base64UrlText.Encode(expected) + "==";

        // Act
        bool accepted = WrappedKeyEnvelope.TryDecode(padded, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An absent member is refused rather than treated as an absent envelope.
    /// </summary>
    /// <remarks>
    /// The member is required on both write paths, and a missing JSON property arrives here as
    /// <see langword="null"/> — the shape a client that has not implemented wrapping yet sends.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithNoValue_Refuses()
    {
        // Act
        bool accepted = WrappedKeyEnvelope.TryDecode(null, out byte[]? envelope);

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
        bool accepted = WrappedKeyEnvelope.TryDecode(string.Empty, out byte[]? envelope);

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
        bool accepted = WrappedKeyEnvelope.TryDecode("not base64url at all!", out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// Standard base64's two extra characters are refused, even in text of the right length.
    /// </summary>
    /// <remarks>
    /// <c>+</c> and <c>/</c> are what base64url replaces with <c>-</c> and <c>_</c>, and they are
    /// substituted into an otherwise perfect encoding rather than produced by re-encoding: a random
    /// value run through <c>Convert.ToBase64String</c> need contain neither character, so that
    /// arrangement would pass against a decoder with no alphabet check at all. The length is untouched,
    /// which is what makes this a refusal for the alphabet and not for the width.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextOutsideTheBase64UrlAlphabet_Refuses()
    {
        // Arrange
        char[] mangled = [.. Base64UrlText.Encode(Envelope(0xC0))];
        mangled[0] = '+';
        mangled[1] = '/';

        // Act
        bool accepted = WrappedKeyEnvelope.TryDecode(new string(mangled), out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// An envelope that is not exactly <see cref="WrappedAccountKeys.EnvelopeLength"/> bytes is
    /// refused, from both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AES-GCM ciphertext is the length of its plaintext and the plaintext is a 32-byte key, so the
    /// envelope has one legal size and both sides of it are wrong. Refused rather than padded or
    /// truncated: either repair stores a well-formed row holding an envelope whose tag cannot verify,
    /// and the account looks registered until somebody needs the keys.
    /// </para>
    /// <para>
    /// <b>The two cases no longer fail for the same reason, and the narrow one is now what this width
    /// is for.</b> The shared decode step refuses anything over the ceiling it was handed, and here
    /// that ceiling and this width are the same 61 bytes, so the wider value is gone before the width
    /// is ever consulted. What no shared rule can see is the band beneath it: an envelope of 29 to 60
    /// bytes clears the format's floor, carries the right version byte, and is still not a wrapped key.
    /// </para>
    /// <para>
    /// The upper side is unreachable from this member only while
    /// <see cref="PasskeyPayloadLimits.WrappedKeyBytes"/> and
    /// <see cref="WrappedAccountKeys.EnvelopeLength"/> are the same number. Let those two part and it
    /// comes back, which is why the check stays an inequality against the width rather than a lower
    /// bound of its own, and why this case keeps asserting both sides.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(WrappedAccountKeys.EnvelopeLength - 1)]
    [Arguments(WrappedAccountKeys.EnvelopeLength + 1)]
    public async Task TryDecode_WithAnEnvelopeOfTheWrongWidth_Refuses(int width)
    {
        // Arrange
        string misshapen = Base64UrlText.Encode(Envelope(0xC0, width));

        // Act
        bool accepted = WrappedKeyEnvelope.TryDecode(misshapen, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// An envelope whose leading byte is not the one version defined today is refused, from both sides
    /// of it.
    /// </summary>
    /// <remarks>
    /// IFR-007 puts this on the server because the successor does not exist: version 2 is a client
    /// claiming a contract this deployment has never implemented, and version 0 is a field nobody set.
    /// Both are the legal width, so only a version check can tell either of them from an envelope this
    /// system can interpret.
    /// </remarks>
    [Test]
    [Arguments((byte)(WrappedAccountKeys.EnvelopeVersion - 1))]
    [Arguments((byte)(WrappedAccountKeys.EnvelopeVersion + 1))]
    public async Task TryDecode_WithAnUnrecognisedEnvelopeVersion_Refuses(byte version)
    {
        // Arrange
        byte[] misversioned = Envelope(0xC0);
        misversioned[0] = version;

        // Act
        bool accepted = WrappedKeyEnvelope.TryDecode(
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
        byte[] unset = new byte[WrappedAccountKeys.EnvelopeLength];

        // Act
        bool accepted = WrappedKeyEnvelope.TryDecode(Base64UrlText.Encode(unset), out byte[]? envelope);

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
        byte[] expected = new byte[WrappedAccountKeys.EnvelopeLength];
        expected[0] = WrappedAccountKeys.EnvelopeVersion;

        // Act
        bool accepted = WrappedKeyEnvelope.TryDecode(Base64UrlText.Encode(expected), out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(envelope).IsEquivalentTo(expected);
    }

    /// <summary>
    /// Text longer than the ceiling allows is refused, and the caller is told nothing but that.
    /// </summary>
    /// <remarks>
    /// The ceiling is judged before the alphabet is validated and before anything is allocated, which
    /// is the argument <see cref="PasskeyEncoding.TryDecode"/> makes for taking one at all: validation
    /// is a full pass over the text and decoding is a second plus a buffer the size of the result, so a
    /// decode performed first has already cost what the ceiling exists to refuse. Both call sites are
    /// authenticated, so this is not the anonymous leg — the ceiling is here because the member has a
    /// known size and there is no reason to read more of it than that.
    /// </remarks>
    [Test]
    public async Task TryDecode_WithTextLongerThanTheCeiling_Refuses()
    {
        // Arrange
        string oversized = Base64UrlText.Encode(new byte[PasskeyPayloadLimits.WrappedKeyBytes * 2]);

        // Act
        bool accepted = WrappedKeyEnvelope.TryDecode(oversized, out byte[]? envelope);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(envelope).IsNull();
    }

    /// <summary>
    /// The ceiling is the domain's envelope width, not a number of its own.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="PasskeyPayloadLimits.CredentialIdBytes"/> already keeps: a member the wire
    /// accepted but the entity could never store is text decoded for nothing, and a separate number
    /// here would only be a way for the two to disagree. Pinned rather than left implicit because the
    /// alias is invisible at every call site — a later edit to a bare literal reads as tuning a limit,
    /// and this is the only place that would notice.
    /// </remarks>
    [Test]
    public async Task WrappedKeyCeiling_IsTheDomainsEnvelopeWidth()
    {
        // Act, Assert
        await Assert.That(PasskeyPayloadLimits.WrappedKeyBytes)
            .IsEqualTo(WrappedAccountKeys.EnvelopeLength);
    }

    /// <summary>
    /// A well-formed envelope: the version byte the contract defines, then filler.
    /// </summary>
    /// <remarks>
    /// The filler is not a nonce and not a ciphertext, and nothing at this layer inspects either. The
    /// width is a parameter only so the refusal cases can step one byte off the bound.
    /// </remarks>
    private static byte[] Envelope(byte filler, int width = WrappedAccountKeys.EnvelopeLength)
    {
        byte[] envelope = new byte[width];
        Array.Fill(envelope, filler);

        if (width > 0)
        {
            envelope[0] = WrappedAccountKeys.EnvelopeVersion;
        }

        return envelope;
    }
}
