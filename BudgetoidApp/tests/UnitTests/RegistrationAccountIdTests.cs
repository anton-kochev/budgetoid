using System.Security.Cryptography;
using Application.Registration;

namespace UnitTests;

/// <summary>
/// The account identifier a registration ceremony derives from its own challenge: one value, reached
/// twice, from bytes the server minted and nobody else chose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a derivation at all rather than a fresh uuid.</b> The <c>user.id</c> handed to the
/// authenticator when options are minted and the <c>users.id</c> written when the ceremony finishes are
/// the same value or the account is unusable: an authenticator stores the handle it was given, and a
/// row created under a different id answers no assertion that device will ever produce. The failure is
/// silent and permanent — every later sign-in from that authenticator simply does not match, with no
/// error naming the cause — so the two are made equal by construction rather than by carrying the value
/// between two requests.
/// </para>
/// <para>
/// <b>Which control covers which claim.</b> Determinism, distinctness, the shape of the identifier and
/// the width refusal are each one test; none of them is worth anything without the last two.
/// <see cref="For_IsNotTheChallengesFirstSixteenBytes" /> and
/// <see cref="For_IsNotTheUndomainSeparatedHashOfTheChallenge" /> are the domain-separation controls,
/// and they are the reason this file exists in this shape: an implementation that truncated the
/// challenge, or that hashed it without the separation prefix, satisfies every other test here. The
/// first hands anybody who can influence a challenge a chosen account id; the second reuses one hash
/// across whatever else this system ever derives from the same bytes.
/// </para>
/// <para>
/// <b>Fixed vectors, never random input.</b> Every challenge below is a literal, so each test is a pin
/// on a value rather than a property check that happened to hold on the bytes this run drew. The
/// derivation is meant to be reproducible by a second implementation reading the specification, and a
/// randomised suite cannot say what it produced.
/// </para>
/// </remarks>
public sealed class RegistrationAccountIdTests
{
    /// <summary>
    /// How many bytes a registration challenge carries.
    /// </summary>
    /// <remarks>
    /// Restated here rather than read off the type under test, because the width <em>is</em> the pin: a
    /// test taking its expectation from the production constant agrees with whatever that constant later
    /// becomes, including with a width no authenticator was ever handed.
    /// </remarks>
    private const int ChallengeLength = 32;

    /// <summary>A challenge, spelled out so every assertion below is about a known value.</summary>
    private const string Challenge =
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    /// <summary>
    /// The same challenge with only its <b>first</b> byte changed.
    /// </summary>
    /// <remarks>
    /// Paired with the one below so distinctness is stated over both ends of the input. A derivation
    /// reading a prefix is green against a neighbour that differs at the end, and one reading a suffix
    /// is green against a neighbour that differs at the start; only both together say the whole
    /// challenge was consumed.
    /// </remarks>
    private const string ChallengeDifferingInItsFirstByte =
        "ff0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    /// <summary>The same challenge with only its <b>last</b> byte changed.</summary>
    private const string ChallengeDifferingInItsLastByte =
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1eff";

    /// <summary>
    /// A challenge of the legal width holding nothing but zeros.
    /// </summary>
    /// <remarks>
    /// The arrangement most likely to produce the all-zero identifier under a truncating implementation,
    /// which is why the "never empty" claim is driven against it as well as against a real vector. The
    /// all-zero uuid is the one value two accounts reach independently, and
    /// <c>CanonicalFactorId.TryParse</c> already refuses it for that reason one layer over.
    /// </remarks>
    private const string AllZeroChallenge =
        "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// The same challenge derives the same identifier, on every call.
    /// </summary>
    /// <remarks>
    /// The whole point of the derivation, stated at its weakest: options time and finish time are two
    /// calls in two requests, and if they disagree the account is created under an id the authenticator
    /// has never heard of.
    /// </remarks>
    [Test]
    public async Task For_GivenTheSameChallenge_ReturnsTheSameId()
    {
        // Arrange
        byte[] challenge = Convert.FromHexString(Challenge);

        // Act
        Guid first = RegistrationAccountId.For(challenge);
        Guid second = RegistrationAccountId.For(Convert.FromHexString(Challenge));

        // Assert
        await Assert.That(first).IsEqualTo(second);
        await Assert.That(RegistrationAccountId.For(challenge)).IsEqualTo(first);
    }

