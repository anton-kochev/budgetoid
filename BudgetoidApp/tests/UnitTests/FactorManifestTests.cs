using Domain.Common;
using Domain.Users;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The account's manifest of every recovery factor's public key — one row per account, and the sole
/// authenticated carrier of what a client may encapsulate a new factor's key to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three refusals, and each of them stands for a loss that nothing else would report.</b> An
/// <em>empty</em> manifest is exactly what an unset member sends, so a caller that forgot to attach one
/// would file a row naming no factor at all — an account stored as though it had a way back in, holding
/// none. A manifest <em>wider than the cap</em> is refused rather than cut, because this blob is the
/// sole carrier of every factor's public key and the cut lands on whichever factor sat past the line:
/// that factor stops being encapsulatable-to, and the row left behind is well-formed, so the loss
/// surfaces on the day somebody reaches for the factor that is gone. An <em>epoch below the floor</em>
/// would have a stored row assert its own absence, because epoch 0 is the answer an account with no
/// manifest gives; store it and "never rotated" and "rotated to generation zero" stop being
/// distinguishable to the one read that has to tell them apart. The three tests below marked REFUSAL
/// are those three, one each, and each names the field the refusal is keyed on rather than merely that
/// something was thrown — a guard that refused the right input under the wrong key would send the
/// client's error to the wrong control.
/// </para>
/// <para>
/// <b>Literals, not the constants under test.</b> <see cref="FactorManifest.MaximumBytes" /> and
/// <see cref="FactorManifest.MinimumRotationEpoch" /> are never read here — <c>4096</c>, <c>4097</c>,
/// <c>1</c> and <c>0</c>/<c>-1</c> are written out, the same rule <c>WrappedAccountKeysTests</c> keeps
/// for <c>EnvelopeLength</c>: a test that reads its bound off the type under test agrees with whatever
/// that type later decides the bound is, and a future edit to either constant would move both sides of
/// the comparison together and prove nothing.
/// </para>
/// <para>
/// <b>What this file does not cover is the mapping.</b> <see cref="FactorManifest.Manifest" /> is a
/// <see cref="ReadOnlyMemory{T}" />, and what EF does with one is decided by
/// <c>FactorManifestConfiguration</c> rather than by anything here.
/// <c>FactorManifestMappingTests</c> holds that half.
/// </para>
/// </remarks>
public sealed class FactorManifestTests
{
    /// <summary>
    /// Fixed instant the seeded <see cref="User" /> is created at, so nothing here depends on the wall
    /// clock.
    /// </summary>
    /// <remarks>
    /// It is the <em>user's</em> instant and nothing else. <see cref="FactorManifest.For" /> takes no
    /// instant of its own — the table carries no timestamp column — so a name suggesting this belonged
    /// to the manifest would describe a member that does not exist.
    /// </remarks>
    private static readonly DateTime UserCreatedAtUtc = new(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// A well-formed manifest is accepted, and every member — owner, bytes, epoch — lands where it
    /// belongs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner is asserted by exact id equality against a <see cref="User" /> built with a known
    /// <see cref="Guid" />, not merely asserted non-empty — a factory that fabricated its own id would
    /// still pass a non-empty check.
    /// </para>
    /// <para>
    /// The bytes are distinct and the comparison is ordered, both on purpose. TUnit's bare
    /// <c>IsEquivalentTo</c> defaults to <see cref="CollectionOrdering.Any" /> and compares no order at
    /// all, and a run of one repeated byte has no order to compare in the first place — between them,
    /// an implementation that reversed the buffer, rotated it, or rebuilt it out of any sixteen bytes of
    /// the same value would satisfy the assertion. Distinct bytes plus
    /// <see cref="CollectionOrdering.Matching" /> is what makes this an assertion about position, which
    /// is the whole of what "the bytes the client wrote" means for a blob nothing on this side can read.
    /// </para>
    /// </remarks>
    [Test]
    public async Task For_WithAWellFormedManifest_StoresOwnerBytesAndEpoch()
    {
        // Arrange
        Guid ownerId = Guid.CreateVersion7();
        User user = User.CreateWithId(ownerId, "person@example.com", UserCreatedAtUtc);

        // Distinct bytes, so the comparison below is about position rather than about content.
        byte[] manifest = DistinctBytes(length: 16);

        // Act
        FactorManifest result = FactorManifest.For(user, manifest, rotationEpoch: 3);

        // Assert
        await Assert.That(result.UserId).IsEqualTo(ownerId);
        await Assert.That(result.Manifest.ToArray()).IsEquivalentTo(manifest, CollectionOrdering.Matching);
        await Assert.That(result.RotationEpoch).IsEqualTo(3);
    }

    /// <summary>
    /// REFUSAL — an epoch below <see cref="FactorManifest.MinimumRotationEpoch" /> is refused with a
    /// <see cref="ValidationException" />.
    /// </summary>
    /// <remarks>
    /// Generations count from one, not zero: zero is what an unset epoch sends, and a negative epoch has
    /// no generation it could name. Both are tried, because a check written as <c>&lt;= 0</c> and one
    /// written as <c>== 0</c> agree on zero and disagree on the negative — only both arguments together
    /// pin the shape of the guard.
    /// </remarks>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task For_WithAnEpochBelowTheMinimum_Throws(int epoch)
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UserCreatedAtUtc);
        byte[] manifest = Bytes(length: 16, filler: 0xAB);

