using Domain.Common;
using Domain.Users;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The staging row a key rotation runs under: the generation of the factor set the run is being carried
/// out against, held beside the generation still in force until a single completion step promotes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rotation is chunked across several requests, which is the whole reason this type exists.</b>
/// Re-sealing every narrative column under a new content key is not one request's worth of work, so the
/// old generation has to stay readable while the new one is being written. The new account keys
/// themselves are <em>not</em> on this row: they live one table down, in <see cref="KeyRotationSeal" />,
/// one per surviving factor, because the run produces one value per factor and this row is keyed on the
/// account. What the run stages <em>here</em> is the factor set it committed to — the manifest and the
/// epoch it was read at.
/// </para>
/// <para>
/// <b>The two envelope columns this row used to carry are gone, and their absence is the reshape.</b>
/// Under the arrangement they belonged to, the account's keys were wrapped <em>symmetrically</em> under
/// one factor's key-encryption key, so a rotation needed that factor physically present — and the
/// staging row named a <c>factor_id</c> to say which. Every factor now holds an ECDH key pair, a run
/// encapsulates the new keys to each factor's <em>public</em> half, and a public half needs nobody
/// present. So the row names no factor at all, and the per-factor value moved to the child table. A
/// reader restoring either column would be restoring the constraint that an account with a hardware key
/// in a drawer could not rotate.
/// </para>
/// <para>
/// <b><c>UserId</c> is the primary key, and that is the rule rather than a column choice.</b> "At most
/// one rotation in flight per account" is the invariant the chunking depends on — two concurrent runs
/// would each re-seal a subset of the same rows under a different content key, and the account would end
/// holding columns sealed under two keys with no record of which is which. Keyed on the user, a second
/// <c>Begin</c> for an account that already has one collides on the primary key and is refused by the
/// database, which is the lowest layer that can say so declaratively — what
/// <see href="../../../docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md">ADR 0002</see>
/// asks for. Nothing below tests that: a primary key is not this factory's to enforce, and a unit test
/// that asserted it would be testing a fake. What is tested here is only what the factory decides.
/// </para>
/// <para>
/// <b>Every manifest refusal below is <see cref="FactorManifest" />'s refusal, restated.</b> The two
/// types carry the same bytes under the same bounds — a staged manifest is a manifest — so a rule one
/// of them keeps and the other does not is a hole that opens on the day a rotation is run rather than
/// on the day one is written. <see cref="KeyRotation.Begin" /> reads
/// <see cref="FactorManifest.MaximumBytes" /> and <see cref="FactorManifest.MinimumRotationEpoch" />
/// rather than restating them, precisely so the two cannot disagree.
/// </para>
/// <para>
/// <b>The bounds are written out below as literals, and that is load-bearing rather than lazy.</b>
/// <c>FactorManifestTests</c> makes the general argument — a test taking its bound from the type under
/// test agrees with any bound that type later chooses — and this file needs one more sentence on top of
/// it. Because <see cref="KeyRotation" /> reads the sibling's constants, a test that also read them
/// would compare the constant against itself and every case here would move whenever the thing it
/// checks moves. The literals are the second independent statement of the same numbers, beside
/// <c>FactorManifestTests</c>' own.
/// </para>
/// <para>
/// <b>One refusal is deliberately absent: there is no test that a non-UTC <c>startedAtUtc</c> is
/// rejected.</b> <see cref="WrappedAccountKeys.For" /> does not reject one — it stores
/// <c>createdAtUtc</c> whatever its <see cref="DateTimeKind" />, and nothing in the Domain names
/// <see cref="DateTimeKind" /> at all. What refuses a local <see cref="DateTime" /> is the
/// <c>timestamptz</c> column underneath, which is the lower layer and already declarative. Adding the
/// check here and nowhere else would make the sibling types disagree about the same column type, which
/// is the one thing this file exists to prevent.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a pin nothing can redden is decoration:
/// </para>
/// <list type="bullet">
/// <item>
/// "every staged value lands in its own column" —
/// <see cref="Begin_CopiesTheCredentialsOwnerAndEveryStagedValueOntoTheRow" /> uses distinct bytes and
/// an ordered comparison, because TUnit's bare <c>IsEquivalentTo</c> defaults to
/// <see cref="CollectionOrdering.Any" /> and would pass on a permutation, and a run of one repeated
/// byte has no order to compare at all.
/// </item>
/// <item>
/// "a manifest is required and bounded" — <see cref="Begin_WithAnEmptyStagedManifest_Throws" /> and
/// <see cref="Begin_WithAStagedManifestOverTheCap_Throws" />, against the accepting test as the control
/// at the boundary itself.
/// </item>
/// <item>
/// "emptiness is judged before width" — the two are one dictionary key because they are one column, so
/// the pair above cannot tell which arm answered. That is deliberate rather than a gap: unlike an
/// envelope's width-before-version, neither order here throws out of the Domain, so there is nothing
/// for a test to discriminate that a reader could act on.
/// </item>
/// <item>
/// "an epoch names a generation that exists" — <see cref="Begin_WithAnEpochBelowTheFloor_Throws" />
/// takes zero and a negative, because a check written <c>&lt;= 0</c> and one written <c>== 0</c> agree
/// on zero and disagree on the negative.
/// </item>
/// <item>
/// "a passkey, and only a passkey" — <see cref="Begin_AgainstAFederatedCredential_Throws" /> and
/// <see cref="Begin_AgainstARecoveryCodeCredential_Throws" /> against
/// <see cref="Begin_CopiesTheCredentialsOwnerAndEveryStagedValueOntoTheRow" />, which proves the
/// refusal is about the type rather than a factory that refuses everything.
/// </item>
/// </list>
/// </remarks>
public sealed class KeyRotationTests
{
    /// <summary>
    /// The widest a staged factor manifest may be.
    /// </summary>
    /// <remarks>
    /// Written out rather than read off <see cref="FactorManifest.MaximumBytes" />, for the reason the
    /// class remarks give and <c>FactorManifestTests</c> gives before it. The sibling test class states
    /// the same literal for its own type, so the two files agree by both naming the number rather than
    /// by both dereferencing the same symbol.
    /// </remarks>
    private const int MaximumManifestBytes = 4096;

