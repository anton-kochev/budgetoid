using Domain.Security;

namespace UnitTests;

/// <summary>
/// The one type a narrative column accepts: an AEAD envelope this server has never seen the inside of,
/// judged for framing and for the caller's ceiling and for nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The strongest claim this type makes is one no test can assert, and saying so is the point.</b>
/// "No narrative value is ever server-readable" is held by the type having no constructor, no factory
/// and no conversion taking a <see cref="string"/> — writing plaintext into a narrative column does not
/// compile. A test cannot exercise a call that does not exist, so what pins that property is the
/// absence of a member, which reviews and the compiler hold between them. Everything below is the
/// smaller half: given bytes, what is accepted and what is refused.
/// </para>
/// <para>
/// <b>Refusals are <see cref="ArgumentException"/> and <see cref="ArgumentOutOfRangeException"/>, never
/// <see cref="Domain.Common.ValidationException"/>, and the assertions are exact about which.</b> The
/// entity factories beside this type use the validation exception because each of them owns a property
/// a refusal can be keyed on; this type is shared by eight columns across six entities and owns none of
/// them, so all eight would key under one word and the resulting 400 would name a member no request
/// carries. The exact-type assertions matter twice over, because
/// <see cref="ArgumentOutOfRangeException"/> <em>is</em> an <see cref="ArgumentException"/>: an
/// assignable match would let the ceiling refusal and the envelope refusal collapse into each other
/// with nothing red.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a refusal test with no accepting twin is passed by
/// a factory that throws unconditionally:
/// </para>
/// <list type="bullet">
/// <item>
/// "at least the fixed parts, and a floor rather than a width" —
/// <see cref="Sealed_WithAnEnvelopeOneByteShorterThanTheMinimum_Throws"/> against
/// <see cref="Sealed_WithTheShortestPossibleEnvelope_IsAccepted"/>, which sits exactly on the bound. It
/// is the case a reader will get wrong: an empty plaintext is a legitimate value — a note with no text,
/// a field somebody cleared — and AES-GCM ciphertext is exactly the length of its plaintext, so it
/// seals to a version, a nonce and a tag and nothing else. A rule written <c>&gt;</c> rather than
/// <c>&gt;=</c> refuses a field somebody cleared, and reddens there.
/// </item>
/// <item>
/// "and it is not a width in the other direction either" —
/// <see cref="Sealed_WithAnEnvelopeLongerThanTheMinimum_KeepsExactlyThoseBytes"/>, the only accepting
/// case here that a factory still measuring one exact width would fail.
/// </item>
/// <item>
/// "the leading byte is the version" — <see cref="Sealed_WithAnUnrecognisedVersion_Throws"/>, whose
/// two arguments sit either side of <see cref="CiphertextEnvelope.Version"/>. Version 0 is the more
/// valuable of the two, because an all-zero buffer of a legal length is what an uninitialised member, a
/// zero-filled allocation and a stubbed client all send; its accepting twin is the shortest-envelope
/// case above, which differs from it in one bit of one byte, so no length rule can separate them.
/// </item>
/// <item>
/// "at most <c>maxBytes</c>, and the argument is what decides it" —
/// <see cref="Sealed_WithAnEnvelopeOneByteOverTheCeiling_Throws"/> against
/// <see cref="Sealed_WithAnEnvelopeExactlyAtTheCeiling_IsAccepted"/>, both run at each of
/// <see cref="NarrativeFieldLimits"/>' two numbers. Running both caps is what catches a factory that
/// ignored its parameter and reached for a constant of its own: it would have to be wrong at one of the
/// two.
/// </item>
/// <item>
/// "absent is not empty" — <see cref="SealedOrAbsent_WithNoValue_AnswersNull"/> against
/// <see cref="SealedOrAbsent_WithASuppliedZeroLengthBuffer_Throws"/>. This is the pair the split
/// signature exists for, and it is the one place the difference is visible:
/// <c>default(ReadOnlyMemory&lt;byte&gt;)</c> is a non-null zero-length buffer, so a factory folding
/// the two together answers <see langword="null"/> for a value that <em>was</em> supplied and is
/// malformed.
/// </item>
/// <item>
/// "a copy, not a view" — <see cref="Sealed_CopiesTheEnvelopeRatherThanAliasingIt"/>, which nothing
/// else here can cover: every other case holds a buffer nobody touches afterwards, so a factory
/// assigning the caller's <see cref="ReadOnlyMemory{T}"/> straight through passes all of them.
/// </item>
/// </list>
/// <para>
/// <b>Every bound is read from the production constant that owns it.</b> This type applies numbers
/// <see cref="CiphertextEnvelope"/> and <see cref="NarrativeFieldLimits"/> declare and defines none, so
/// a literal here would let the field and the format drift apart while staying green. That is the
/// opposite of the choice <c>CiphertextEnvelopeTests</c> and <c>WrappedAccountKeysTests</c> make for
/// their own pinning cases, and the distinction is the one both of them record.
/// </para>
/// <para>
/// <b>Nothing here touches <c>FromStore</c>, and that is a limit of the arrangement rather than an
/// omission.</b> It is <see langword="internal"/> to <c>Domain</c> and the solution carries no
/// <c>InternalsVisibleTo</c>, so this project cannot call it. Its documented rule — that it does
/// <em>not</em> re-validate, because a validating read side makes a lowered cap retroactive and turns a
/// one-integer diff into data loss — is therefore held by review alone until an assembly is granted
/// access.
/// </para>
/// </remarks>
public sealed class NarrativeFieldTests
{
    /// <summary>
    /// An envelope longer than the minimum is kept as exactly the bytes that were handed over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case the type exists for, carrying two claims nothing else here carries.</b> The first is
    /// that the length rule is a floor. Write it as an equality against
    /// <see cref="CiphertextEnvelope.MinimumLength"/> — the shape
    /// <see cref="Application.Passkeys.WrappedKeyEnvelope"/> uses, correctly, over a key of one legal
    /// size — and every other accepting case here still passes, over a type that quietly refuses every
    /// entry longer than an empty one. The person finds out by not being able to save what they typed.
    /// </para>
    /// <para>
    /// The second is that the bytes kept are the bytes supplied. Content is asserted, not length: a
    /// factory holding a fresh buffer of the right size, the caller's bytes truncated to the floor, or
    /// the payload with its version byte already stripped satisfies every other case here, and what it
    /// stores is ciphertext nobody will try to open until the day they need it. The filler varies per
    /// position for the same reason — a uniform payload is one <c>Array.Fill</c> away from being
    /// reproduced by a factory that read nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Sealed_WithAnEnvelopeLongerThanTheMinimum_KeepsExactlyThoseBytes()
    {
        // Arrange
        byte[] expected = Envelope(CiphertextEnvelope.MinimumLength + 137);

        // Act
        NarrativeField field = NarrativeField.Sealed(expected, NarrativeFieldLimits.NameBytes);

        // Assert
        await Assert.That(field.Envelope.ToArray()).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An envelope of exactly <see cref="CiphertextEnvelope.MinimumLength"/> bytes is accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case a reader will get wrong, which is why it is written out rather than folded into the
    /// one above.</b> An empty plaintext is a legitimate value and AES-GCM ciphertext is exactly the
    /// length of its plaintext, so an empty plaintext seals to a version, a nonce and a tag: this many
    /// bytes and no more. A rule written <c>&gt; MinimumLength</c> reads like prudence and refuses a
    /// field somebody cleared, and the person finds out by not being able to clear it.
    /// </para>
    /// <para>
    /// It is also the accepting twin of the version pair. This buffer and the version-0 argument of
    /// <see cref="Sealed_WithAnUnrecognisedVersion_Throws"/> are the same length and differ in one bit
    /// of one byte, so no length rule can separate them and no factory can pass both by measuring width
    /// and calling it a version check. That is why the payload here is left at zero rather than filled.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Sealed_WithTheShortestPossibleEnvelope_IsAccepted()
    {
        // Arrange
        byte[] expected = new byte[CiphertextEnvelope.MinimumLength];
        expected[0] = CiphertextEnvelope.Version;

        // Act
        NarrativeField field = NarrativeField.Sealed(expected, NarrativeFieldLimits.NameBytes);

        // Assert
        await Assert.That(field.Envelope.ToArray()).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An envelope one byte shorter than <see cref="CiphertextEnvelope.MinimumLength"/> is refused.
    /// </summary>
    /// <remarks>
    /// The floor from the refusing side, one byte off the bound so that the pair with the case above
    /// pins its polarity rather than its existence. The leading byte is the version this deployment
    /// recognises and the ceiling comfortably admits the length, which is what makes the refusal
    /// attributable to the floor and to nothing else.
    /// </remarks>
    [Test]
    public async Task Sealed_WithAnEnvelopeOneByteShorterThanTheMinimum_Throws()
    {
        // Arrange
        byte[] truncated = Envelope(CiphertextEnvelope.MinimumLength - 1);

        // Act, Assert
        await Assert.That(() => NarrativeField.Sealed(truncated, NarrativeFieldLimits.NameBytes))
            .ThrowsExactly<ArgumentException>();
    }

    /// <summary>
    /// An envelope whose leading byte is not the one version defined today is refused, from both sides
    /// of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IFR-007 in the domain. Version 2 is a client claiming a contract this deployment has never
    /// implemented; version 0 is a field nobody set — an all-zero buffer of a legal length is what an
    /// uninitialised member, a zero-filled allocation and a stubbed client all send. Stored either way
    /// the symptom is silent and late: the row is well-formed and the bytes turn out to be
    /// uninterpretable on the day somebody needs them back.
    /// </para>
    /// <para>
    /// Both arguments are carried on a buffer of exactly
    /// <see cref="CiphertextEnvelope.MinimumLength"/> bytes, the same length
    /// <see cref="Sealed_WithTheShortestPossibleEnvelope_IsAccepted"/> hands over, so neither the floor
    /// nor the ceiling can be what refuses them.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments((byte)(CiphertextEnvelope.Version - 1))]
    [Arguments((byte)(CiphertextEnvelope.Version + 1))]
    public async Task Sealed_WithAnUnrecognisedVersion_Throws(byte version)
    {
        // Arrange
        byte[] misversioned = new byte[CiphertextEnvelope.MinimumLength];
        misversioned[0] = version;

        // Act, Assert
        await Assert.That(() => NarrativeField.Sealed(misversioned, NarrativeFieldLimits.NameBytes))
            .ThrowsExactly<ArgumentException>();
    }

    /// <summary>
    /// An envelope sitting exactly on the ceiling is accepted, at each of the two caps.
    /// </summary>
    /// <remarks>
    /// The ceiling's polarity, from the accepting side and at both of
    /// <see cref="NarrativeFieldLimits"/>' numbers. A factory written <c>&lt;</c> rather than
    /// <c>&lt;=</c> refuses a value the cap names as legal and reddens here; a factory that ignored its
    /// parameter and reached for one constant of its own is wrong at one of the two arguments, because
    /// the numbers differ. The cap is a bound on <em>envelope</em> bytes and not on characters — this
    /// side never sees a character — so the buffer handed over is the cap exactly.
    /// </remarks>
    [Test]
    [Arguments(NarrativeFieldLimits.NameBytes)]
    [Arguments(NarrativeFieldLimits.DescriptionBytes)]
    public async Task Sealed_WithAnEnvelopeExactlyAtTheCeiling_IsAccepted(int maxBytes)
    {
        // Arrange
        byte[] expected = Envelope(maxBytes);

        // Act
        NarrativeField field = NarrativeField.Sealed(expected, maxBytes);

        // Assert
        await Assert.That(field.Envelope.Length).IsEqualTo(maxBytes);
        await Assert.That(field.Envelope.ToArray()).IsEquivalentTo(expected);
    }

    /// <summary>
    /// An envelope one byte over the ceiling is refused, at each of the two caps.
    /// </summary>
    /// <remarks>
    /// The other side of the same bound. Paired with the accepting case at the identical width, the
    /// argument is provably what decides the answer rather than decoration — a factory holding its own
    /// constant refuses this at one cap and accepts it at the other. The envelope is well-formed in
    /// every other respect, a legal length leading with the version this deployment recognises, so
    /// neither framing rule can be what refuses it.
    /// </remarks>
    [Test]
    [Arguments(NarrativeFieldLimits.NameBytes)]
    [Arguments(NarrativeFieldLimits.DescriptionBytes)]
    public async Task Sealed_WithAnEnvelopeOneByteOverTheCeiling_Throws(int maxBytes)
    {
        // Arrange
        byte[] overlong = Envelope(maxBytes + 1);

        // Act, Assert
        await Assert.That(() => NarrativeField.Sealed(overlong, maxBytes))
            .ThrowsExactly<ArgumentException>();
    }

    /// <summary>
    /// A ceiling of zero or less is refused as an argument out of range, not as a malformed envelope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ceiling no envelope can satisfy is a caller that computed one wrongly, and the failure it
    /// would otherwise present as is every field in that column being refused — a symptom that reads
    /// like the client sealing badly and sends the next person to look at the browser. The envelope
    /// handed over is well-formed, so the ceiling is the only thing left to refuse it.
    /// </para>
    /// <para>
    /// <b>The exact type is the assertion, and an assignable match would not do.</b>
    /// <see cref="ArgumentOutOfRangeException"/> is an <see cref="ArgumentException"/>, so a check that
    /// accepted either would be satisfied by a factory that never told the two refusals apart — which
    /// is precisely the distinction being pinned: one says the value was wrong, the other says the rule
    /// was.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Sealed_WithANonPositiveCeiling_Throws(int maxBytes)
    {
        // Arrange
        byte[] envelope = Envelope(CiphertextEnvelope.MinimumLength);

        // Act, Assert
        await Assert.That(() => NarrativeField.Sealed(envelope, maxBytes))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The field copies the envelope, so a caller still holding the buffer cannot change what was
    /// judged.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="Domain.Users.WrappedAccountKeys.For"/> keeps for its own two envelopes.
    /// <see cref="ReadOnlyMemory{T}"/> is a window onto a buffer somebody else still owns, not a value:
    /// without a copy, the field and the caller's array are the same bytes, and a buffer reused for the
    /// next field rewrites an envelope that has already been accepted. Nothing downstream could notice
    /// — the result is a well-formed row holding ciphertext that will not open, months later.
    /// </remarks>
    [Test]
    public async Task Sealed_CopiesTheEnvelopeRatherThanAliasingIt()
    {
        // Arrange
        byte[] envelope = Envelope(CiphertextEnvelope.MinimumLength + 7);
        byte expected = envelope[^1];
        NarrativeField field = NarrativeField.Sealed(envelope, NarrativeFieldLimits.NameBytes);

        // Act
        envelope[^1] ^= 0xFF;

        // Assert
        await Assert.That(field.Envelope.Span[^1]).IsEqualTo(expected);
    }

    /// <summary>
    /// No value at all answers <see langword="null"/> rather than being judged.
    /// </summary>
    /// <remarks>
    /// Three of the eight narrative columns are nullable — the descriptions — and for those, no value
    /// is a legal state of the row rather than a malformed one. The question "is there one?" is
    /// answered by which member the caller named, before anything is measured.
    /// </remarks>
    [Test]
    public async Task SealedOrAbsent_WithNoValue_AnswersNull()
    {
        // Act
        NarrativeField? field = NarrativeField.SealedOrAbsent(null, NarrativeFieldLimits.DescriptionBytes);

        // Assert
        await Assert.That(field).IsNull();
    }

    /// <summary>
    /// A value that is present and well-formed is accepted, and kept as exactly those bytes.
    /// </summary>
    /// <remarks>
    /// The accepting control for the two refusals below, without which a member that answered
    /// <see langword="null"/> for everything, or threw for everything present, would pass them. Content
    /// is asserted for the reason the <see cref="NarrativeField.Sealed"/> cases give: a member that
    /// judged the value and then handed back a buffer of its own satisfies every other assertion in
    /// this file.
    /// </remarks>
    [Test]
    public async Task SealedOrAbsent_WithAWellFormedEnvelope_IsAccepted()
    {
        // Arrange
        byte[] expected = Envelope(CiphertextEnvelope.MinimumLength + 7);
        ReadOnlyMemory<byte> supplied = expected;

        // Act
        NarrativeField? field = NarrativeField.SealedOrAbsent(
            supplied, NarrativeFieldLimits.DescriptionBytes);

        // Assert
        await Assert.That(field).IsNotNull();
        await Assert.That(field!.Envelope.ToArray()).IsEquivalentTo(expected);
    }

    /// <summary>
    /// A value that is present and malformed is refused, not read as an absent one.
    /// </summary>
    /// <remarks>
    /// The member's whole risk in one case: an implementation that reached for "is there anything
    /// usable here?" rather than "did the caller supply a value?" would answer <see langword="null"/>
    /// here, and a description somebody wrote would vanish into a nullable column with no refusal
    /// anybody could act on. The buffer is one byte under the floor and carries the right version, so
    /// only the floor can refuse it.
    /// </remarks>
    [Test]
    public async Task SealedOrAbsent_WithASuppliedButMalformedValue_Throws()
    {
        // Arrange
        ReadOnlyMemory<byte> truncated = Envelope(CiphertextEnvelope.MinimumLength - 1);

        // Act, Assert
        await Assert.That(() => NarrativeField.SealedOrAbsent(
                truncated, NarrativeFieldLimits.DescriptionBytes))
            .ThrowsExactly<ArgumentException>();
    }

    /// <summary>
    /// A zero-length buffer that was supplied is refused, though absence is not.
    /// </summary>
    /// <remarks>
    /// <b>The pair with <see cref="SealedOrAbsent_WithNoValue_AnswersNull"/> is the reason this member
    /// is separate from <see cref="NarrativeField.Sealed"/>, and this is the half that catches the
    /// collapse.</b> <c>default(ReadOnlyMemory&lt;byte&gt;)</c> is a non-null, zero-length buffer —
    /// precisely what a caller that passed nothing hands over — so a factory testing emptiness rather
    /// than nullability answers <see langword="null"/> here and reads a supplied, malformed value as an
    /// absent one. It is written as an explicit empty array rather than <c>default</c> so that the
    /// caller's intent in the fixture is unambiguous: this value <em>was</em> supplied.
    /// </remarks>
    [Test]
    public async Task SealedOrAbsent_WithASuppliedZeroLengthBuffer_Throws()
    {
        // Arrange
        ReadOnlyMemory<byte> supplied = Array.Empty<byte>();

        // Act, Assert
        await Assert.That(() => NarrativeField.SealedOrAbsent(
                supplied, NarrativeFieldLimits.DescriptionBytes))
            .ThrowsExactly<ArgumentException>();
    }

    /// <summary>
    /// One buffer, sized between the two caps, is refused under the smaller and accepted under the
    /// larger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The relation between <see cref="NarrativeFieldLimits"/>' two numbers, asserted through the
    /// factory rather than over the constants themselves.</b> Written as a comparison of the two
    /// <see langword="const"/> fields it says the same thing and says it about nothing that ran — and
    /// the analyser is right to flag that: an assertion whose operands are both compile-time constants
    /// is decided by the compiler, and the type under test need not even be loaded for it to pass.
    /// Here the same bytes go in twice, the ceiling is the only thing that differs, and the answers
    /// differ, so what is pinned is the factory's behaviour.
    /// </para>
    /// <para>
    /// <b>It is also strictly stronger than the constant comparison it replaces.</b> That version could
    /// only catch the two caps being made equal. This one catches that — a factory told
    /// <see cref="NarrativeFieldLimits.DescriptionBytes"/> would refuse a
    /// <see cref="NarrativeFieldLimits.NameBytes"/>-plus-one buffer, and the second half reddens — and
    /// it additionally catches a factory that ignored its argument entirely and reached for a constant
    /// of its own, whichever of the two it picked: one number cannot both refuse and accept the same
    /// buffer. The parameterised ceiling cases above rely on the caps differing to cover that; this is
    /// the case that makes the reliance visible instead of silent.
    /// </para>
    /// <para>
    /// Neither number is restated. A test asserting <c>1024</c> would be a second owner of a product
    /// decision the domain already owns, and would redden on every legitimate tuning of it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Sealed_WithAnEnvelopeBetweenTheTwoCaps_IsDecidedByTheCeilingItWasGiven()
    {
        // Arrange
        byte[] envelope = Envelope(NarrativeFieldLimits.NameBytes + 1);

        // Act
        NarrativeField underTheLargerCap = NarrativeField.Sealed(
            envelope, NarrativeFieldLimits.DescriptionBytes);

        // Assert
        await Assert.That(underTheLargerCap.Envelope.Length)
            .IsEqualTo(NarrativeFieldLimits.NameBytes + 1);
        await Assert.That(() => NarrativeField.Sealed(envelope, NarrativeFieldLimits.NameBytes))
            .ThrowsExactly<ArgumentException>();
    }

    /// <summary>
    /// A well-formed envelope of <paramref name="width"/> bytes: the version byte, then filler that
    /// varies per position.
    /// </summary>
    /// <remarks>
    /// The filler is not a nonce and not a ciphertext, and nothing at this layer inspects either — the
    /// server holds no value that could open the envelope. It varies rather than repeating so that the
    /// content assertions cannot be satisfied by a buffer of the right size filled with one byte, and
    /// the multiplier is odd so no run of it is period-aligned to the format's 12- and 16-byte parts.
    /// </remarks>
    private static byte[] Envelope(int width)
    {
        byte[] envelope = new byte[width];

        for (int position = 1; position < width; position++)
        {
            envelope[position] = (byte)(position * 37 + 11);
        }

        if (width > 0)
        {
            envelope[0] = CiphertextEnvelope.Version;
        }

        return envelope;
    }
}
