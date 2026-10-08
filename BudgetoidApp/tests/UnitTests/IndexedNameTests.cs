using System.Security.Cryptography;
using Domain.Security;

namespace UnitTests;

/// <summary>
/// The one value a searchable name column pair holds: a sealed name judged as a narrative field, and
/// the blind index computed over it, neither admissible without the other.
/// </summary>
/// <remarks>
/// <para>
/// <b>The type's headline claim — "a call cannot be half" — is held by the signature and not by any
/// case below.</b> Both parameters are non-nullable, so an omitted half has no spelling this factory
/// accepts; what the cases can reach is the next thing down, which is that
/// <c>default(ReadOnlyMemory&lt;byte&gt;)</c> — what a caller passing nothing actually hands over — is
/// a zero-length buffer that fails the envelope's floor on one side and the index's width on the other,
/// and is repaired on neither. That is the shape
/// <see cref="Of_WithAMalformedEnvelope_ReportsTheEnvelope"/> and
/// <see cref="Of_WithABlindIndexOfTheWrongWidth_ReportsTheIndex"/> pin between them.
/// </para>
/// <para>
/// <b>Refusals are <see cref="ArgumentException"/>, never
/// <see cref="Domain.Common.ValidationException"/>, and each names the half at fault.</b> The
/// exception the entity factories beside this type use keys its message on the property a value lands
/// in; a narrative field is shared by eight columns and owns none, so its refusals would all key under
/// one word and produce a 400 naming a member no request carries. What a caller gets instead is the
/// parameter name, which is the only place an <see cref="ArgumentException"/> carries a machine-readable
/// account of what was wrong — hence <c>WithParameterName</c> rather than a message assertion. The
/// exact type is asserted because <see cref="ArgumentOutOfRangeException"/> is an
/// <see cref="ArgumentException"/>, and an assignable match would let a ceiling refusal pass as a value
/// refusal.
/// </para>
/// <para>
/// <b>Both halves malformed at once is not covered, and it cannot be.</b> The factory throws, and a
/// throw carries one exception; <see cref="ArgumentException"/> has a single
/// <see cref="ArgumentException.ParamName"/> and no errors collection of the shape
/// <see cref="Domain.Common.ValidationException"/> carries. So there is no member for a second report to
/// land in, and a case asserting that both are named would be a case the signature makes unsatisfiable
/// — the assertion would have to be over free message text nothing in the type's documentation
/// promises. What is pinned instead is that each half, malformed on its own with the other half
/// well-formed, is the one that gets named. Whichever half an implementation checks first is then the
/// one reported when both are wrong, which is a wording question and not a correctness one: the call is
/// refused either way.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a refusal test with no accepting twin is passed by
/// a factory that throws unconditionally:
/// </para>
/// <list type="bullet">
/// <item>
/// "the name is judged as a narrative field" — <see cref="Of_WithAMalformedEnvelope_ReportsTheEnvelope"/>
/// against <see cref="Of_WithAWellFormedNameAndIndex_KeepsBoth"/>. The malformed value is one byte
/// under <see cref="CiphertextEnvelope.MinimumLength"/> and carries the right version, so the floor is
/// the only rule that can refuse it.
/// </item>
/// <item>
/// "under <see cref="NarrativeFieldLimits.NameBytes"/>, named by the factory itself" —
/// <see cref="Of_WithAnEnvelopeAtTheNameCap_IsAccepted"/> against
/// <see cref="Of_WithAnEnvelopeOneByteOverTheNameCap_ReportsTheEnvelope"/>. This is the pair that
/// catches a factory reaching for <see cref="NarrativeFieldLimits.DescriptionBytes"/> — the value in
/// between clears the name cap and is still a description-sized value filed into a name column, and no
/// other case in this file can see it.
/// </item>
/// <item>
/// "exactly <see cref="IndexedName.BlindIndexLength"/> bytes, from both sides" —
/// <see cref="Of_WithABlindIndexOfTheWrongWidth_ReportsTheIndex"/>, whose arguments step one byte
/// either way. Unlike its wire-side neighbour, both sides are this type's own work: nothing beneath a
/// domain factory is measuring anything, so a width written as a ceiling reddens the short argument and
/// one written as a floor reddens the wide one.
/// </item>
/// <item>
/// "a copy, not a view" — <see cref="Of_CopiesTheNameAndTheIndexRatherThanAliasingThem"/>, which
/// nothing else here can cover: every other case holds buffers nobody touches afterwards, so a factory
/// assigning the caller's <see cref="ReadOnlyMemory{T}"/> straight through passes all of them.
/// </item>
/// </list>
/// <para>
/// <b>What no case here can cover.</b> The server holds no index key, so it can never say the index is
/// the index <em>of</em> the name beside it. A correct-width value taken over the wrong text, under the
/// wrong key, or straight out of a random number generator satisfies every assertion below and is wrong
/// for the life of the account, silently. The width is the whole of the defence, which is why it is
/// exact rather than a band.
/// </para>
/// </remarks>
public sealed class IndexedNameTests
{
    /// <summary>
    /// A well-formed name and a well-formed index are both kept, each as exactly the bytes handed over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The accepting control the whole file rests on, and the only case that can catch a factory
    /// assigning one argument to both columns. The two buffers carry different lengths and different
    /// filler, so a swap or a duplication is visible: an index is 32 bytes and a name envelope is at
    /// least 29, so widths alone would not always separate them, and the content assertions are what
    /// close that.
    /// </para>
    /// <para>
    /// Content is asserted rather than length for the reason its neighbours give: a factory holding
    /// fresh buffers of the right sizes satisfies every other case here, and what it stores is a
    /// ciphertext nobody will open and a digest nobody can recompute — so the mistake surfaces on the
    /// day somebody searches for a name and does not find it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Of_WithAWellFormedNameAndIndex_KeepsBoth()
    {
        // Arrange
        byte[] name = Envelope(CiphertextEnvelope.MinimumLength + 137);
        byte[] index = Index(IndexedName.BlindIndexLength);

        // Act
        IndexedName indexed = IndexedName.Of(name, index);

        // Assert
        await Assert.That(indexed.Name.Envelope.ToArray()).IsEquivalentTo(name);
        await Assert.That(indexed.BlindIndex.ToArray()).IsEquivalentTo(index);
    }