    /// <summary>
    /// Two challenges differing in one byte derive two identifiers.
    /// </summary>
    /// <remarks>
    /// Stated over three vectors rather than two, because the pair a careless implementation survives
    /// depends on which end of the input it reads. The three differ from one another in the first byte
    /// and in the last, so a derivation that consumed a prefix, a suffix, or a fixed slice of either is
    /// red here.
    /// </remarks>
    [Test]
    public async Task For_GivenDifferentChallenges_ReturnsDifferentIds()
    {
        // Arrange
        byte[][] challenges =
        [
            Convert.FromHexString(Challenge),
            Convert.FromHexString(ChallengeDifferingInItsFirstByte),
            Convert.FromHexString(ChallengeDifferingInItsLastByte),
        ];

        // Act
        Guid[] derived = [.. challenges.Select(challenge => RegistrationAccountId.For(challenge))];

        // Assert
        await Assert.That(derived.Distinct().Count()).IsEqualTo(challenges.Length);
    }

    /// <summary>
    /// The identifier is never the all-zero uuid.
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.Empty" /> is what an unset field sends and the one value two accounts reach
    /// independently, so it is refused wherever this system accepts an identifier from anybody. Driven
    /// against the all-zero challenge too, which is the input under which a truncating implementation
    /// would produce it.
    /// </remarks>
    [Test]
    [Arguments(Challenge)]
    [Arguments(AllZeroChallenge)]
    public async Task For_ReturnsAnIdThatIsNeverEmpty(string challenge)
    {
        // Act
        Guid derived = RegistrationAccountId.For(Convert.FromHexString(challenge));

        // Assert
        await Assert.That(derived).IsNotEqualTo(Guid.Empty);
    }

    /// <summary>
    /// The identifier is a well-formed RFC 9562 version 8 uuid with the standard variant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Version 8 is the specification's own slot for a value derived by an application rather than drawn
    /// at random or built from a timestamp, which is exactly what this is. The variant bits say the
    /// layout is the standard one, so anything reading the value as a uuid reads the same bytes this
    /// system meant.
    /// </para>
    /// <para>
    /// <b>The nibbles are read off the big-endian layout, and that is the whole care in this test.</b>
    /// <see cref="Guid.ToByteArray()" /> with no argument hands back .NET's mixed-endian layout, in which
    /// the first three fields are byte-swapped — read the version from index 6 of <em>that</em> and the
    /// assertion is about a byte nobody set, which is the most likely way a test in this file passes
    /// while saying nothing. <c>ToByteArray(bigEndian: true)</c> is the layout the specification numbers,
    /// and the same one <c>new Guid(span, bigEndian: true)</c> consumes.
    /// </para>
    /// </remarks>
    [Test]
    public async Task For_ReturnsAVersionEightVariantOneId()
    {
        // Arrange
        byte[] challenge = Convert.FromHexString(Challenge);

        // Act
        byte[] layout = RegistrationAccountId.For(challenge).ToByteArray(bigEndian: true);
        int version = layout[6] >> 4;
        int variant = layout[8] & 0xC0;

        // Assert
        await Assert.That(version).IsEqualTo(8);
        await Assert.That(variant).IsEqualTo(0x80);
    }

    /// <summary>
    /// A challenge that is not exactly <see cref="ChallengeLength" /> bytes is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both sides of the bound and the empty buffer, because the three are different mistakes. Short is
    /// a challenge with less entropy than the design claims, and it would derive a perfectly well-formed
    /// identifier nothing downstream could tell from a real one. Long is a caller and this function
    /// disagreeing about what a challenge is. Empty is what an unset field and a stubbed caller both
    /// send, and it is the arrangement a derivation would most plausibly answer with a constant.
    /// </para>
    /// <para>
    /// <b>It throws rather than returning a sentinel</b>, because there is no caller that can do
    /// anything with a partial answer: the value is the account's identity, and a function that quietly
    /// padded or truncated would create the account under an id the authenticator was never handed.
    /// <see cref="ArgumentException" /> is asserted rather than an exact type, which also admits
    /// <see cref="ArgumentOutOfRangeException" /> — either says the argument was wrong, and pinning the
    /// narrower of the two would be this test having an opinion about a sentence it does not own.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(0)]
    [Arguments(ChallengeLength - 1)]
    [Arguments(ChallengeLength + 1)]
    public async Task For_GivenAChallengeOfTheWrongWidth_Throws(int width)
    {
        // Arrange
        byte[] misshapen = new byte[width];

        // Act
        ArgumentException exception = Throws<ArgumentException>(() => RegistrationAccountId.For(misshapen));

        // Assert
        await Assert.That(exception).IsNotNull();
    }

