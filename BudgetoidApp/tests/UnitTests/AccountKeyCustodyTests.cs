using Application.AccountKeys;
using Domain.Users;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The pairing rule on the account-keys answer: a manifest and the generation beside it either describe
/// one stored row or describe its absence, and nothing in between may be constructed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three states are legal and every other pairing is refused, because the illegal ones are
/// indistinguishable from the legal ones on the wire.</b> An absent manifest at a non-zero epoch
/// serialises as a generation number over a manifest naming nobody — a client is told which generation
/// is in force and handed nothing to check it against. A present-but-empty manifest encodes as
/// <c>""</c>, which is legal base64url for zero bytes, so the client cannot tell "this account holds no
/// manifest" from "this account holds a manifest and it names nobody" — two states whose next steps
/// differ, one of them being to go and enrol a factor. A manifest wider than the column's cap, or at a
/// generation below the floor a stored row may claim, describes a row the database would have refused.
/// None of those has a symptom on this side of the wire, which is why the refusal is a constructor
/// guard rather than a note.
/// </para>
/// <para>
/// <b>Written because deleting the whole guard still reddens nothing.</b> Registration now writes a
/// <c>factor_manifests</c> row — at <c>FactorManifest.MinimumRotationEpoch</c>, in the account's own
/// save — so the populated pairing is reachable; but it is reachable at exactly one width and exactly
/// one generation, and every account that predates that line still produces the absent pairing. The read
/// service, the handler and the endpoint are therefore exercised on two points of a range the guard
/// covers the whole of. An off-by-one in the cap, or a floor of 2 instead of 1, passes the entire suite
/// without this file.
/// The boundary cases below are therefore as load-bearing as the refusals: the refusals say the guard
/// exists, and the boundaries say it is drawn in the right place.
/// </para>
/// <para>
/// <b>Bounds are written out as literals and never read off the type under test.</b> <c>4096</c>,
/// <c>4097</c>, <c>1</c>, <c>0</c> and <c>-1</c> appear here as numbers, the rule
/// <c>FactorManifestTests</c> keeps for the same three bounds one ring down: a test that reads its
/// bound off the code it checks moves with any edit to that code and compares a constant with itself.
/// The <em>names</em> in the thrown exceptions are read through <c>nameof</c>, which is the opposite
/// kind of reference — it tracks a rename rather than a decision, and a refusal keyed on the wrong
/// member would send a caller's error to the wrong place.
/// </para>
/// <para>
/// <b>What no test here can hold is the <c>with</c> expression, and that is by design rather than by
/// omission.</b> A record's <c>with</c> copies fields and never runs the constructor, so a guard cannot
/// see it; what keeps <c>with { Manifest = … }</c> and <c>with { RotationEpoch = … }</c> out is that
/// both members are declared get-only, which makes either one a <b>compile error</b>. A test cannot
/// assert a compile error, and a test that could would be the weaker of the two anyway.
/// </para>
/// <para>
/// <b>The exception types are distinguished exactly, not by assignability.</b>
/// <see cref="ArgumentOutOfRangeException" /> derives from <see cref="ArgumentException" />, so a
/// <c>catch (ArgumentException)</c> is satisfied by both and a guard that threw the range exception
/// where the argument exception belongs would pass a lenient check. <see cref="Refusal" /> compares the
/// runtime type.
/// </para>
/// </remarks>
public sealed class AccountKeyCustodyTests
{
    /// <summary>
    /// The absent pairing constructs: no manifest, epoch 0, and whatever factors the account holds.
    /// </summary>
    /// <remarks>
    /// The state of every account in every database, so a guard that refused it would take the product
    /// down rather than protect it. Factors are supplied, because the absent manifest says nothing
    /// about whether the account holds factors — the two levels are two tables keyed on two different
    /// things.
    /// </remarks>
    [Test]
    public async Task Constructor_WithNoManifestAtEpochZero_Constructs()
    {
        // Arrange
        IReadOnlyList<FactorEnvelopes> factors = [Factor(0)];

        // Act
        AccountKeyCustody custody = new(null, 0, factors);

        // Assert
        await Assert.That(custody.Manifest.HasValue).IsFalse();
        await Assert.That(custody.RotationEpoch).IsEqualTo(0);
        await Assert.That(custody.Factors.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A stored manifest at the floor constructs — the boundary is inclusive on that side.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="Constructor_WithAManifestBelowTheFloorEpoch_Throws" />: without this
    /// case a guard written <c>&lt;=</c> instead of <c>&lt;</c> would refuse the very first manifest
    /// every account writes, and every other case here would stay green. Epoch 1 is the generation a
    /// registration files, so this is not a corner — it is the commonest stored row there will ever be.
    /// </remarks>
    [Test]
    public async Task Constructor_WithAManifestAtTheFloorEpoch_Constructs()
    {
        // Arrange
        byte[] manifest = Bytes(length: 16);

        // Act
        AccountKeyCustody custody = new(manifest, 1, []);

        // Assert
        await Assert.That(custody.Manifest.HasValue).IsTrue();
        await Assert.That(custody.Manifest!.Value.Length).IsEqualTo(16);
        await Assert.That(custody.RotationEpoch).IsEqualTo(1);
    }

    /// <summary>
    /// A manifest of exactly the widest legal width constructs — the cap is inclusive.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="Constructor_WithAManifestWiderThanTheCap_Throws" />. Without it a
    /// guard written <c>&gt;=</c> instead of <c>&gt;</c> refuses the one manifest an account with the
    /// most factors would hold, which is the account that can least afford to be refused, and no other
    /// case here exercises the width at the line.
    /// </remarks>
    [Test]
    public async Task Constructor_WithAManifestAtExactlyTheCap_Constructs()
    {
        // Arrange
        byte[] manifest = Bytes(length: 4096);

        // Act
        AccountKeyCustody custody = new(manifest, 7, []);

        // Assert
        await Assert.That(custody.Manifest!.Value.Length).IsEqualTo(4096);
    }

    /// <summary>
    /// REFUSAL — no manifest at a non-zero epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pairing this type used to admit, and the one it spends a paragraph of its own remarks
    /// refusing. It serialises as a generation number over nothing: the client is told which generation
    /// is in force and handed no manifest to check its factor set against, which is the input its
    /// mismatch detection reads as tampering.
    /// </para>
    /// <para>
    /// Two epochs rather than one, because a guard written <c>&gt; 0</c> and a guard written
    /// <c>!= 0</c> agree on 7 and disagree on -1 — the negative is what tells the two shapes apart, and
    /// a negative generation is meaningless in either direction.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(7)]
    [Arguments(-1)]
    public async Task Constructor_WithNoManifestAtANonZeroEpoch_Throws(int epoch)
    {
        // Arrange, Act
        Exception refusal = Refusal(() => new AccountKeyCustody(null, epoch, []));

        // Assert — keyed on the epoch, because the epoch is the member that is wrong: the absence of a
        // manifest is a legal state and it is the number beside it that disagrees.
        await Assert.That(refusal.GetType()).IsEqualTo(typeof(ArgumentOutOfRangeException));
        await Assert.That(((ArgumentOutOfRangeException)refusal).ParamName)
            .IsEqualTo(nameof(AccountKeyCustody.RotationEpoch));
    }

    /// <summary>
    /// REFUSAL — a present manifest carrying no bytes, at either neighbouring epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The spelling the whole type exists to keep apart.</b> An empty buffer encodes as <c>""</c>,
    /// which is legal base64url for zero bytes, so a client handed it cannot tell an account with no
    /// manifest from an account whose manifest names nobody. <c>null</c> is the only spelling on this
    /// wire that separates the two, and an empty buffer would spend a rendering the domain has already
    /// declared impossible — <c>FactorManifest.For</c> refuses one — on the state that is normal.
    /// </para>
    /// <para>
    /// <b>Both 0 and 1 are tried, and the pair is the point.</b> At epoch 0 the emptiness is the only
    /// thing wrong; at epoch 1 the pairing would otherwise be a legal stored row. A guard that checked
    /// the epoch first and returned early would refuse one and admit the other, and either single case
    /// on its own would miss it.
    /// </para>
    /// <para>
    /// The refusal is an <see cref="ArgumentException" /> and not an
    /// <see cref="ArgumentOutOfRangeException" />: an empty manifest is not a value out of range, it is
    /// a value that should have been spelled <see langword="null" />. Asserted as an exact type for the
    /// reason this class's remarks give about the two being related by inheritance.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Constructor_WithAnEmptyManifest_Throws(int epoch)
    {
        // Arrange — an empty ReadOnlyMemory<byte>, which is a PRESENT value of zero length rather than
        // an absent one. That difference is invisible at a glance and is the whole subject here.
        ReadOnlyMemory<byte>? empty = ReadOnlyMemory<byte>.Empty;

        // Act
        Exception refusal = Refusal(() => new AccountKeyCustody(empty, epoch, []));

        // Assert
        await Assert.That(refusal.GetType()).IsEqualTo(typeof(ArgumentException));
        await Assert.That(((ArgumentException)refusal).ParamName)
            .IsEqualTo(nameof(AccountKeyCustody.Manifest));
    }

    /// <summary>
    /// REFUSAL — a stored manifest at an epoch below the floor.
    /// </summary>
    /// <remarks>
    /// Epoch 0 is the <em>absence</em> of a row, so a manifest claiming it would assert its own absence
    /// and "never rotated" would stop being distinguishable from "rotated to generation zero" for the
    /// one read that has to tell them apart. The negative is refused beside it because no generation
    /// has a number below the first, and because it separates a guard written <c>&lt; 1</c> from one
    /// written <c>== 0</c>.
    /// </remarks>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Constructor_WithAManifestBelowTheFloorEpoch_Throws(int epoch)
    {
        // Arrange
        byte[] manifest = Bytes(length: 16);

        // Act
        Exception refusal = Refusal(() => new AccountKeyCustody(manifest, epoch, []));

        // Assert
        await Assert.That(refusal.GetType()).IsEqualTo(typeof(ArgumentOutOfRangeException));
        await Assert.That(((ArgumentOutOfRangeException)refusal).ParamName)
            .IsEqualTo(nameof(AccountKeyCustody.RotationEpoch));
    }

    /// <summary>
    /// REFUSAL — a manifest one byte wider than the column will hold.
    /// </summary>
    /// <remarks>
    /// One byte over rather than wildly over, because the guard this refuses is an off-by-one: a
    /// comparison written <c>&gt;=</c> and one written <c>&gt;</c> agree about a value twice the cap
    /// and disagree about exactly the cap and one past it. Paired with
    /// <see cref="Constructor_WithAManifestAtExactlyTheCap_Constructs" />, the two say where the line
    /// is rather than merely that there is one.
    /// </remarks>
    [Test]
    public async Task Constructor_WithAManifestWiderThanTheCap_Throws()
    {
        // Arrange
        byte[] manifest = Bytes(length: 4097);

        // Act
        Exception refusal = Refusal(() => new AccountKeyCustody(manifest, 1, []));

        // Assert — keyed on the manifest rather than the epoch: the epoch here is legal and it is the
        // bytes that are wrong.
        await Assert.That(refusal.GetType()).IsEqualTo(typeof(ArgumentOutOfRangeException));
        await Assert.That(((ArgumentOutOfRangeException)refusal).ParamName)
            .IsEqualTo(nameof(AccountKeyCustody.Manifest));
    }

    /// <summary>
    /// The absent case has one spelling, and it produces the pairing the guard permits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The factory exists because the absent pairing had three spellings.</b> "No manifest means
    /// epoch 0" was decided independently in two literals in the read service and once more in a test
    /// fake, which is three places a later edit has to find and agree with. This case is what says the
    /// one remaining spelling is the one the guard admits — a factory that passed any other number
    /// would throw rather than compile into a wrong answer, but a factory that passed the number is
    /// still worth reading back, because the constant it names is the thing a later edit would move.
    /// </para>
    /// <para>
    /// The factors are handed through unchanged, which is the half a factory could silently drop: an
    /// account with no manifest very often holds factors, and <c>WithNoManifest([])</c> would be an
    /// easy and invisible simplification.
    /// </para>
    /// <para>
    /// The epoch is compared to a literal <c>0</c> rather than to <c>NoManifestRotationEpoch</c>,
    /// because reading the constant would compare it with itself.
    /// </para>
    /// </remarks>
    [Test]
    public async Task WithNoManifest_IsNullAtEpochZeroAndKeepsTheFactors()
    {
        // Arrange
        IReadOnlyList<FactorEnvelopes> factors = [Factor(0), Factor(1)];

        // Act
        AccountKeyCustody custody = AccountKeyCustody.WithNoManifest(factors);

        // Assert
        await Assert.That(custody.Manifest.HasValue).IsFalse();
        await Assert.That(custody.RotationEpoch).IsEqualTo(0);
        await Assert.That(custody.Factors.Select(factor => factor.FactorId))
            .IsEquivalentTo(factors.Select(factor => factor.FactorId));
    }

    /// <summary>
    /// The constructor keeps the caller's bytes rather than a rendering of them.
    /// </summary>
    /// <remarks>
    /// The guard returns the value it was handed, and a guard that rebuilt one — normalising, copying
    /// into a right-sized array, slicing — would be doing work this type has no business doing and
    /// could get wrong without a single other case noticing. Distinct bytes and a length that is not a
    /// multiple of anything, so a rebuild that reversed, padded or truncated is visible.
    /// </remarks>
    [Test]
    public async Task Constructor_KeepsTheManifestBytesItWasHanded()
    {
        // Arrange
        byte[] manifest = Bytes(length: 37);

        // Act
        AccountKeyCustody custody = new(manifest, 7, []);

        // Assert
        await Assert.That(custody.Manifest!.Value.ToArray())
            .IsEquivalentTo(manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Builds <paramref name="length" /> bytes no two of which are equal up to 256 positions, so an
    /// assertion over them is about position and not merely about content.
    /// </summary>
    /// <remarks>
    /// The stride is coprime with 256 and the offset keeps index 0 off value 0, the arrangement
    /// <c>FactorManifestTests</c> keeps for the same reason: a run of one repeated byte is its own
    /// reversal and its own rotation, so nothing written over it can tell a copy from a rebuild.
    /// </remarks>
    private static byte[] Bytes(int length)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)(0x11 + (index * 7));
        }

        return bytes;
    }

    /// <summary>
    /// One factor's stored share, distinguishable from every other <paramref name="ordinal" />.
    /// </summary>
    /// <remarks>
    /// The two payloads are built to each suite's own width and version through two builders rather
    /// than one taking a width, the rule <c>WrappedAccountKeys</c> states: a single parameterised
    /// builder would let one suite's length be paired with the other's version. Nothing here opens
    /// either value — this type carries them and reads neither.
    /// </remarks>
    private static FactorEnvelopes Factor(int ordinal) => new(
        Guid.NewGuid(),
        Payload(
            WrappedAccountKeys.WrappedPrivateKeyLength,
            WrappedAccountKeys.WrappedPrivateKeyVersion,
            (byte)(0x10 + ordinal)),
        Payload(
            WrappedAccountKeys.EncapsulatedAccountKeysLength,
            WrappedAccountKeys.EncapsulatedAccountKeysVersion,
            (byte)(0xA0 + ordinal)));

    private static byte[] Payload(int length, byte version, byte marker)
    {
        byte[] bytes = new byte[length];

        bytes[0] = version;
        bytes[^1] = marker;

        return bytes;
    }

    /// <summary>
    /// Runs <paramref name="construct" /> and returns the exception it threw, so a case can assert the
    /// exact runtime type and the member the refusal is keyed on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Typed as <see cref="Exception" /> rather than as <see cref="ArgumentException" />, which is the
    /// point of it. <see cref="ArgumentOutOfRangeException" /> derives from
    /// <see cref="ArgumentException" />, so a helper that caught the base type and handed it back under
    /// that name would invite a case asserting assignability — and a guard that threw the range
    /// exception where the argument exception belongs would sail through. Handing back the bare
    /// exception forces every caller to say which type it means.
    /// </para>
    /// <para>
    /// A construction that <em>succeeds</em> fails here rather than returning null, because the
    /// alternative is a null-reference several assertions later with nothing in the message about what
    /// really happened: the guard admitted a pairing it should have refused.
    /// </para>
    /// </remarks>
    private static Exception Refusal(Func<AccountKeyCustody> construct)
    {
        try
        {
            _ = construct();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            "The pairing was accepted; AccountKeyCustody was expected to refuse it.");
    }
}