    /// <summary>
    /// A name that is not a well-formed envelope is refused, and the refusal names the name.
    /// </summary>
    /// <remarks>
    /// One byte under <see cref="CiphertextEnvelope.MinimumLength"/>, carrying the version this
    /// deployment recognises, with a perfectly good index beside it — so the floor is the only rule
    /// that can refuse this and the index cannot be what is at fault. The parameter name is asserted
    /// because it is the one part of an <see cref="ArgumentException"/> a caller can read without
    /// parsing prose, and because the two halves of this type are told apart by nothing else: a factory
    /// that reported every refusal against the envelope would hand a client debugging a bad index the
    /// wrong field to look at, and would pass every other case in this file.
    /// </remarks>
    [Test]
    public async Task Of_WithAMalformedEnvelope_ReportsTheEnvelope()
    {
        // Arrange
        byte[] truncated = Envelope(CiphertextEnvelope.MinimumLength - 1);
        byte[] index = Index(IndexedName.BlindIndexLength);

        // Act, Assert
        await Assert.That(() => IndexedName.Of(truncated, index))
            .ThrowsExactly<ArgumentException>()
            .WithParameterName("envelope");
    }

    /// <summary>
    /// A name whose leading byte is not the one version defined today is refused.
    /// </summary>
    /// <remarks>
    /// IFR-007 reaching this factory through the narrative field it delegates to. Version 0 is the more
    /// valuable of the two arguments: an all-zero buffer of a legal length is what an uninitialised
    /// member, a zero-filled allocation and a stubbed client all send, and it satisfies every length
    /// rule here exactly. Its accepting twin is <see cref="Of_WithAWellFormedNameAndIndex_KeepsBoth"/>,
    /// which differs from it in the version byte and in filler, so no length rule can separate them.
    /// </remarks>
    [Test]
    [Arguments((byte)(CiphertextEnvelope.Version - 1))]
    [Arguments((byte)(CiphertextEnvelope.Version + 1))]
    public async Task Of_WithAnUnrecognisedVersion_ReportsTheEnvelope(byte version)
    {
        // Arrange
        byte[] misversioned = new byte[CiphertextEnvelope.MinimumLength];
        misversioned[0] = version;

        // Act, Assert
        await Assert.That(() => IndexedName.Of(misversioned, Index(IndexedName.BlindIndexLength)))
            .ThrowsExactly<ArgumentException>()
            .WithParameterName("envelope");
    }