    /// <summary>
    /// The identifier is not the challenge's own first sixteen bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The most important test in this file, and the reason the derivation is a hash rather than a
    /// slice.</b> "Take the first half of the challenge" satisfies determinism, distinctness,
    /// non-emptiness and the version-and-variant shape — every other test here — while handing anybody
    /// who can influence a challenge an account identifier of their choosing. A one-way function is what
    /// makes the id unpredictable to everyone but this server, even though the challenge itself is sent
    /// to a browser in the clear.
    /// </para>
    /// <para>
    /// Four candidates rather than one, because the plausible truncation has four spellings: the sixteen
    /// bytes read as a uuid in either endianness, and each of those with the version and variant bits
    /// stamped — the last two being exactly what a truncating implementation would have to do to pass
    /// <see cref="For_ReturnsAVersionEightVariantOneId" />.
    /// </para>
    /// </remarks>
    [Test]
    public async Task For_IsNotTheChallengesFirstSixteenBytes()
    {
        // Arrange
        byte[] challenge = Convert.FromHexString(Challenge);

        // Act
        Guid derived = RegistrationAccountId.For(challenge);
        Guid[] truncations = ReadEveryWay(challenge[..16]);

        // Assert
        await Assert.That(truncations.Contains(derived)).IsFalse();
    }

    /// <summary>
    /// The identifier is not a bare <c>SHA-256</c> of the challenge, truncated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second control, one level up from the first: an implementation that hashes but forgets the
    /// domain-separation prefix is one-way, unpredictable, deterministic and distinct — green on every
    /// other test in this file. What it loses is separation. <c>SHA-256(challenge)</c> is a value any
    /// other part of this system deriving something from the same challenge would reach independently,
    /// so two unrelated purposes would share one secret and neither would be able to say so.
    /// </para>
    /// <para>
    /// The same four candidate spellings as the test above, and for the same reason.
    /// </para>
    /// </remarks>
    [Test]
    public async Task For_IsNotTheUndomainSeparatedHashOfTheChallenge()
    {
        // Arrange
        byte[] challenge = Convert.FromHexString(Challenge);

        // Act
        Guid derived = RegistrationAccountId.For(challenge);
        Guid[] undomainSeparated = ReadEveryWay(SHA256.HashData(challenge)[..16]);

        // Assert
        await Assert.That(undomainSeparated.Contains(derived)).IsFalse();
    }

    /// <summary>
    /// Every uuid sixteen bytes could plausibly be read as: either endianness, raw and with the version
    /// and variant bits stamped.
    /// </summary>
    /// <remarks>
    /// The stamped pair is what makes the two controls above real. A wrong implementation cannot both
    /// take its bytes straight from the input and satisfy the shape assertion, so the value it would
    /// actually produce is the stamped one — and a control listing only the raw readings would miss it.
    /// </remarks>
    private static Guid[] ReadEveryWay(byte[] sixteen)
    {
        byte[] stamped = [.. sixteen];
        stamped[6] = (byte)((stamped[6] & 0x0F) | 0x80);
        stamped[8] = (byte)((stamped[8] & 0x3F) | 0x80);

        return
        [
            new Guid(sixteen, bigEndian: true),
            new Guid(sixteen, bigEndian: false),
            new Guid(stamped, bigEndian: true),
            new Guid(stamped, bigEndian: false),
        ];
    }

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" />, so anything else escapes and fails the test
    /// as itself rather than as "the expected exception was not thrown". The shape is
    /// <c>GenerateRecoveryCodesHandlerTests.ThrowsAsync</c>'s, synchronous because nothing on this path
    /// awaits — a span-taking function cannot be called from an async closure at all.
    /// </remarks>
    private static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