    /// <summary>The lowest generation a manifest can name — generations count from one, not zero.</summary>
    private const int MinimumRotationEpoch = 1;

    /// <summary>Fixed instant, so nothing here depends on the wall clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The owner, the rotation identifier, the staged manifest, its epoch and the instant all reach the
    /// row, each in its own place.
    /// </summary>
    /// <remarks>
    /// The manifest carries distinct bytes and the comparison is ordered, both on purpose: a factory
    /// that reversed the buffer, rotated it, or rebuilt it from any run of the same value would satisfy
    /// an unordered comparison over repeated bytes. Position is the whole of what "the bytes the client
    /// wrote" means for a blob nothing on this side can read.
    /// </remarks>
    [Test]
    public async Task Begin_CopiesTheCredentialsOwnerAndEveryStagedValueOntoTheRow()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        Guid rotationId = Guid.CreateVersion7();
        byte[] manifest = DistinctBytes(length: 16);

        // Act
        KeyRotation rotation = KeyRotation.Begin(passkey, rotationId, manifest, 3, UtcNow);

        // Assert
        await Assert.That(rotation.UserId).IsEqualTo(passkey.UserId);
        await Assert.That(rotation.RotationId).IsEqualTo(rotationId);
        await Assert.That(rotation.StagedManifest.ToArray())
            .IsEquivalentTo(manifest, CollectionOrdering.Matching);
        await Assert.That(rotation.StagedRotationEpoch).IsEqualTo(3);
        await Assert.That(rotation.StartedAtUtc).IsEqualTo(UtcNow);
    }

    /// <summary>
    /// A rotation begun under the account's federated Google credential is refused.
    /// </summary>
    /// <remarks>
    /// The refusal <see cref="WrappedAccountKeys.For" /> already keeps, and for a related reason.
    /// Identity and key custody are two tiers and the provider is only ever on the first: OAuth has no
    /// PRF equivalent, so a federated credential derives no key-encryption key, holds no factor key pair
    /// and can prove possession of nothing a rotation could be authorised by.
    /// </remarks>
    [Test]
    public async Task Begin_AgainstAFederatedCredential_Throws()
    {
        // Arrange
        Credential federated = Credential.CreateFederated(
            Guid.CreateVersion7(), Credential.GoogleProvider, "google-subject", UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => KeyRotation.Begin(
            federated, Guid.CreateVersion7(), Manifest(0xAB), MinimumRotationEpoch, UtcNow));

        // Assert — keyed on nothing, because the staged row carries no credential-type column for a
        // refusal to be keyed on. WrappedAccountKeys names nameof(CredentialType) because it stores
        // one; naming the same string here would be this test choosing the entity's dictionary key for
        // it rather than checking a decision the entity took.
        await Assert.That(exception.Errors).IsNotEmpty();
    }

    /// <summary>
    /// A rotation begun under a set of recovery codes is refused.
    /// </summary>
    /// <remarks>
    /// <b>The one place this type is stricter than <see cref="WrappedAccountKeys" />, which accepts a
    /// set of codes happily.</b> Beginning is gated on a server-verified passkey <em>assertion</em>, and
    /// a set of codes produces no assertion to verify. The contract says so in the parameter's name:
    /// <c>Begin</c> takes a <c>passkey</c>, not a credential.
    /// <para>
    /// <b>The product consequence, stated here because a reader will hit it and file it as a bug.</b>
    /// Somebody who has lost their authenticator and signed in with a recovery code <b>cannot begin a
    /// rotation at all</b> until they register a new passkey. That is the position, not an oversight:
    /// the run re-seals every narrative column in the account under a new content key, and a factor
    /// whose secret was typed into a form is not the thing that should be able to authorise it. Register
    /// a passkey first; the rotation is available from that passkey. Note that this says nothing about
    /// which factors the run <em>reaches</em> — every factor the staged manifest names gets a seal,
    /// codes included, and none of them has to be present for it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Begin_AgainstARecoveryCodeCredential_Throws()
    {
        // Arrange
        Credential recoveryCodes = Credential.CreateRecoveryCodes(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => KeyRotation.Begin(
            recoveryCodes, Guid.CreateVersion7(), Manifest(0xAB), MinimumRotationEpoch, UtcNow));

        // Assert
        await Assert.That(exception.Errors).IsNotEmpty();
    }

    /// <summary>
    /// An empty rotation identifier is refused.
    /// </summary>
    /// <remarks>
    /// The identifier is what every later chunk of the run quotes to say which rotation it is continuing,
    /// and what the completion step quotes to say which one it is promoting. All-zeros is the value two
    /// accounts reach independently and the value a client sends when it has not started a run at all, so
    /// accepting it means a chunk cannot be told from a chunk of somebody else's abandoned attempt.
    /// </remarks>
    [Test]
    public async Task Begin_WithAnEmptyRotationIdentifier_Throws()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.Empty, Manifest(0xAB), MinimumRotationEpoch, UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(KeyRotation.RotationId))).IsTrue();
    }

    /// <summary>
    /// An empty staged manifest is refused.
    /// </summary>
    /// <remarks>
    /// An empty <c>bytea</c> is exactly what an unset member sends, so a caller that forgot to attach
    /// the manifest would stage a generation naming no factor at all — and the promotion would file it:
    /// an account with no way back in, stored as though it had one. The same refusal
    /// <see cref="FactorManifest.For" /> keeps over the same bytes.
    /// <para>
    /// Empty is not a hypothetical shape. <c>ReadOnlyMemory&lt;byte&gt;</c>'s default value is exactly
    /// this, so a caller who declared the parameter and forgot to assign it arrives here rather than at
    /// a compiler error.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Begin_WithAnEmptyStagedManifest_Throws()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), default, MinimumRotationEpoch, UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(KeyRotation.StagedManifest))).IsTrue();
    }

    /// <summary>
    /// A staged manifest wider than the cap is refused, and one exactly at the cap is accepted.
    /// </summary>
    /// <remarks>
    /// Both sides, because a check written <c>&gt;=</c> rather than <c>&gt;</c> refuses a manifest that
    /// is exactly legal and nothing else in this file would notice — every other case is far under the
    /// bound. Refused rather than cut, for the reason <see cref="FactorManifest" /> argues: the cut
    /// lands on whichever factor sat past the line, that factor stops being encapsulatable-to, and the
    /// row left behind is well-formed, so the loss surfaces on the day somebody reaches for the factor
    /// that is gone.
    /// </remarks>
    [Test]
    public async Task Begin_WithAStagedManifestOverTheCap_Throws()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => KeyRotation.Begin(
            passkey,
            Guid.CreateVersion7(),
            Manifest(0xAB, MaximumManifestBytes + 1),
            MinimumRotationEpoch,
            UtcNow));
        KeyRotation atTheCap = KeyRotation.Begin(
            passkey,
            Guid.CreateVersion7(),
            Manifest(0xAB, MaximumManifestBytes),
            MinimumRotationEpoch,
            UtcNow);

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(KeyRotation.StagedManifest))).IsTrue();
        await Assert.That(atTheCap.StagedManifest.Length).IsEqualTo(MaximumManifestBytes);
    }

    /// <summary>
    /// A staged rotation epoch below the floor is refused.
    /// </summary>
    /// <remarks>
    /// Epoch 0 is the answer an account with no manifest row gives, so staging it would stage a
    /// generation asserting its own absence. A negative epoch names no generation either, and one
    /// comparison refuses both — which is why both arguments are here: a check written <c>&lt;= 0</c>
    /// and one written <c>== 0</c> agree on zero and disagree on the negative.
    /// </remarks>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Begin_WithAnEpochBelowTheFloor_Throws(int epoch)
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), Manifest(0xAB), epoch, UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(KeyRotation.StagedRotationEpoch))).IsTrue();
    }

    /// <summary>
    /// The entity copies the staged manifest, so a caller still holding the buffer cannot change what
    /// was staged.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="WrappedAccountKeys.For" /> and <see cref="PasskeyPublicKey.Register" /> both
    /// keep. <c>ReadOnlyMemory&lt;byte&gt;</c> is a view, not a value: without a copy, the row and the
    /// caller's array are the same bytes. The caller on this path is assembling a generation and has
    /// every reason to be reusing one buffer.
    /// </remarks>
    [Test]
    public async Task Begin_CopiesTheStagedManifestRatherThanAliasingIt()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        byte[] manifest = Manifest(0xAB);
        KeyRotation rotation = KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), manifest, MinimumRotationEpoch, UtcNow);

        // Act
        manifest[^1] ^= 0xFF;

        // Assert — the expected value is cast, because TUnit's IsEqualTo(1) against a byte compiles and
        // then throws at run time on the comparison rather than failing the assertion.
        await Assert.That(rotation.StagedManifest.Span[^1]).IsEqualTo((byte)0xAB);
    }

    /// <summary>
    /// No credential, nothing to begin — and the answer is an argument exception, not a validation one.
    /// </summary>
    /// <remarks>
    /// The distinction <see cref="WrappedAccountKeys.For" /> and <see cref="RecoveryCodeHash.From" />
    /// both draw: the owner and the type are read off the credential, so there is nothing to validate
    /// without one. No user typed this.
    /// </remarks>
    [Test]
    public async Task Begin_WithNoCredential_Throws()
    {
        // Act, Assert
        await Assert.That(() => KeyRotation.Begin(
                null!, Guid.CreateVersion7(), Manifest(0xAB), MinimumRotationEpoch, UtcNow))
            .Throws<ArgumentNullException>();
    }

    /// <summary>A manifest of <paramref name="length" /> bytes, all <paramref name="filler" />.</summary>
    /// <remarks>
    /// Nothing at this layer parses a manifest — it is an authenticated blob the client builds and the
    /// client reads — so the content is arbitrary wherever the test is not about position.
    /// </remarks>
    private static byte[] Manifest(byte filler, int length = 16)
    {
        byte[] manifest = new byte[length];
        Array.Fill(manifest, filler);

        return manifest;
    }

    /// <summary>
    /// <paramref name="length" /> bytes that are all different, so an assertion over them is about
    /// position rather than about content.
    /// </summary>
    private static byte[] DistinctBytes(int length) =>
        [.. Enumerable.Range(1, length).Select(value => (byte)value)];

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
}
