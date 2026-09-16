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
/// for <c>WrappedPrivateKeyLength</c>: a test that reads its bound off the type under test agrees with whatever
/// that type later decides the bound is, and a future edit to either constant would move both sides of
/// the comparison together and prove nothing.
/// </para>
/// <para>
/// <b><see cref="FactorManifest.Promote" /> is the other half of this file, and its refusals are not
/// <see cref="FactorManifest.For" />'s with a different arithmetic.</b> The factory's epoch guard is a
/// floor — any generation at or above the first is a legal thing to file — while the promotion's is a
/// <em>step</em>: exactly one greater than the generation the row holds, and nothing else. The
/// difference matters because the second half of that rule is held by EF's concurrency token, and the
/// token accepts <c>N + 17</c> exactly as it accepts <c>N + 1</c> — its predicate is only
/// <c>WHERE rotation_epoch = @original</c>. So a guard written <c>&gt;</c> or <c>&gt;=</c> instead of
/// <c>==</c> is not half a check: composed with a token that never looked at the step, it is no check
/// at all, and an account's generations start skipping numbers with nothing anywhere to notice.
/// <see cref="Promote_WithAnEpochThatIsNotTheNextGeneration_Throws" /> is the case that says so, and
/// <c>N + 17</c> is the argument on it that no weaker guard survives.
/// </para>
/// <para>
/// <b>Every refusal here also asserts that the row did not move</b>, which the factory's refusals have
/// no equivalent of: a factory that threw after building nothing has nothing to leave behind, while a
/// promotion is a mutation of a live, tracked entity — so a guard that assigned before it validated
/// would throw the right exception over a row already carrying the caller's bytes, and
/// <c>SaveChanges</c> on that unit of work would file them.
/// </para>
/// <para>
/// <b>What this file does not cover is the mapping.</b> <see cref="FactorManifest.Manifest" /> is a
/// <see cref="ReadOnlyMemory{T}" />, and what EF does with one is decided by
/// <c>FactorManifestConfiguration</c> rather than by anything here.
/// <c>FactorManifestMappingTests</c> holds that half — including
/// <c>.IsConcurrencyToken()</c> on the epoch, which is the <em>atomicity</em> half of the promotion
/// rule and is unreachable from an entity test: nothing in memory has an original value to compare
/// against. <c>PasskeyRepositoryTests</c> and <c>RecoveryCodeRepositoryTests</c> drive the lost race
/// against a real database.
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
    /// The next generation is accepted, and both the bytes and the epoch land on the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stored manifest is asserted against the <em>new</em> bytes and the two arrays are made
    /// disjoint by construction — <see cref="DistinctBytes" /> starts at a different offset for each —
    /// so a promotion that kept the old blob and moved only the epoch is visible. That is the shape a
    /// method assigning its epoch and forgetting its payload produces, and it is the worst of the three
    /// possible half-promotions: the row then claims a generation whose list of factors it does not
    /// carry, and a client encapsulating to what the manifest names would skip the factor this very
    /// request registered.
    /// </para>
    /// <para>
    /// The comparison is ordered for the reason
    /// <see cref="For_WithAWellFormedManifest_StoresOwnerBytesAndEpoch" /> gives, and the owner is read
    /// back too: <see cref="FactorManifest.Promote" /> has no business touching
    /// <see cref="FactorManifest.UserId" />, which is the primary key and the tenancy column at once, so
    /// a promotion that rewrote it would re-file one account's whole factor set against another.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Promote_WithTheNextGeneration_StoresTheNewBytesAndEpoch()
    {
        // Arrange — a row standing at generation 4, and a manifest unlike the one it holds.
        Guid ownerId = Guid.CreateVersion7();
        FactorManifest stored = StoredAt(ownerId, rotationEpoch: 4);
        byte[] promoted = DistinctBytes(length: 24, offset: 0x80);

        // Act
        stored.Promote(promoted, rotationEpoch: 5);

        // Assert
        await Assert.That(stored.Manifest.ToArray()).IsEquivalentTo(promoted, CollectionOrdering.Matching);
        await Assert.That(stored.RotationEpoch).IsEqualTo(5);
        await Assert.That(stored.UserId).IsEqualTo(ownerId);
    }

    /// <summary>
    /// REFUSAL — any epoch that is not exactly one greater than the stored generation is refused, and
    /// the row is left where it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>N + 17</c> is the argument that matters, and it is the one a reader is most likely to think
    /// redundant.</b> The other half of this rule is EF's optimistic concurrency token on the same
    /// property, whose predicate is <c>WHERE rotation_epoch = @original</c> — which <c>N + 17</c>
    /// satisfies exactly as <c>N + 1</c> does, because the token compares the generation the row was
    /// <em>read</em> at and never the one being written. So a guard relaxed to <c>&gt;</c> here does not
    /// leave the step half-checked; it leaves it unchecked, and an account's generations begin skipping
    /// numbers with nothing in the stack to notice. <c>N</c> and <c>N + 2</c> pin the two sides of the
    /// step, <c>N - 1</c> and the two below the floor pin that going backwards is refused by this member
    /// rather than left to the factory's floor — which this member deliberately does not restate.
    /// </para>
    /// <para>
    /// <b>The stored row is read back afterwards, and that is a separate claim from the throw.</b> A
    /// guard that assigned <see cref="FactorManifest.Manifest" /> and
    /// <see cref="FactorManifest.RotationEpoch" /> before validating would raise exactly the exception
    /// this test demands while leaving a tracked entity carrying the caller's bytes — and the next
    /// <c>SaveChanges</c> on that unit of work, for any reason at all, would commit them.
    /// </para>
    /// <para>
    /// The key is asserted as the <em>whole</em> key set rather than with a containment check, because
    /// an epoch refusal that also keyed an error on <see cref="FactorManifest.Manifest" /> would send a
    /// client's error to a control the person cannot act on: the manifest they sent was fine.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(4)]
    [Arguments(6)]
    [Arguments(21)]
    [Arguments(3)]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Promote_WithAnEpochThatIsNotTheNextGeneration_Throws(int epoch)
    {
        // Arrange — a row standing at generation 4, so the one accepted epoch is 5.
        FactorManifest stored = StoredAt(Guid.CreateVersion7(), rotationEpoch: 4);
        byte[] filed = stored.Manifest.ToArray();
        byte[] rejected = DistinctBytes(length: 24, offset: 0x80);

        // Act
        ValidationException exception = ThrowsValidationException(() => stored.Promote(rejected, epoch));

        // Assert — keyed on the epoch alone, which is the only member the caller can correct.
        await Assert.That(exception.Errors.Keys).IsEquivalentTo(new[] { nameof(FactorManifest.RotationEpoch) });

        // And nothing moved: not the generation, and not the bytes the refused request was carrying.
        await Assert.That(stored.RotationEpoch).IsEqualTo(4);
        await Assert.That(stored.Manifest.ToArray()).IsEquivalentTo(filed, CollectionOrdering.Matching);
    }

    /// <summary>
    /// REFUSAL — at <see cref="int.MaxValue" /> there is no next generation, and the successor is
    /// computed widely enough to say so instead of wrapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one case where the arithmetic, and not the comparison, is what is under test.</b>
    /// <c>RotationEpoch + 1</c> in plain <see cref="int" /> arithmetic at
    /// <see cref="int.MaxValue" /> is <see cref="int.MinValue" /> — quietly, because the runtime does
    /// not check it — so a guard written that way would <em>accept</em> <see cref="int.MinValue" /> as
    /// the next generation and file it. What catches the row afterwards is the column's own
    /// <c>CHECK</c>, which is a 500 describing a database rule for a value the entity had already
    /// blessed; the honest answer is the refusal here, keyed on the member.
    /// </para>
    /// <para>
    /// Both arguments are needed and neither implies the other. <see cref="int.MinValue" /> is the
    /// wrapped successor, and only a widened comparison refuses it. <see cref="int.MaxValue" /> is the
    /// stored value offered back, which is refused by any implementation that checks a step at all —
    /// it is here as the control that says this row is refusing <em>everything</em> rather than
    /// refusing one particular number.
    /// </para>
    /// <para>
    /// The ceiling is written out as <see cref="int.MaxValue" /> rather than as a literal, unlike the
    /// bounds elsewhere in this file, and the difference is what the constant belongs to.
    /// <c>4096</c> and <c>1</c> are decisions this product took, so reading them off the type under test
    /// would compare a constant with itself; the width of an <see cref="int" /> is not this product's
    /// to decide and cannot drift with an edit to <see cref="FactorManifest" />.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task Promote_AtTheCeilingOfTheEpochType_RefusesEveryCandidate(int epoch)
    {
        // Arrange — a row that has run out of successors.
        FactorManifest stored = StoredAt(Guid.CreateVersion7(), rotationEpoch: int.MaxValue);
        byte[] rejected = DistinctBytes(length: 24, offset: 0x80);

        // Act
        ValidationException exception = ThrowsValidationException(() => stored.Promote(rejected, epoch));

        // Assert
        await Assert.That(exception.Errors.Keys).IsEquivalentTo(new[] { nameof(FactorManifest.RotationEpoch) });
        await Assert.That(stored.RotationEpoch).IsEqualTo(int.MaxValue);
    }

    /// <summary>
    /// REFUSAL — an empty manifest is refused, and the row is left where it was.
    /// </summary>
    /// <remarks>
    /// An empty <c>bytea</c> is exactly what an unset member sends, so this is the shape a caller that
    /// forgot to attach the manifest produces — and letting it through would overwrite the account's
    /// only statement of its factor set with a row naming no factor at all, under a generation the
    /// client then believes is in force. Worse here than at <see cref="FactorManifest.For" />, because
    /// that one files a row nobody has yet and this one destroys the list the account already had.
    /// </remarks>
    [Test]
    public async Task Promote_WithAnEmptyManifest_Throws()
    {
        // Arrange
        FactorManifest stored = StoredAt(Guid.CreateVersion7(), rotationEpoch: 4);
        byte[] filed = stored.Manifest.ToArray();

        // Act — the epoch is the one legal successor, so the manifest is the only thing wrong.
        ValidationException exception = ThrowsValidationException(
            () => stored.Promote(ReadOnlyMemory<byte>.Empty, rotationEpoch: 5));

        // Assert
        await Assert.That(exception.Errors.Keys).IsEquivalentTo(new[] { nameof(FactorManifest.Manifest) });
        await Assert.That(stored.RotationEpoch).IsEqualTo(4);
        await Assert.That(stored.Manifest.ToArray()).IsEquivalentTo(filed, CollectionOrdering.Matching);
    }

    /// <summary>
    /// REFUSAL — a manifest wider than <see cref="FactorManifest.MaximumBytes" /> is refused, never
    /// truncated.
    /// </summary>
    /// <remarks>
    /// The factory's argument, and it lands harder on this path: truncation drops whichever factor's
    /// public key fell past the cut, and on a <em>promotion</em> the row being overwritten is the one
    /// that still named it. The person loses a way back into the account, the stored row is well formed,
    /// and the loss surfaces on the day they reach for the factor that is gone.
    /// </remarks>
    [Test]
    public async Task Promote_WithAManifestWiderThanTheMaximum_Throws()
    {
        // Arrange
        FactorManifest stored = StoredAt(Guid.CreateVersion7(), rotationEpoch: 4);
        byte[] filed = stored.Manifest.ToArray();

        // Act
        ValidationException exception = ThrowsValidationException(
            () => stored.Promote(Bytes(length: 4097, filler: 0x5C), rotationEpoch: 5));

        // Assert
        await Assert.That(exception.Errors.Keys).IsEquivalentTo(new[] { nameof(FactorManifest.Manifest) });
        await Assert.That(stored.RotationEpoch).IsEqualTo(4);
        await Assert.That(stored.Manifest.ToArray()).IsEquivalentTo(filed, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Exactly <see cref="FactorManifest.MaximumBytes" /> is accepted — the boundary is inclusive on
    /// that side here as well.
    /// </summary>
    /// <remarks>
    /// The companion to the refusal above, and it is not the factory's case restated: the two guards are
    /// separate code, so a <c>&gt;=</c> written on this one alone would refuse the widest legal manifest
    /// on both routes that change a factor set while every case around <see cref="FactorManifest.For" />
    /// stayed green.
    /// </remarks>
    [Test]
    public async Task Promote_WithExactlyTheMaximumBytes_Succeeds()
    {
        // Arrange
        FactorManifest stored = StoredAt(Guid.CreateVersion7(), rotationEpoch: 4);

        // Act
        stored.Promote(Bytes(length: 4096, filler: 0x5C), rotationEpoch: 5);

        // Assert
        await Assert.That(stored.Manifest.Length).IsEqualTo(4096);
        await Assert.That(stored.RotationEpoch).IsEqualTo(5);
    }

    /// <summary>
    /// An empty manifest and a wrong epoch are reported together, each under its own key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The claim is that the guards collect rather than short-circuit</b>, which is what a caller who
    /// got both wrong needs: told one at a time, they correct the manifest, retry, and are turned away
    /// again for an epoch that has meanwhile been overtaken by somebody else's registration. A
    /// <c>return</c> or a <c>throw</c> inside either guard satisfies every single-fault case above and
    /// only this one notices.
    /// </para>
    /// <para>
    /// <b>The two keys are asserted as a set, and that is the second claim.</b> The refusals are keyed on
    /// the property the value lands in, so a member reported under its neighbour's name sends a client's
    /// error to the wrong control — and with both faults present, a guard that keyed everything on one
    /// name would still produce an exception with a non-empty key.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Promote_WithBothMembersWrong_ReportsBothUnderTheirOwnKeys()
    {
        // Arrange
        FactorManifest stored = StoredAt(Guid.CreateVersion7(), rotationEpoch: 4);

        // Act — an empty manifest and an epoch two generations on.
        ValidationException exception = ThrowsValidationException(
            () => stored.Promote(ReadOnlyMemory<byte>.Empty, rotationEpoch: 6));

        // Assert
        await Assert.That(exception.Errors.Keys).IsEquivalentTo(new[]
        {
            nameof(FactorManifest.Manifest), nameof(FactorManifest.RotationEpoch),
        });
    }

    /// <summary>
    /// The two manifest refusals say different things, so a caller can tell "you sent none" from "you
    /// sent too much".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are keyed on <see cref="FactorManifest.Manifest" /> because they are one column, so the key
    /// cannot tell them apart and the sentence is all a person gets. The two remedies are opposites —
    /// attach the manifest you forgot, against send fewer factors or a tighter encoding — so one
    /// sentence covering both is a client shown advice that does not apply to what they did.
    /// </para>
    /// <para>
    /// <b>Compared rather than transcribed, on purpose.</b> Writing either sentence out here would make
    /// this file a second owner of wording <c>docs/design/voice.md</c> owns, and would redden on an
    /// edit that improved the words without changing what is distinguishable. What is asserted is only
    /// that the two differ and that neither is blank — which is the whole of the property, and is
    /// exactly what a guard collapsing the pair into one message breaks.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Promote_TellsAnEmptyManifestFromAnOverwideOne()
    {
        // Arrange — one row per refusal, because a promotion mutates and a refused one must not be
        // asked a second question through an entity another act has touched.
        FactorManifest forTheEmpty = StoredAt(Guid.CreateVersion7(), rotationEpoch: 4);
        FactorManifest forTheWide = StoredAt(Guid.CreateVersion7(), rotationEpoch: 4);

        // Act
        ValidationException empty = ThrowsValidationException(
            () => forTheEmpty.Promote(ReadOnlyMemory<byte>.Empty, rotationEpoch: 5));
        ValidationException wide = ThrowsValidationException(
            () => forTheWide.Promote(Bytes(length: 4097, filler: 0x5C), rotationEpoch: 5));

        // Assert — one key, two sentences.
        string emptySentence = string.Join(" ", empty.Errors[nameof(FactorManifest.Manifest)]);
        string wideSentence = string.Join(" ", wide.Errors[nameof(FactorManifest.Manifest)]);

        await Assert.That(string.IsNullOrWhiteSpace(emptySentence)).IsFalse();
        await Assert.That(string.IsNullOrWhiteSpace(wideSentence)).IsFalse();
        await Assert.That(emptySentence).IsNotEqualTo(wideSentence);
    }

    /// <summary>
    /// The promotion copies the manifest bytes, so a caller still holding the buffer cannot change what
    /// the row ended up carrying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="For_CopiesTheManifestRatherThanAliasingIt" />'s rule on the path where it is harder to
    /// see and worse to lose. A promotion runs against a <em>tracked</em> entity, so an aliased
    /// <see cref="ReadOnlyMemory{T}" /> is not merely a stale value in memory: whatever the buffer holds
    /// when <c>SaveChanges</c> reaches it is what the column stores, and the manifest is the sole
    /// authenticated carrier of every factor's public key. A caller reusing one array across the request
    /// would file a blob whose authentication tag covers something else, and every client reading it
    /// afterwards would refuse to open the account's factor list with nothing saying why.
    /// </para>
    /// <para>
    /// The mutation happens strictly between the call and the read, and the re-read comes off the entity
    /// rather than off the array still in scope — comparing content at assertion time alone would pass
    /// an aliasing implementation, because both sides would be the same buffer. The bytes are distinct
    /// for the reason the factory's twin gives: over a run of one repeated byte, "position 0 still holds
    /// what position 0 held" is true of a reversal and a rotation as well as of a copy.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Promote_CopiesTheManifestRatherThanAliasingIt()
    {
        // Arrange
        FactorManifest stored = StoredAt(Guid.CreateVersion7(), rotationEpoch: 4);
        byte[] promoted = DistinctBytes(length: 24, offset: 0x80);
        byte promotedFirstByte = promoted[0];
        stored.Promote(promoted, rotationEpoch: 5);

        // Act — the caller reuses the buffer after the promotion has been accepted.
        promoted[0] ^= 0xFF;

        // Assert
        await Assert.That(stored.Manifest.Span[0]).IsEqualTo(promotedFirstByte);
    }

    /// <summary>
    /// A manifest row standing at <paramref name="rotationEpoch" />, which is the arrangement every
    /// promotion case needs and the one <see cref="FactorManifest" /> offers no other way to reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built through <see cref="FactorManifest.For" /> because the constructor is private and there is
    /// no second factory — deliberately, as that member's own remarks argue. <b>What this arrangement
    /// cannot stand in for is the tracking</b>: <see cref="FactorManifest.For" /> returns a
    /// <em>detached</em> instance, so the original values EF would compare a concurrency token against
    /// do not exist here. That is the half this file says it does not hold, and it is why every case
    /// above is about the <em>step</em> and none is about the race.
    /// </para>
    /// <para>
    /// The bytes are <see cref="DistinctBytes" /> at the default offset and every promotion is handed an
    /// array built at a different one, so "the stored manifest is the new one" and "the stored manifest
    /// is the old one" are never the same assertion.
    /// </para>
    /// </remarks>
    private static FactorManifest StoredAt(Guid ownerId, int rotationEpoch) =>
        FactorManifest.For(
            User.CreateWithId(ownerId, "person@example.com", UserCreatedAtUtc),
            DistinctBytes(length: 24),
            rotationEpoch);

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
    /// distinct up to a length of 256 — well past the twenty-four bytes the callers here ask for — and
    /// the offset is chosen so that no index holds its own value, so an off-by-one shift stays visible
    /// too.
    /// <para>
    /// <paramref name="offset" /> is what makes two runs of this helper <em>disjoint</em> rather than
    /// merely both distinct, which the promotion cases need: they assert that the row carries the new
    /// manifest and not the one it already held, and two arrays built from the same offset would be the
    /// same bytes. <c>0x80</c> is a quarter-turn away from the default in a space where the step never
    /// repeats inside these lengths, so no index of one run holds a value the other run holds at that
    /// index.
    /// </para>
    /// </remarks>
    private static byte[] DistinctBytes(int length, byte offset = 0x11)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)(offset + (index * 7));
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