    /// <summary>
    /// A name sitting exactly on <see cref="NarrativeFieldLimits.NameBytes"/> is accepted.
    /// </summary>
    /// <remarks>
    /// The cap's polarity from the accepting side, and half of the pair that proves this factory names
    /// the <em>name</em> cap. A check written <c>&lt;</c> rather than <c>&lt;=</c> refuses a value the
    /// limit names as legal and reddens here.
    /// </remarks>
    [Test]
    public async Task Of_WithAnEnvelopeAtTheNameCap_IsAccepted()
    {
        // Arrange
        byte[] name = Envelope(NarrativeFieldLimits.NameBytes);

        // Act
        IndexedName indexed = IndexedName.Of(name, Index(IndexedName.BlindIndexLength));

        // Assert
        await Assert.That(indexed.Name.Envelope.Length).IsEqualTo(NarrativeFieldLimits.NameBytes);
    }

    /// <summary>
    /// A name one byte over <see cref="NarrativeFieldLimits.NameBytes"/> is refused.
    /// </summary>
    /// <remarks>
    /// <b>The case that pins which cap this factory names, and the only one that can.</b> The type takes
    /// no ceiling parameter on purpose — every blind-indexed column in the product is a <c>name</c> — so
    /// the number is chosen inside the factory and is invisible at every call site. A factory reaching
    /// for <see cref="NarrativeFieldLimits.DescriptionBytes"/> instead admits this value and everything
    /// up to two and a half kilobytes, which is a description-sized value filed into a name column, and
    /// nothing else in this file notices. The envelope is well-formed in every other respect, so only
    /// the cap can refuse it.
    /// </remarks>
    [Test]
    public async Task Of_WithAnEnvelopeOneByteOverTheNameCap_ReportsTheEnvelope()
    {
        // Arrange
        byte[] overlong = Envelope(NarrativeFieldLimits.NameBytes + 1);

        // Act, Assert
        await Assert.That(() => IndexedName.Of(overlong, Index(IndexedName.BlindIndexLength)))
            .ThrowsExactly<ArgumentException>()
            .WithParameterName("envelope");
    }

    /// <summary>
    /// An index that is not exactly <see cref="IndexedName.BlindIndexLength"/> bytes is refused, from
    /// both sides, and the refusal names the index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HMAC-SHA-256 emits 32 bytes and nothing truncates in between, so there is no band of legal sizes
    /// to allow for and both sides of the width are wrong. Unlike the wire-side neighbour, where the
    /// wide side is refused by a shared decoder before the width is consulted, both arguments here are
    /// this factory's own work: nothing beneath a domain factory measures anything.
    /// </para>
    /// <para>
    /// It is refused rather than padded or truncated into shape. Either repair stores a well-formed row
    /// holding a value that is stable, never collides, keys perfectly and matches nothing for the life
    /// of the account — and the row looks correct until somebody searches for the name it was supposed
    /// to find. The name beside it is well-formed, so the index is the only thing that can be at fault,
    /// which is what makes the parameter name meaningful rather than incidental.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(IndexedName.BlindIndexLength - 1)]
    [Arguments(IndexedName.BlindIndexLength + 1)]
    public async Task Of_WithABlindIndexOfTheWrongWidth_ReportsTheIndex(int width)
    {
        // Arrange
        byte[] name = Envelope(CiphertextEnvelope.MinimumLength + 7);
        byte[] misshapen = Index(width);

        // Act, Assert
        await Assert.That(() => IndexedName.Of(name, misshapen))
            .ThrowsExactly<ArgumentException>()
            .WithParameterName("blindIndex");
    }

    /// <summary>
    /// An empty index is refused, which is what an omitted one arrives as.
    /// </summary>
    /// <remarks>
    /// The nearest a case can get to the type's "a call cannot be half" claim. The signature has no
    /// spelling for an absent index, so what a caller that passed nothing actually hands over is
    /// <c>default(ReadOnlyMemory&lt;byte&gt;)</c> — a non-null, zero-length buffer. Written out as
    /// <c>default</c> rather than as an empty array precisely because that is the value the mistake
    /// produces.
    /// </remarks>
    [Test]
    public async Task Of_WithAnEmptyBlindIndex_ReportsTheIndex()
    {
        // Arrange
        byte[] name = Envelope(CiphertextEnvelope.MinimumLength + 7);

        // Act, Assert
        await Assert.That(() => IndexedName.Of(name, default))
            .ThrowsExactly<ArgumentException>()
            .WithParameterName("blindIndex");
    }

