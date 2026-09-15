using Domain.Security;

namespace UnitTests;

/// <summary>
/// The framing every value encapsulated to a recovery factor's public key carries:
/// <c>version(1) || ephemeral public key(65) || nonce(12) || ciphertext || tag(16)</c>, with
/// <c>0x01</c> — ECDH over NIST P-256, HKDF-SHA-256, then AES-256-GCM with a 96-bit nonce and a
/// 128-bit tag — as the only version defined.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a second type and not a wider <see cref="CiphertextEnvelope"/>.</b> The two framings
/// are not the same layout at different lengths: the 65-byte ephemeral point sits <em>between</em> the
/// version byte and the nonce, so a reader slicing encapsulated bytes by the AEAD framing takes the first
/// twelve bytes of a P-256 point for a nonce and the rest of it for ciphertext. There is no byte in
/// either format that says which of the two it is — the leading byte of both is <c>0x01</c> — so the
/// column the bytes were read from is the only thing that distinguishes them, and each format needs a
/// floor of its own. <c>EnvelopeSuiteCensusTests</c> is what stops the two version constants being
/// "de-duplicated" into one on the grounds that they hold the same number.
/// </para>
/// <para>
/// <b>Nothing here decrypts anything, and nothing here can.</b> Opening a value in this framing takes
/// the private half of the key it was encapsulated to, and the server holds no private key of any kind
/// — nothing is typed for one and no route accepts one — so it can run neither the ECDH this format
/// names nor the AES-GCM after it. What is checkable at this layer is the framing and
/// only the framing: a leading byte this deployment recognises, and enough bytes for the fixed parts to
/// exist. An ephemeral "public key" of 65 zeros is not a point on P-256 and is well-formed by every
/// rule this type owns — saying so is what keeps each refusal below attributable to the one thing that
/// was changed.
/// </para>
/// <para>
/// <b>Where the numbers come from, which is not one answer.</b> Cases that exercise a bound being
/// <em>applied</em> read it from the production constant, so the format and its edge cannot drift apart
/// while staying green. The two cases that pin a number <em>itself</em> restate the literal, the choice
/// <c>CiphertextEnvelopeTests</c> makes for the same reason: a test deriving the number from the same
/// constant it is checking agrees with any number the type later chooses, which is no pin at all. That
/// is sharper here than next door, because three of the five widths are declared as aliases of
/// <see cref="CiphertextEnvelope"/>'s — so a literal is the only form in which this file notices an
/// alias whose target moved.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a refusal test with no accepting twin is passed by
/// a function that returns <see langword="false"/> unconditionally:
/// </para>
/// <list type="bullet">
/// <item>
/// "the leading byte is the version" — the pair
/// <see cref="IsWellFormed_WithAnAllZeroBufferOfALegalLength_Refuses"/> and
/// <see cref="IsWellFormed_WithTheShortestPossibleEncapsulatedValue_Accepts"/>. Both buffers are the same
/// legal length and differ in one bit of one byte, which is what makes them proof that the version
/// check is a real check rather than the length check wearing another name.
/// </item>
/// <item>
/// "at least a version, a point, a nonce and a tag" —
/// <see cref="IsWellFormed_WithOneByteShorterThanTheMinimum_Refuses"/> against that same accepting
/// twin, which sits exactly on the bound, so a floor written <c>&gt;</c> fails exactly one case rather
/// than none.
/// </item>
/// <item>
/// "this floor, and not the AEAD framing's" —
/// <see cref="IsWellFormed_WithABufferLongEnoughForTheAeadFramingAlone_Refuses"/>, which is
/// <em>not</em> the only case that catches a body delegating to
/// <see cref="CiphertextEnvelope.IsWellFormed"/>: the one-byte-short case above catches it too, and
/// catches every floor at or below 93 while this one catches every floor at or below 29. It is kept
/// because the two failures read differently. A refused buffer of 93 bytes reads as an off-by-one; a
/// refused buffer of exactly <see cref="CiphertextEnvelope.MinimumLength"/> names the wrong
/// implementation it came from.
/// </item>
/// <item>
/// "the minimum is a floor, not a width" —
/// <see cref="IsWellFormed_WithAnEncapsulatedValueWiderThanTheMinimum_Accepts"/>. An encapsulated
/// value carries a
/// plaintext — a content key, an index key, whatever a later factor is handed — so an implementation
/// comparing the length for equality refuses every one of them, and nothing else here would say so.
/// </item>
/// </list>
/// </remarks>
public sealed class EncapsulatedValueEnvelopeTests
{
    /// <summary>
    /// A leading byte that is not the one version defined today is refused, from both sides of it and
    /// at both a minimum and a wide length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Version 2 is a client claiming a suite this deployment has never implemented; version 0 is a
    /// field nobody set — an all-zero buffer of a legal width is what an uninitialised member, a
    /// zero-filled allocation and a stubbed client all send. Both buffers are a legal length, so
    /// nothing but a version check can tell either of them from an encapsulated value this system can
    /// interpret.
    /// </para>
    /// <para>
    /// <b>The cases are a matrix, version against width, and the second axis is not padding.</b> A
    /// wrong version on a minimum-length buffer and a wrong version on a long one are two different
    /// code paths the moment the length rule stops being an equality: an implementation that read the
    /// leading byte only after measuring an exact width would refuse the short pair for the right
    /// reason and let the long pair through — a value carrying a version this deployment cannot run.
    /// </para>
    /// <para>
    /// The version is read from <see cref="EncapsulatedValueEnvelope.Version"/> rather than written out,
    /// because this case is the version rule being <em>applied</em>: were the format to define a
    /// successor, a literal here would keep passing while testing a version the type no longer
    /// recognises.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments((byte)(EncapsulatedValueEnvelope.Version - 1), EncapsulatedValueEnvelope.MinimumLength)]
    [Arguments((byte)(EncapsulatedValueEnvelope.Version + 1), EncapsulatedValueEnvelope.MinimumLength)]
    [Arguments((byte)(EncapsulatedValueEnvelope.Version - 1), EncapsulatedValueEnvelope.MinimumLength + 137)]
    [Arguments((byte)(EncapsulatedValueEnvelope.Version + 1), EncapsulatedValueEnvelope.MinimumLength + 137)]
    public async Task IsWellFormed_WithAnUnrecognisedVersion_Refuses(byte version, int width)
    {
        // Arrange
        byte[] misversioned = EncapsulatedValue(version, width);

        // Act
        bool wellFormed = EncapsulatedValueEnvelope.IsWellFormed(misversioned);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// A buffer of exactly <see cref="EncapsulatedValueEnvelope.MinimumLength"/> carrying the version byte is
    /// well-formed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The accepting twin the refusals need.</b> Every refusal in this file is satisfied by a method
    /// that returns <see langword="false"/> for everything; this case and
    /// <see cref="IsWellFormed_WithAnEncapsulatedValueWiderThanTheMinimum_Accepts"/> are the two that
    /// are not, and this is the one that sits on the bound itself.
    /// </para>
    /// <para>
    /// It also states the bound's polarity. AES-GCM ciphertext is exactly the length of its plaintext,
    /// so an empty plaintext produces a version, an ephemeral point, a nonce and a tag and nothing
    /// else: exactly this length. That is why the rule is <c>&gt;=</c> and not <c>&gt;</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithTheShortestPossibleEncapsulatedValue_Accepts()
    {
        // Arrange
        byte[] shortest = EncapsulatedValue(
            EncapsulatedValueEnvelope.Version, EncapsulatedValueEnvelope.MinimumLength);

        // Act
        bool wellFormed = EncapsulatedValueEnvelope.IsWellFormed(shortest);

        // Assert
        await Assert.That(wellFormed).IsTrue();
    }

    /// <summary>
    /// A buffer far wider than the minimum, carrying the version byte, is well-formed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case that separates a floor from a width.</b> Write the length rule as
    /// <c>value.Length != MinimumLength</c> and every other test here still passes: the shortest
    /// encapsulated value is accepted, one byte short is refused, the all-zero buffer and the empty
    /// are refused. A green file over a type that admits exactly one plaintext length.
    /// </para>
    /// <para>
    /// That implementation is not hypothetical — it is the shape
    /// <see cref="Application.Passkeys.WrappedPrivateKeyEnvelope"/> uses, correctly, over a wrapped key of one
    /// legal size. This format has to carry whatever plaintext a factor is handed, so the equality is
    /// the edit to expect and this is the case that catches it.
    /// </para>
    /// <para>
    /// The 137 is deliberate and arbitrary in equal measure: odd, and a multiple of neither 16 nor 32
    /// nor 65, so no arithmetic that happens to be block-aligned, key-sized or point-sized admits it by
    /// luck.
    /// </para>
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithAnEncapsulatedValueWiderThanTheMinimum_Accepts()
    {
        // Arrange
        byte[] wide = EncapsulatedValue(
            EncapsulatedValueEnvelope.Version, EncapsulatedValueEnvelope.MinimumLength + 137);

        // Act
        bool wellFormed = EncapsulatedValueEnvelope.IsWellFormed(wide);

        // Assert
        await Assert.That(wellFormed).IsTrue();
    }

    /// <summary>
    /// A buffer far wider than anything this product will ever encapsulate is still well-formed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case that catches an undeclared ceiling, measured rather than imagined.</b> The code agent
    /// mutated <see cref="EncapsulatedValueEnvelope.IsWellFormed"/> to
    /// <c>value.Length &gt;= MinimumLength &amp;&amp; value.Length &lt;= MinimumLength + 512 &amp;&amp;
    /// value[0] == Version</c> and the whole unit suite stayed green at 1062 of 1062. Every accepting
    /// case in this file hands over a buffer of at most
    /// <see cref="EncapsulatedValueEnvelope.MinimumLength"/> + 137, so a cap anywhere above about 231 bytes
    /// was invisible to the file — including every cap a person would actually write, since nobody caps
    /// a buffer at 231.
    /// </para>
    /// <para>
    /// <b>Why an undeclared ceiling is the specific way "a floor, not a width" dies.</b> An equality on
    /// the width — the mutation
    /// <see cref="IsWellFormed_WithAnEncapsulatedValueWiderThanTheMinimum_Accepts"/> covers — is loud: it
    /// refuses every non-empty plaintext, so the first person to encapsulate anything finds out. A
    /// generous
    /// cap is silent. It admits every value anybody tests with, and refuses the one that arrives later
    /// and is bigger than whoever wrote the cap imagined — which is a value that was encapsulated
    /// correctly,
    /// stored nowhere, and reported as malformed. The format states no maximum for a reason the type
    /// argues at length: how wide a plaintext may be belongs to the entity storing it, and folding a
    /// cap in here would tie the format to today's one consumer.
    /// </para>
    /// <para>
    /// <b>64 KiB is chosen to clear every number a cap could plausibly be written at</b> — the two
    /// narrative limits this repository already defines
    /// (<see cref="NarrativeFieldLimits.NameBytes"/> at 1024 and
    /// <see cref="NarrativeFieldLimits.DescriptionBytes"/> at 2560), the wrapped-key width, and every
    /// power of two up to 32768 that a reader reaching for "a sensible maximum" would land on.
    /// </para>
    /// <para>
    /// <b>What it cannot do, stated so nobody reads it as more than it is.</b> No test can prove the
    /// absence of a bound; this one proves only that any bound sits above 65,630 bytes. A cap written
    /// at a megabyte survives it. That is a real limit and the answer to it is not a wider buffer —
    /// it is that a cap at a megabyte is not an edit anybody makes by accident, where a cap at 512
    /// bytes is exactly what "let's be defensive about input" produces.
    /// </para>
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithAnEncapsulatedValueFarWiderThanAnyPlausibleCeiling_Accepts()
    {
        // Arrange
        byte[] enormous = EncapsulatedValue(
            EncapsulatedValueEnvelope.Version, EncapsulatedValueEnvelope.MinimumLength + (64 * 1024));

        // Act
        bool wellFormed = EncapsulatedValueEnvelope.IsWellFormed(enormous);

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
        byte[] truncated = EncapsulatedValue(
            EncapsulatedValueEnvelope.Version, EncapsulatedValueEnvelope.MinimumLength - 1);

        // Act
        bool wellFormed = EncapsulatedValueEnvelope.IsWellFormed(truncated);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// A buffer long enough to be a well-formed AEAD envelope, and too short to be an encapsulated
    /// value, is
    /// refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case that names the wrong implementation instead of merely failing it.</b> The two suites
    /// share a version byte and three of their five widths, so a body written as
    /// <c>CiphertextEnvelope.IsWellFormed(value)</c> — a shorter line than the correct one, which reads
    /// as reuse rather than as a mistake — is the likeliest way this method goes wrong. This buffer is
    /// exactly <see cref="CiphertextEnvelope.MinimumLength"/> bytes led by <c>0x01</c>: a well-formed
    /// AEAD envelope, and 65 bytes short of the shortest encapsulated value there can be.
    /// </para>
    /// <para>
    /// <b>It is not the only case that catches that delegation, and claiming so would be wrong.</b>
    /// <see cref="IsWellFormed_WithOneByteShorterThanTheMinimum_Refuses"/> hands over 93 bytes, which
    /// the sibling's floor of 29 also admits, so it reddens under the same implementation — and under
    /// every floor at or below 93, where this case only reaches those at or below 29. What this case
    /// adds is not coverage but attribution: 93 refused reads as an off-by-one and sends a reader to
    /// the comparison operator, where a buffer of exactly the sibling's minimum sends them to the
    /// sibling.
    /// </para>
    /// <para>
    /// What gets through if this is relaxed is not a refusal that never happened. It is a stored value
    /// whose first 12 bytes will be read as a nonce by something that needed them to be the tail of an
    /// ephemeral point, on the day somebody rotates a key.
    /// </para>
    /// <para>
    /// The bound is read from both constants rather than written out, because this case is the relation
    /// between the two formats being applied, not either number being pinned. The pinning cases below
    /// are what hold the numbers.
    /// </para>
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithABufferLongEnoughForTheAeadFramingAlone_Refuses()
    {
        // Arrange
        byte[] aeadSized = EncapsulatedValue(
            EncapsulatedValueEnvelope.Version, CiphertextEnvelope.MinimumLength);

        // Act
        bool wellFormed = EncapsulatedValueEnvelope.IsWellFormed(aeadSized);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// A buffer of a legal length holding nothing but zeros is refused, for its version byte.
    /// </summary>
    /// <remarks>
    /// The discriminator between the two rules, and the reason it is written as its own case rather
    /// than folded into the parameterised version test: this buffer and the one
    /// <see cref="IsWellFormed_WithTheShortestPossibleEncapsulatedValue_Accepts"/> hands over are the same
    /// length and differ in one bit of one byte, so no length check can separate them and no
    /// implementation can pass both by accident. An implementation that measured width and called it a
    /// version check would accept this; one that refused everything would fail its twin.
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithAnAllZeroBufferOfALegalLength_Refuses()
    {
        // Arrange
        byte[] unset = new byte[EncapsulatedValueEnvelope.MinimumLength];

        // Act
        bool wellFormed = EncapsulatedValueEnvelope.IsWellFormed(unset);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// An empty buffer is refused rather than read.
    /// </summary>
    /// <remarks>
    /// Length has to be judged before version, and this is the case that says so: there is no leading
    /// byte here for a version check to look at, so an implementation that reached for <c>value[0]</c>
    /// first would not return <see langword="false"/> — it would throw, out of a method whose whole
    /// contract is to answer a question without one. An absent member and a zero-length column both
    /// arrive in exactly this shape.
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithAnEmptyBuffer_Refuses()
    {
        // Arrange
        byte[] empty = [];

        // Act
        bool wellFormed = EncapsulatedValueEnvelope.IsWellFormed(empty);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// A null buffer is answered rather than thrown at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The empty-buffer argument extended by one step, and the step is where the contract actually
    /// breaks.</b> A column that is NULL and a member absent from a payload both arrive as a null
    /// reference, not as a zero-length array — an EF-mapped <c>byte[]</c> and a deserialised member
    /// each produce one — and this type's whole contract is that it answers a question without
    /// throwing. Refusing is an answer; a <see cref="NullReferenceException"/> escaping into a caller
    /// that was about to write a refusal sentence is not.
    /// </para>
    /// <para>
    /// <b>What it pins as a side effect is the parameter type, which is the point.</b> Measured by the
    /// code agent: writing the parameter as <c>byte[] value</c> compiles at every call site in the
    /// solution and passes every other case in this file, whether it reads <c>value.Length</c> on a
    /// null and throws <see cref="NullReferenceException"/> or guards it and throws
    /// <see cref="ArgumentNullException"/>. Both are refusals dressed as crashes. Under
    /// <c>ReadOnlySpan&lt;byte&gt;</c> a null array converts to an empty span and the method answers
    /// <see langword="false"/>, which is this case.
    /// </para>
    /// <para>
    /// It is written as a null rather than as <c>default</c> deliberately: <c>default</c> would also
    /// pass against a <c>byte[]</c> parameter reading <c>Length</c> on... nothing, because
    /// <c>default(byte[])</c> <em>is</em> null — the two spellings mean the same thing here, and the
    /// explicit <c>null!</c> is the one that says out loud what arrives from the database.
    /// </para>
    /// </remarks>
    [Test]
    public async Task IsWellFormed_WithANullBuffer_Refuses()
    {
        // Arrange
        byte[]? absent = null;

        // Act
        bool wellFormed = EncapsulatedValueEnvelope.IsWellFormed(absent);

        // Assert
        await Assert.That(wellFormed).IsFalse();
    }

    /// <summary>
    /// The minimum is a version byte, an uncompressed P-256 point, a 96-bit nonce and a 128-bit tag,
    /// and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One of the two places in this file where a literal is correct.</b> Everywhere else a bound is
    /// read from the constant, so the rule and its edge cannot drift apart; here the constants
    /// <em>are</em> the thing under test, and a test computing <c>MinimumLength</c> from
    /// <c>VersionBytes + EphemeralPublicKeyBytes + NonceBytes + TagBytes</c> would agree with any four
    /// numbers the type later chose. So each component is pinned on its own and the sum is written out
    /// as <c>94</c>, because the sum alone is satisfied by a format that moved a byte from the nonce to
    /// the tag.
    /// </para>
    /// <para>
    /// <b>Three of the four components are declared as aliases of <see cref="CiphertextEnvelope"/>'s,
    /// which is what makes the literals load-bearing rather than ceremonial here.</b> A test reading
    /// <see cref="EncapsulatedValueEnvelope.NonceBytes"/> and comparing it to
    /// <see cref="CiphertextEnvelope.NonceBytes"/> compares a definition with itself and would stay
    /// green through any edit to the target. Written out, an alias whose target moved is red in this
    /// file and in <c>CiphertextEnvelopeTests</c> at once, which is the correct number of places for a
    /// width two formats share.
    /// </para>
    /// <para>
    /// <b>65 is the uncompressed SEC1 encoding of a P-256 point</b> — a <c>0x04</c> prefix and two
    /// 32-byte coordinates — and it is the one component with no twin next door. A compressed point is
    /// 33 bytes and a raw coordinate pair is 64; both are numbers a later reader could arrive at
    /// honestly, and either one silently re-cuts every slice after it.
    /// </para>
    /// <para>
    /// <b>What these two literals do <em>not</em> hold, measured and deliberately left.</b> Writing
    /// <see cref="EncapsulatedValueEnvelope.MinimumLength"/> out as a hand-written <c>94</c> instead of the
    /// const arithmetic passes this test and every other one in the solution — nothing in the build
    /// derives one of these numbers from the other, exactly as
    /// <see cref="Domain.Users.WrappedAccountKeys.WrappedPrivateKeyLength"/> records about its own arithmetic.
    /// The answer this repository has already settled on is not a stronger single assertion, which
    /// cannot exist: a test that read the sum from the parts would agree with any four numbers, and one
    /// that read the parts from the sum would agree with any sum. It is two pins that do not follow an
    /// edit, which is what the five lines below are. Move a part and the parts go red; move the sum and
    /// the sum and <see cref="MinimumLength_IsTheAeadFramingWithAnEphemeralPointSplicedIn"/> both do.
    /// The spelling of the declaration is invisible to all of them and is argued in the type's own
    /// remarks, where an unexecuted argument is the only kind available.
    /// </para>
    /// </remarks>
    [Test]
    public async Task MinimumLength_IsAVersionAPointANonceAndATag()
    {
        // Act, Assert
        await Assert.That(EncapsulatedValueEnvelope.VersionBytes).IsEqualTo(1);
        await Assert.That(EncapsulatedValueEnvelope.EphemeralPublicKeyBytes).IsEqualTo(65);
        await Assert.That(EncapsulatedValueEnvelope.NonceBytes).IsEqualTo(12);
        await Assert.That(EncapsulatedValueEnvelope.TagBytes).IsEqualTo(16);

        // 94 = 1 + 65 + 12 + 16, written as the number the format produces rather than as the sum of
        // the four lines above, which the type's own definition already computes.
        await Assert.That(EncapsulatedValueEnvelope.MinimumLength).IsEqualTo(94);
    }

    /// <summary>
    /// The encapsulation framing is the AEAD framing with an ephemeral point spliced in, and the two
    /// floors
    /// are 65 bytes apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A relation, not a number, and the only assertion here that is allowed to be arithmetic over
    /// constants.</b> It can fail: either format may move a width independently, and the day one of
    /// them does, the sentence this whole file is written around — "the encapsulation framing is the AEAD
    /// framing with a point in the middle" — stops being true. Pinning it beside the literals is what
    /// turns that from a comment into a claim.
    /// </para>
    /// <para>
    /// The inequality is the other half. Two floors that coincided would make
    /// <see cref="IsWellFormed_WithABufferLongEnoughForTheAeadFramingAlone_Refuses"/> a test of
    /// nothing — the buffer it builds would sit exactly on this format's bound — so the case above is
    /// only a discriminator while these two numbers differ, and this line is what says they do.
    /// </para>
    /// </remarks>
    [Test]
    public async Task MinimumLength_IsTheAeadFramingWithAnEphemeralPointSplicedIn()
    {
        // Act, Assert
        await Assert.That(EncapsulatedValueEnvelope.MinimumLength)
            .IsEqualTo(CiphertextEnvelope.MinimumLength + EncapsulatedValueEnvelope.EphemeralPublicKeyBytes);
        await Assert.That(EncapsulatedValueEnvelope.MinimumLength)
            .IsGreaterThan(CiphertextEnvelope.MinimumLength);
    }

    /// <summary>
    /// The version this suite is defined at is <c>1</c>, and the number is written out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own method rather than a sixth line in
    /// <see cref="MinimumLength_IsAVersionAPointANonceAndATag"/>, because it is a different claim.</b>
    /// That test pins the <em>widths</em> the layout is cut into; this one pins the <em>value</em> of
    /// the byte that goes on the wire. Folded together, a failure would not say which of the two moved.
    /// </para>
    /// <para>
    /// <b>Nothing else in this file can hold this number.</b> Every other case reads the version
    /// relatively — <c>Version - 1</c>, <c>Version + 1</c>, or a buffer carrying <c>Version</c> in its
    /// leading byte — so every one of them follows the constant wherever it goes. Change it to 2 and
    /// this file would go on describing a format that is no longer the one it tests: the matrix would
    /// begin testing 1 and 3, the accepting cases would build buffers led by <c>0x02</c>, and the
    /// all-zero buffer would stay refused because 2 is not 0.
    /// </para>
    /// <para>
    /// <b>And it is deliberately <em>not</em> written as
    /// <c>IsEqualTo(CiphertextEnvelope.Version)</c>.</b> The two suites spend the same byte on
    /// different cryptography, and they do so by coincidence of both being first rather than by any
    /// rule tying them together. An assertion in terms of the neighbour would say the opposite, and
    /// would go on passing if that neighbour were ever versioned forward — leaving this deployment
    /// claiming an encapsulated-value suite it does not implement. <c>EnvelopeSuiteCensusTests</c> holds the
    /// same rule from the other side, over the declarations themselves.
    /// </para>
    /// <para>
    /// The cast is not decoration. <c>IsEqualTo(1)</c> against a <see cref="byte"/> compiles and then
    /// throws at run time, so the expected value is typed to match the constant.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Version_IsTheOneVersionDefined()
    {
        // Act, Assert
        await Assert.That(EncapsulatedValueEnvelope.Version).IsEqualTo((byte)1);
    }

    /// <summary>
    /// A buffer of <paramref name="width"/> bytes whose leading byte is <paramref name="version"/> and
    /// whose remainder is zeros.
    /// </summary>
    /// <remarks>
    /// The zeros are not a point, not a nonce and not a tag, and nothing at this layer inspects any of
    /// them — the server cannot run the ECDH this format names. Leaving them zero is what makes the
    /// accepting case and the all-zero refusal differ in exactly one bit.
    /// </remarks>
    private static byte[] EncapsulatedValue(byte version, int width)
    {
        byte[] value = new byte[width];

        if (width > 0)
        {
            value[0] = version;
        }

        return value;
    }
}