        // Act
        ValidationException exception = ThrowsValidationException(
            () => FactorManifest.For(user, manifest, epoch));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(FactorManifest.RotationEpoch))).IsTrue();
    }

    /// <summary>
    /// REFUSAL — an empty manifest is refused.
    /// </summary>
    /// <remarks>
    /// An account whose manifest names no factor has no way back in, and an empty <c>bytea</c> is
    /// exactly what an unset member sends — a caller that forgot to attach the manifest would otherwise
    /// file a row indistinguishable from a legitimately narrow one.
    /// </remarks>
    [Test]
    public async Task For_WithAnEmptyManifest_Throws()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UserCreatedAtUtc);
        byte[] manifest = [];

        // Act
        ValidationException exception = ThrowsValidationException(
            () => FactorManifest.For(user, manifest, rotationEpoch: 1));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(FactorManifest.Manifest))).IsTrue();
    }

    /// <summary>
    /// REFUSAL — a manifest wider than <see cref="FactorManifest.MaximumBytes" /> is refused, never
    /// truncated.
    /// </summary>
    /// <remarks>
    /// The manifest is the sole authenticated carrier of every factor's public key, so truncation is
    /// not a safe repair: it silently drops whichever factor's key fell past the cut, that factor stops
    /// being encapsulatable-to, the person loses a way back into the account, and nothing about the
    /// stored row says so. A refusal is the only response that does not hide the loss.
    /// </remarks>
    [Test]
    public async Task For_WithAManifestWiderThanTheMaximum_Throws()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UserCreatedAtUtc);
        byte[] manifest = Bytes(length: 4097, filler: 0xAB);

        // Act
        ValidationException exception = ThrowsValidationException(
            () => FactorManifest.For(user, manifest, rotationEpoch: 1));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(FactorManifest.Manifest))).IsTrue();
    }

    /// <summary>
    /// Exactly <see cref="FactorManifest.MaximumBytes" /> is accepted — the boundary is inclusive on
    /// that side.
    /// </summary>
    /// <remarks>
    /// The companion to the refusal above: without this case a guard written <c>&gt;=</c> instead of
    /// <c>&gt;</c> would also make every other test here pass, since none of them exercises the width
    /// exactly at the line.
    /// </remarks>
    [Test]
    public async Task For_WithExactlyTheMaximumBytes_Succeeds()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UserCreatedAtUtc);
        byte[] manifest = Bytes(length: 4096, filler: 0xAB);

        // Act
        FactorManifest result = FactorManifest.For(user, manifest, rotationEpoch: 1);

        // Assert
        await Assert.That(result.Manifest.Length).IsEqualTo(4096);
    }

    /// <summary>Exactly <see cref="FactorManifest.MinimumRotationEpoch" /> is accepted.</summary>
    /// <remarks>
    /// The companion to the epoch refusal above: without this case a guard written <c>&gt;</c> instead
    /// of <c>&gt;=</c> would refuse the very first manifest every account writes.
    /// </remarks>
    [Test]
    public async Task For_WithExactlyTheMinimumRotationEpoch_Succeeds()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UserCreatedAtUtc);
        byte[] manifest = Bytes(length: 16, filler: 0xAB);

        // Act
        FactorManifest result = FactorManifest.For(user, manifest, rotationEpoch: 1);

        // Assert
        await Assert.That(result.RotationEpoch).IsEqualTo(1);
    }

    /// <summary>
    /// The entity copies the manifest bytes, so a caller still holding the buffer cannot change what was
    /// filed.
    /// </summary>
    /// <remarks>
    /// The rule <c>WrappedAccountKeys.For</c> already keeps for its two envelopes.
    /// <see cref="ReadOnlyMemory{T}" /> is a view, not a value: the source array is mutated only after
    /// <see cref="FactorManifest.For" /> returns, so comparing content at assertion time alone would
    /// pass an aliasing implementation — the mutation has to happen in between, and the re-read has to
    /// come from the entity, not from the array still in scope. The bytes are distinct for the reason
    /// <see cref="For_WithAWellFormedManifest_StoresOwnerBytesAndEpoch" /> gives: over a run of one
    /// repeated byte, "position 0 still holds what position 0 held" is true of a reversal and a
    /// rotation as well as of a copy, so the assertion would not be about position at all.
    /// </remarks>
    [Test]
    public async Task For_CopiesTheManifestRatherThanAliasingIt()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UserCreatedAtUtc);
        byte[] manifest = DistinctBytes(length: 16);
        byte filedFirstByte = manifest[0];
        FactorManifest result = FactorManifest.For(user, manifest, rotationEpoch: 1);

        // Act
        manifest[0] ^= 0xFF;

        // Assert
        await Assert.That(result.Manifest.Span[0]).IsEqualTo(filedFirstByte);
    }

    /// <summary>
    /// No user, nothing to file — and the answer is an argument exception, not a validation one.
    /// </summary>
    /// <remarks>
    /// The owner is read off the user, so there is nothing to validate without one — the same
    /// distinction <c>WrappedAccountKeys.For</c> draws for its credential. The exception type is
    /// asserted, not just caught, because an implementation with no guard at all still throws — a
    /// <see cref="NullReferenceException" /> reading <c>user.Id</c> — and that is a different type. The
    /// parameter name is asserted too, because <c>ArgumentNullException.ThrowIfNull(user, "manifest")</c>
    /// throws the right type under the wrong name and a type-only check would not catch it.
    /// </remarks>
    [Test]
    public async Task For_WithNoUser_ThrowsArgumentNullException()
    {
        // Arrange
        byte[] manifest = Bytes(length: 16, filler: 0xAB);

        // Act
        ArgumentNullException exception = ThrowsArgumentNullException(
            () => FactorManifest.For(null!, manifest, rotationEpoch: 1));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("user");
    }

    /// <summary>
    /// Builds an array of <paramref name="length" /> bytes, every one <paramref name="filler" /> — for
    /// the cases where only the width of the manifest is under test.
    /// </summary>
    private static byte[] Bytes(int length, byte filler)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, filler);
        return bytes;
    }

    /// <summary>
    /// Builds <paramref name="length" /> bytes no two of which are equal, for the cases that assert
    /// <em>which byte landed where</em> rather than merely what the manifest is made of.
    /// </summary>
    /// <remarks>
    /// A run of one repeated byte is its own reversal and its own rotation, so an assertion over it
    /// cannot tell a copy from a rebuild. The step is coprime with 256, which is what keeps every value
    /// distinct up to a length of 256 — well past the sixteen bytes the callers here ask for — and the
    /// offset is chosen so that no index holds its own value, so an off-by-one shift stays visible too.
    /// </remarks>
    private static byte[] DistinctBytes(int length)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)(0x11 + (index * 7));
        }

        return bytes;
    }

    /// <summary>
    /// Runs <paramref name="action" /> and returns the validation exception it threw, so the assertions
    /// above can name the field the refusal is keyed on.
    /// </summary>
    private static ValidationException ThrowsValidationException(Action action)
    {
        try
        {
            action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }

    /// <summary>Runs <paramref name="action" /> and returns the argument-null exception it threw.</summary>
    private static ArgumentNullException ThrowsArgumentNullException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentNullException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ArgumentNullException.");
    }
}
