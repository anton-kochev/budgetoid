using Domain.Security;
using Domain.Users;

namespace UnitTests;

/// <summary>
/// The shape every AEAD envelope this system stores must have, judged with no width to hide behind:
/// <c>version(1) || nonce(12) || ciphertext || tag(16)</c>, and version <c>0x01</c> — AES-256-GCM,
/// 96-bit nonce, 128-bit tag — as the only version defined.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists as its own type at all.</b> The rule lives today inside
/// <see cref="Application.Passkeys.WrappedKeyEnvelope"/>, fused to a fixed 61-byte width, where the
/// version check only ever runs on a buffer that already measured exactly one legal size. Narrative
/// text is variable-length: the ciphertext is as long as whatever somebody typed, so the width says
/// nothing about the envelope and the version byte is the only thing left standing between this
/// server and bytes no version of this system can interpret. Every case below is written for that
/// reading — the version gate under its own light, not behind a width.
/// </para>
/// <para>
/// <b>Nothing here decrypts anything, and nothing here can.</b> The server holds no key that opens an
/// envelope, so what is checkable at this layer is the framing and only the framing: a leading byte it
/// recognises, and enough bytes for the fixed parts of the format to exist. A nonce of zeros and a tag
/// of zeros are well-formed by every rule this type owns, and saying so is what keeps each refusal
/// below attributable to the one thing that was changed.
/// </para>
/// <para>
/// <b>Where the numbers come from, which is not one answer.</b> Cases that exercise the bound being
/// <em>applied</em> read it from the production constant, so the format and its edge cannot drift
/// apart while staying green — the choice <c>WrappedKeyEnvelopeTests</c> makes for the same reason.
/// The three cases that pin a number <em>itself</em> restate the literal, the choice
/// <c>WrappedAccountKeysTests</c> makes: a test deriving the number from the same constant it is
/// checking agrees with any number the type later chooses, which is no pin at all. Each of the three
/// says so in its own remarks.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a refusal test with no accepting twin is passed by
/// a function that returns <see langword="false"/> unconditionally:
/// </para>
/// <list type="bullet">
/// <item>
/// "the leading byte is the version" — the pair
/// <see cref="IsWellFormed_WithAnAllZeroBufferOfALegalLength_Refuses"/> and
/// <see cref="IsWellFormed_WithTheShortestPossibleEnvelope_Accepts"/>. Both buffers are the same legal
/// length and differ in one bit of one byte, which is what makes them proof that the version check is
/// a real check rather than the length check wearing another name.
/// </item>
/// <item>
/// "at least a version, a nonce and a tag" —
/// <see cref="IsWellFormed_WithOneByteShorterThanTheMinimum_Refuses"/> against that same accepting
/// twin, which sits exactly on the bound, so a check written <c>&gt;</c> fails exactly one case rather
/// than none.
/// </item>
/// <item>
/// "the minimum is a floor, not a width" —
/// <see cref="IsWellFormed_WithAnEnvelopeWiderThanTheMinimum_Accepts"/>, the one case that separates
/// this type from the thing it is being extracted out of. Nothing else in this file can.
/// </item>
/// </list>
/// </remarks>
public sealed class CiphertextEnvelopeTests
{
    /// <summary>
    /// A leading byte that is not the one version defined today is refused, from both sides of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The riskiest rule in the format, and the one with the least holding it up today.</b> Version
    /// 2 is a client claiming a contract this deployment has never implemented; version 0 is a field
    /// nobody set — an all-zero buffer of a legal width is what an uninitialised field, a zero-filled
    /// allocation and a client that has stubbed the member all send. Both buffers below are a legal
    /// length, so nothing but a version check can tell either of them from an envelope this system can
    /// interpret.
    /// </para>
    /// <para>
    /// Stored either way, the symptom is silent and late: the row is well-formed, the account looks
    /// registered or the entry looks saved, and the bytes turn out to be uninterpretable on the day
    /// somebody needs them back. Refused here, the mistake costs a request.
    /// </para>
    /// <para>
    /// <b>The cases are a matrix, version against width, and the second axis is not padding.</b> A
    /// wrong version on a minimum-length buffer and a wrong version on a long one are two different
    /// code paths the moment the length rule stops being an equality: an implementation that read the
    /// leading byte only after measuring an exact width would refuse the short pair for the right
    /// reason and let the long pair through, which is a narrative entry stored under a version this
    /// deployment cannot read.
    /// </para>
    /// <para>
    /// The bound is read from <see cref="CiphertextEnvelope.Version"/> rather than written out, because
    /// this case is the version rule being <em>applied</em>: were the format to define a successor, a
    /// literal here would keep passing while testing a version the type no longer recognises.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments((byte)(CiphertextEnvelope.Version - 1), CiphertextEnvelope.MinimumLength)]
    [Arguments((byte)(CiphertextEnvelope.Version + 1), CiphertextEnvelope.MinimumLength)]
    [Arguments((byte)(CiphertextEnvelope.Version - 1), CiphertextEnvelope.MinimumLength + 137)]
    [Arguments((byte)(CiphertextEnvelope.Version + 1), CiphertextEnvelope.MinimumLength + 137)]
    public async Task IsWellFormed_WithAnUnrecognisedVersion_Refuses(byte version, int width)
    {
        // Arrange
        byte[] misversioned = Envelope(version, width);

        // Act
        bool wellFormed = CiphertextEnvelope.IsWellFormed(misversioned);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// A buffer of exactly <see cref="CiphertextEnvelope.MinimumLength"/> carrying the version byte is
    /// well-formed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The accepting twin the refusals need.</b> Every refusal in this file is satisfied by a method
    /// that returns <see langword="false"/> for everything; this case and
    /// <see cref="IsWellFormed_WithAnEnvelopeWiderThanTheMinimum_Accepts"/> are the two that are not,
    /// and this is the one that sits on the bound itself.
    /// </para>
    /// <para>
    /// It also states the bound's polarity. An empty plaintext is a legitimate value — a note with no
    /// text, a field somebody cleared — and AES-GCM ciphertext is exactly the length of its plaintext,
    /// so an empty plaintext seals to a version, a nonce and a tag and nothing else: exactly this
    /// length. That is why the rule is <c>&gt;=</c> and not <c>&gt;</c>, and why refusing the shortest
    /// envelope would refuse a value the format produces.
    /// </para>
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithTheShortestPossibleEnvelope_Accepts()
    {
        // Arrange
        byte[] shortest = Envelope(CiphertextEnvelope.Version, CiphertextEnvelope.MinimumLength);

        // Act
        bool wellFormed = CiphertextEnvelope.IsWellFormed(shortest);

        // Assert
        await Assert.That(wellFormed).IsTrue();
    }

    /// <summary>
    /// A buffer far wider than the minimum, carrying the version byte, is well-formed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case the whole extraction exists for, and the only one in this file that can fail against
    /// the most likely wrong implementation.</b> Write the length rule as
    /// <c>envelope.Length != MinimumLength</c> — an equality rather than a floor — and every other test
    /// here still passes: the shortest envelope is accepted, one byte short is refused, the all-zero
    /// buffer and the empty buffer are refused. Seven green tests over a type that has quietly kept the
    /// fixed width it was supposed to shed.
    /// </para>
    /// <para>
    /// That implementation is not hypothetical. It is the line standing in
    /// <see cref="Application.Passkeys.WrappedKeyEnvelope.TryDecode"/> today —
    /// <c>decoded.Length != WrappedAccountKeys.EnvelopeLength</c> — where it is correct, because a
    /// wrapped 32-byte key has exactly one legal size. Carried across into a format that also has to
    /// hold variable-length narrative text, it refuses every entry longer than an empty one, and the
    /// person finds out by not being able to save what they typed.
    /// </para>
    /// <para>
    /// The 137 is deliberate and arbitrary in equal measure: odd, and a multiple of neither 16 nor 32,
    /// so no arithmetic that happens to be block-aligned or key-sized admits it by luck.
    /// </para>
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithAnEnvelopeWiderThanTheMinimum_Accepts()
    {
        // Arrange
        byte[] wide = Envelope(CiphertextEnvelope.Version, CiphertextEnvelope.MinimumLength + 137);

        // Act
        bool wellFormed = CiphertextEnvelope.IsWellFormed(wide);

        // Assert
        await Assert.That(wellFormed).IsTrue();
    }

    /// <summary>
    /// A buffer one byte shorter than the minimum is refused, however well-formed its leading byte.
    /// </summary>
    /// <remarks>
    /// The other side of the bound the accepting twin sits on. Together the two pin it from both
    /// directions, so a check written <c>&gt;</c> reddens exactly this file and a check written
    /// <c>&gt;=</c> stays green — with one test each way, a wrong comparison has nowhere to hide. The
    /// buffer carries the version byte on purpose: the refusal has to be attributable to the length,
    /// and a short buffer whose leading byte were also wrong would be refused by either rule.
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithOneByteShorterThanTheMinimum_Refuses()
    {
        // Arrange
        byte[] truncated = Envelope(CiphertextEnvelope.Version, CiphertextEnvelope.MinimumLength - 1);

        // Act
        bool wellFormed = CiphertextEnvelope.IsWellFormed(truncated);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// A buffer of a legal length holding nothing but zeros is refused, for its version byte.
    /// </summary>
    /// <remarks>
    /// The discriminator between the two rules, and the reason it is written as its own case rather
    /// than folded into the parameterised version test: this buffer and the one
    /// <see cref="IsWellFormed_WithTheShortestPossibleEnvelope_Accepts"/> hands over are the same
    /// length and differ in one bit of one byte, so no length check can separate them and no
    /// implementation can pass both by accident. An implementation that measured width and called it a
    /// version check would accept this; one that refused everything would fail its twin.
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithAnAllZeroBufferOfALegalLength_Refuses()
    {
        // Arrange
        byte[] unset = new byte[CiphertextEnvelope.MinimumLength];

        // Act
        bool wellFormed = CiphertextEnvelope.IsWellFormed(unset);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// An empty buffer is refused rather than read.
    /// </summary>
    /// <remarks>
    /// Length has to be judged before version, and this is the case that says so: there is no leading
    /// byte here for a version check to look at, so an implementation that reached for
    /// <c>envelope[0]</c> first would not return <see langword="false"/> — it would throw, out of a
    /// method whose whole contract is to answer a question without one. An absent member and a
    /// zero-length field both arrive in exactly this shape.
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithAnEmptyBuffer_Refuses()
    {
        // Arrange
        byte[] empty = [];

        // Act
        bool wellFormed = CiphertextEnvelope.IsWellFormed(empty);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// The minimum is a version byte, a 96-bit nonce and a 128-bit tag, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The one place in this file where a literal is correct.</b> Everywhere else the bound is read
    /// from the constant, so the rule and its edge cannot drift apart; here the constant <em>is</em>
    /// the thing under test, and a test computing <c>MinimumLength</c> from
    /// <c>VersionBytes + NonceBytes + TagBytes</c> would agree with any three numbers the type later
    /// chose. So the arithmetic is written out — <c>1 + 12 + 16</c> — and each component is pinned on
    /// its own, because the sum alone is satisfied by a format that moved a byte from the nonce to the
    /// tag. The layout is the contract: these widths are what a client's AES-GCM implementation slices
    /// on, and a change to any of them is a change no other test in this repository would notice.
    /// </remarks>
    [Test]
    public async Task MinimumLength_IsAVersionANonceAndATag()
    {
        // Act, Assert
        await Assert.That(CiphertextEnvelope.VersionBytes).IsEqualTo(1);
        await Assert.That(CiphertextEnvelope.NonceBytes).IsEqualTo(12);
        await Assert.That(CiphertextEnvelope.TagBytes).IsEqualTo(16);
        await Assert.That(CiphertextEnvelope.MinimumLength).IsEqualTo(1 + 12 + 16);
    }

    /// <summary>
    /// The version this format is defined at is <c>1</c>, and the number is written out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own method rather than a fourth line in
    /// <see cref="MinimumLength_IsAVersionANonceAndATag"/>, because it is a different claim.</b> That
    /// test pins the <em>widths</em> the layout is cut into; this one pins the <em>value</em> of the
    /// byte that goes on the wire. Folded together, a failure would not say which of the two moved.
    /// </para>
    /// <para>
    /// <b>Nothing else in this file can hold this number, and that is measured rather than argued.</b>
    /// Every other case reads the version <em>relatively</em> — <c>Version - 1</c>, <c>Version + 1</c>,
    /// or a buffer carrying <c>Version</c> in its leading byte — so every one of them moves when the
    /// constant moves. Mutating <see cref="CiphertextEnvelope.Version"/> from 1 to 2 was run against
    /// the suite and failed nothing: the matrix simply began testing 1 and 3, the accepting cases began
    /// building buffers led by <c>0x02</c>, and the all-zero buffer stayed refused because 2 is not 0.
    /// A file wholly green over a format that is no longer the one it describes.
    /// </para>
    /// <para>
    /// <b>And the cost of that is not a red build.</b> This number is the contract with every client
    /// that will ever seal an envelope, and it is derived from nothing — no arithmetic produces it and
    /// no neighbouring definition would disagree with it. Changed, this deployment stops recognising
    /// the envelopes it is handed, and reads the ones already written under a rule they were not
    /// sealed with. So it is restated as a literal, for the reason the widths above are: a test that
    /// reads the constant it is checking agrees with whatever that constant becomes.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Version_IsTheOneVersionDefined()
    {
        // Act, Assert
        await Assert.That(CiphertextEnvelope.Version).IsEqualTo((byte)1);
    }

    /// <summary>
    /// The wrapped-key width is this minimum stretched over a 32-byte key, and it is 61.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>61 is written out deliberately, for a reason the other pin does not have: the number lives in
    /// three places and the code owns one of them.</b> The first is
    /// <see cref="WrappedAccountKeys.EnvelopeLength"/> itself, which
    /// <c>WrappedAccountKeysConfiguration</c> interpolates into two check constraints —
    /// <c>CK_wrapped_account_keys_wrapped_content_key_length</c> and its index-key twin. The second is
    /// the migration that created the table, where the same rule stands as the fixed text
    /// <c>length(wrapped_content_key) = 61</c>, which follows no constant. The third is
    /// <c>SchemaConstraintSnapshotTests</c>, which pins those constraints again as rendered text, 61
    /// included.
    /// </para>
    /// <para>
    /// So the constant is the only copy an edit here moves, and the other two would sit still and
    /// disagree with it. A test deriving its expectation from
    /// <see cref="WrappedAccountKeys.EnvelopeLength"/> would follow the edit without a word and leave
    /// that disagreement to be discovered by a schema comparison; written out, it says the width
    /// changed at the place the change was made.
    /// </para>
    /// <para>
    /// The relation is asserted beside it, and is the reason this case sits in this file rather than
    /// with the entity's own tests: pulling the version gate out of the wrapped-key path is only safe
    /// while the wrapped-key width is genuinely this format over a 32-byte plaintext. If it ever is not,
    /// the two are different formats sharing a leading byte, and that is worth finding out here.
    /// </para>
    /// </remarks>
    [Test]
    public async Task WrappedKeyWidth_IsTheMinimumOverA32ByteKey()
    {
        // Act, Assert
        await Assert.That(WrappedAccountKeys.EnvelopeLength).IsEqualTo(61);
        await Assert.That(WrappedAccountKeys.EnvelopeLength)
            .IsEqualTo(CiphertextEnvelope.MinimumLength + 32);
    }

    /// <summary>
    /// A buffer of <paramref name="width"/> bytes whose leading byte is <paramref name="version"/> and
    /// whose remainder is zeros.
    /// </summary>
    /// <remarks>
    /// The zeros are not a nonce and not a tag, and nothing at this layer inspects either — the server
    /// holds no value that could open the envelope. Leaving them zero is what makes the accepting case
    /// and the all-zero refusal differ in exactly one bit.
    /// </remarks>
    private static byte[] Envelope(byte version, int width)
    {
        byte[] envelope = new byte[width];

        if (width > 0)
        {
            envelope[0] = version;
        }

        return envelope;
    }
}