    /// <summary>
    /// Both halves are copied, so a caller still holding either buffer cannot change what was filed.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="Domain.Users.WrappedAccountKeys.For"/> keeps for its own envelopes, applied
    /// to both members here because they are two different assignments and a factory can copy one and
    /// alias the other. <see cref="ReadOnlyMemory{T}"/> is a window onto a buffer somebody else still
    /// owns: without a copy, a buffer reused for the next row rewrites a name that has already been
    /// accepted, or an index that has, and the result is a well-formed row that will not open or will
    /// not be found — months later, with no server-side symptom.
    /// </remarks>
    [Test]
    public async Task Of_CopiesTheNameAndTheIndexRatherThanAliasingThem()
    {
        // Arrange
        byte[] name = Envelope(CiphertextEnvelope.MinimumLength + 7);
        byte[] index = Index(IndexedName.BlindIndexLength);
        byte expectedName = name[^1];
        byte expectedIndex = index[^1];
        IndexedName indexed = IndexedName.Of(name, index);

        // Act
        name[^1] ^= 0xFF;
        index[^1] ^= 0xFF;

        // Assert
        await Assert.That(indexed.Name.Envelope.Span[^1]).IsEqualTo(expectedName);
        await Assert.That(indexed.BlindIndex.Span[^1]).IsEqualTo(expectedIndex);
    }

    /// <summary>
    /// The blind index width is the width HMAC-SHA-256 actually emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one case here that pins the constant rather than reading it, and it takes the number
    /// from the algorithm rather than restating it.</b> The reason to pin at all is the one
    /// <c>WrappedAccountKeysTests</c> gives about its own width: every other case in this file reads
    /// <see cref="IndexedName.BlindIndexLength"/> for its fixtures, so all of them agree with whatever
    /// that constant later becomes. Something has to disagree with it.
    /// </para>
    /// <para>
    /// <b>What disagrees with it is a real digest, not a literal <c>32</c>.</b> The docblock's claim is
    /// that the width "follows from the algorithm rather than from a choice", and a hand-written 32
    /// asserts a number while quietly re-making the choice — two constants either side of an equals
    /// sign, which the compiler decides and which needs no type under test loaded to pass. Taking the
    /// length off an actual <see cref="HMACSHA256"/> output states the claim as it is written: change
    /// the constant and this reddens; change the algorithm the product uses and the fixture is what has
    /// to move with it. The key and the message are arbitrary — HMAC's output width does not depend on
    /// either, which is the whole of the point.
    /// </para>
    /// <para>
    /// What the disagreement is worth: a width that is not the digest's is a client and a server
    /// disagreeing about which digest is being computed, and the symptom is a column of indices that
    /// match nothing, for the life of the account, with nothing on this side able to see it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BlindIndexLength_IsTheHmacSha256OutputWidth()
    {
        // Arrange
        byte[] key = Index(IndexedName.BlindIndexLength);

        // Act
        byte[] digest = HMACSHA256.HashData(key, "a normalised name"u8);

        // Assert
        await Assert.That(digest.Length).IsEqualTo(IndexedName.BlindIndexLength);
    }

    /// <summary>
    /// A well-formed envelope of <paramref name="width"/> bytes: the version byte, then filler that
    /// varies per position.
    /// </summary>
    /// <remarks>
    /// The filler is not a nonce and not a ciphertext, and nothing at this layer inspects either — the
    /// server holds no value that could open the envelope. It varies rather than repeating so that the
    /// content assertions cannot be satisfied by a buffer of the right size filled with one byte.
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

    /// <summary>
    /// A blind index of <paramref name="width"/> bytes: filler that varies per position.
    /// </summary>
    /// <remarks>
    /// Not a real digest, and nothing at this layer could tell — the server holds no index key, so a
    /// value of the right width is indistinguishable from one HMAC-SHA-256 produced. The filler is
    /// deliberately a different sequence from the envelope helper's, so that the accepting case cannot
    /// be satisfied by a factory that assigned one argument to both columns.
    /// </remarks>
    private static byte[] Index(int width)
    {
        byte[] index = new byte[width];

        for (int position = 0; position < width; position++)
        {
            index[position] = (byte)(position * 53 + 29);
        }

        return index;
    }
}
