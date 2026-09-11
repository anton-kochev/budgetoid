using Domain.Common;
using Domain.Users;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The staging row a key rotation runs under: the next generation of the account's two wrapped keys,
/// held beside the generation still in force until a single completion step promotes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rotation is chunked across several requests, which is the whole reason this type exists.</b>
/// Re-wrapping every narrative column under a new content key is not one request's worth of work, so
/// the old generation has to stay readable while the new one is being written. Overwriting
/// <see cref="WrappedAccountKeys" /> in place would make the account unreadable from the first chunk
/// until the last, and unrecoverable if the run were abandoned in between. The new envelopes are
/// therefore filed here first and moved once, at the end.
/// </para>
/// <para>
/// <b><c>UserId</c> is the primary key, and that is the rule rather than a column choice.</b> "At most
/// one rotation in flight per account" is the invariant the chunking depends on — two concurrent runs
/// would each re-wrap a subset of the same rows under a different content key, and the account would
/// end holding columns sealed under two keys with no record of which is which. Keyed on the user, a
/// second <c>Begin</c> for an account that already has one collides on the primary key and is refused
/// by the database, which is the lowest layer that can say so declaratively — what
/// <see href="../../../docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md">ADR 0002</see>
/// asks for. Nothing below tests that: a primary key is not this factory's to enforce, and a unit test
/// that asserted it would be testing a fake. What is tested here is only what the factory decides.
/// </para>
/// <para>
/// <b>Every refusal below is <see cref="WrappedAccountKeys" />'s refusal, restated.</b> The two types
/// carry the same material — two AEAD envelopes over the same two 32-byte keys, under the same factor
/// identifier — so a rule one of them keeps and the other does not is a hole that opens on the day a
/// rotation is run rather than on the day one is written.
/// </para>
/// <para>
/// <b>The width and the version are written out below as literals, and that is load-bearing rather
/// than lazy.</b> <c>WrappedAccountKeysTests</c> makes the general argument — a test taking its bound
/// from the type under test agrees with any bound that type later chooses — and this file needs one
/// more sentence on top of it. <see cref="KeyRotation" /> does not restate either value: it reads
/// <see cref="WrappedAccountKeys.EnvelopeLength" /> and
/// <see cref="WrappedAccountKeys.EnvelopeVersion" />, precisely so the two types cannot disagree. So a
/// test that also read those constants would compare the constant against itself, and every case below
/// would move whenever the thing it checks moves. The literals here are the <em>only</em> independent
/// statement of the two values anywhere on this path, which is the entire reason they are literals.
/// </para>
/// <para>
/// <b>One refusal is deliberately absent: there is no test that a non-UTC <c>startedAtUtc</c> is
/// rejected.</b> <see cref="WrappedAccountKeys.For" /> does not reject one — it stores
/// <c>createdAtUtc</c> whatever its <see cref="DateTimeKind" />, and nothing in the Domain names
/// <see cref="DateTimeKind" /> at all. What refuses a local <see cref="DateTime" /> is the
/// <c>timestamptz</c> column underneath, which is the lower layer and already declarative. Adding the
/// check here and nowhere else would make the two sibling types disagree about the same column type,
/// which is the one thing this file exists to prevent.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a pin nothing can redden is decoration:
/// </para>
/// <list type="bullet">
/// <item>
/// "both envelopes land in their own column" —
/// <see cref="Begin_CopiesTheCredentialsOwnerAndEveryStagedValueOntoTheRow" /> gives the two columns
/// <em>different</em> bytes and names which is which. Two envelopes filled with the same value would
/// let a factory that assigned one argument twice pass, and a swapped pair is the one mistake at this
/// layer that no width check, no version check and no database constraint can see. The byte comparison
/// is ordered on purpose — TUnit's bare <c>IsEquivalentTo</c> defaults to
/// <see cref="CollectionOrdering.Any" />, which would pass on a permutation.
/// </item>
/// <item>
/// "exactly one legal width" — <see cref="Begin_WithAnEnvelopeOfTheWrongWidth_Throws" /> takes the
/// bound from both sides and on both columns, so a check written <c>&gt;=</c> or <c>&lt;=</c> fails
/// exactly one case rather than none; the accepting tests are the control at the boundary itself.
/// </item>
/// <item>
/// "width is checked <em>before</em> version" — <see cref="Begin_WithAnEmptyEnvelope_Throws" />, which
/// is the only case that can tell the order, because an empty envelope is the only input for which the
/// wrong order is not merely untidy but a different exception out of a different type.
/// </item>
/// <item>
/// "a recognised envelope version" — <see cref="Begin_WithAnUnknownEnvelopeVersion_Throws" />, whose
/// control is that every accepting test feeds an envelope carrying
/// <see cref="EnvelopeVersion" /> and would go red if the check refused everything.
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
    /// The only legal width of an envelope over a 32-byte key.
    /// </summary>
    /// <remarks>
    /// Written out rather than read off <see cref="WrappedAccountKeys.EnvelopeLength" />, for the reason
    /// the class remarks give and <c>WrappedAccountKeysTests.EnvelopeLength</c> and
    /// <c>RecoveryCodeHashTests.VerifierLength</c> both give before it. The arithmetic is
    /// <c>1 (version) + 12 (nonce) + 32 (ciphertext) + 16 (tag)</c> — AES-GCM ciphertext is the length
    /// of its plaintext, and the plaintext is a 32-byte key, so an envelope over a wrapped key has
    /// exactly one legal size. This is not a cap. The sibling test class states the same literal for its
    /// own type, so the two files agree by both naming the number rather than by both dereferencing the
    /// same symbol.
    /// </remarks>
    private const int EnvelopeLength = 61;

    /// <summary>The one envelope version defined today (IFR-007): AES-256-GCM, 96-bit nonce, 128-bit tag.</summary>
    private const byte EnvelopeVersion = 1;

    /// <summary>Fixed instant, so nothing here depends on the wall clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The owner, both identifiers, both envelopes and the instant all reach the row, each in its own
    /// place.
    /// </summary>
    /// <remarks>
    /// The two envelopes carry different filler bytes on purpose. This is the only test at this layer
    /// that can catch a factory assigning the content argument to the index column: both values are the
    /// same width, both carry the same version byte, both columns are <c>NOT NULL</c>, so every width
    /// check, every version check and every database constraint is satisfied by the swap. The associated
    /// data of each envelope binds its purpose, so a swapped pair fails to open in the browser — and a
    /// rotation is the worst place to find that out, because by then the old generation has been
    /// promoted away.
    /// </remarks>
    [Test]
    public async Task Begin_CopiesTheCredentialsOwnerAndEveryStagedValueOntoTheRow()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        Guid factorId = Guid.CreateVersion7();
        Guid rotationId = Guid.CreateVersion7();
        byte[] content = Envelope(0xC0);
        byte[] index = Envelope(0x1D);

        // Act
        KeyRotation rotation = KeyRotation.Begin(passkey, factorId, rotationId, content, index, UtcNow);

        // Assert
        await Assert.That(rotation.UserId).IsEqualTo(passkey.UserId);
        await Assert.That(rotation.RotationId).IsEqualTo(rotationId);
        await Assert.That(rotation.FactorId).IsEqualTo(factorId);
        await Assert.That(rotation.WrappedContentKey.ToArray())
            .IsEquivalentTo(content, CollectionOrdering.Matching);
        await Assert.That(rotation.WrappedIndexKey.ToArray())
            .IsEquivalentTo(index, CollectionOrdering.Matching);
        await Assert.That(rotation.StartedAtUtc).IsEqualTo(UtcNow);
    }

    /// <summary>
    /// A rotation begun under the account's federated Google credential is refused.
    /// </summary>
    /// <remarks>
    /// The refusal <see cref="WrappedAccountKeys.For" /> already keeps, and for the same reason.
    /// Identity and key custody are two tiers and the provider is only ever on the first: OAuth has no
    /// PRF equivalent, so there is no key-encryption key a federated credential could have wrapped the
    /// new generation under. Staged here, the promotion at the end of the run would file two envelopes
    /// nothing in the world can open over the two that still opened.
    /// </remarks>
    [Test]
    public async Task Begin_AgainstAFederatedCredential_Throws()
    {
        // Arrange
        Credential federated = Credential.CreateFederated(
            Guid.CreateVersion7(), Credential.GoogleProvider, "google-subject", UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => KeyRotation.Begin(
            federated, Guid.CreateVersion7(), Guid.CreateVersion7(), Envelope(0xC0), Envelope(0x1D), UtcNow));

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
    /// set of codes happily.</b> The difference is that a set is ten factors under one credential, each
    /// with its own key-encryption key, so "the factor this rotation is begun under" is not a question a
    /// set of codes answers — there are ten answers and the server can tell them apart only by a
    /// <c>factorId</c> the client would have to choose without proving it holds the matching code. The
    /// contract says so in the parameter's name: <c>Begin</c> takes a <c>passkey</c>, not a credential.
    /// The rotation's other factors get their new envelopes re-wrapped during the run, not at its start.
    /// It follows from the same place at the other end: beginning is gated on a server-verified passkey
    /// <em>assertion</em>, and a set of codes produces no assertion to verify.
    /// <para>
    /// <b>The product consequence, stated here because a reader will hit it and file it as a bug.</b>
    /// Somebody who has lost their authenticator and signed in with a recovery code <b>cannot begin a
    /// rotation at all</b> until they register a new passkey. That is the position, not an oversight:
    /// the run rewraps every narrative column in the account under a new content key, and a factor whose
    /// secret was typed into a form is not the thing that should be able to authorise it. Register a
    /// passkey first; the rotation is available from that passkey.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Begin_AgainstARecoveryCodeCredential_Throws()
    {
        // Arrange
        Credential recoveryCodes = Credential.CreateRecoveryCodes(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => KeyRotation.Begin(
            recoveryCodes, Guid.CreateVersion7(), Guid.CreateVersion7(), Envelope(0xC0), Envelope(0x1D), UtcNow));

        // Assert
        await Assert.That(exception.Errors).IsNotEmpty();
    }

    /// <summary>
    /// An empty factor identifier is refused.
    /// </summary>
    /// <remarks>
    /// All-zeros is a storable <c>uuid</c>, and it is what an unbound form control, a field read before
    /// it was set, or a client that forgot to mint one sends. It is also the associated data of both
    /// staged envelopes, so nothing downstream can tell a deliberate zero from a mistake — and at
    /// promotion time it is what decides which <see cref="WrappedAccountKeys" /> row the new generation
    /// belongs to. A zero there does not fail; it points at no factor.
    /// </remarks>
    [Test]
    public async Task Begin_WithAnEmptyFactorIdentifier_Throws()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.Empty, Guid.CreateVersion7(), Envelope(0xC0), Envelope(0x1D), UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(KeyRotation.FactorId))).IsTrue();
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
            passkey, Guid.CreateVersion7(), Guid.Empty, Envelope(0xC0), Envelope(0x1D), UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(KeyRotation.RotationId))).IsTrue();
    }

    /// <summary>
    /// An envelope that is not exactly <see cref="EnvelopeLength" /> bytes is refused,
    /// from both sides and on both columns.
    /// </summary>
    /// <remarks>
    /// Refused rather than padded or truncated, the choice <see cref="WrappedAccountKeys" /> argues:
    /// either repair stores a well-formed row holding an envelope whose tag cannot verify. Here that is
    /// worse than there, because the row is staged to be promoted — the account would look rotated, and
    /// the generation that did open would already have been replaced.
    /// </remarks>
    [Test]
    [Arguments(EnvelopeLength - 1)]
    [Arguments(EnvelopeLength + 1)]
    public async Task Begin_WithAnEnvelopeOfTheWrongWidth_Throws(int width)
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        byte[] misshapen = Envelope(0xC0, width);

        // Act
        ValidationException onContent = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), Guid.CreateVersion7(), misshapen, Envelope(0x1D), UtcNow));
        ValidationException onIndex = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), Guid.CreateVersion7(), Envelope(0xC0), misshapen, UtcNow));

        // Assert
        await Assert.That(onContent.Errors.ContainsKey(nameof(KeyRotation.WrappedContentKey))).IsTrue();
        await Assert.That(onIndex.Errors.ContainsKey(nameof(KeyRotation.WrappedIndexKey))).IsTrue();
    }

    /// <summary>
    /// An empty envelope is refused as a validation failure, on both columns — not as an index out of
    /// range.
    /// </summary>
    /// <remarks>
    /// <b>This is the only case that pins the ORDER of the two checks, and the order is a real rule.</b>
    /// <c>WrappedAccountKeys.DescribeMalformedEnvelope</c> says so in a comment beside itself — width
    /// before version, "and not merely for message quality: an empty envelope has no leading byte to
    /// read". Zero bytes is the one input where getting that backwards is not untidy but wrong in kind:
    /// reading <c>Span[0]</c> first throws <see cref="IndexOutOfRangeException" /> out of the domain, so
    /// the refusal arrives as a 500 rather than as the 400 every other malformed envelope gets, and a
    /// client that sent an unset field is told the server broke.
    /// <para>
    /// It is a separate test rather than a third <c>[Arguments]</c> row on the width case above, because
    /// it pins a different claim. The width cases say "not 60, not 62"; this one says "and the check
    /// that answers is the width one". Folded in, the sentence explaining why zero is special would have
    /// to be written in the width test's remarks about two cases it does not apply to.
    /// </para>
    /// <para>
    /// Empty is also not a hypothetical shape. It is what a client sends for a field it never set, and
    /// <c>ReadOnlyMemory&lt;byte&gt;</c>'s default value is exactly this — so a caller who declared the
    /// parameter and forgot to assign it arrives here rather than at a compiler error.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Begin_WithAnEmptyEnvelope_Throws()
    {
        // Arrange — width 0, so the helper writes no version byte and there is nothing at index 0.
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        byte[] empty = Envelope(0xC0, 0);

        // Act — ThrowsValidationException catches only ValidationException, so an IndexOutOfRangeException
        // from a version check reached before the width check escapes it and reddens this test as an
        // error rather than passing quietly. That is the mechanism doing the work here.
        ValidationException onContent = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), Guid.CreateVersion7(), empty, Envelope(0x1D), UtcNow));
        ValidationException onIndex = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), Guid.CreateVersion7(), Envelope(0xC0), empty, UtcNow));

        // Assert
        await Assert.That(onContent.Errors.ContainsKey(nameof(KeyRotation.WrappedContentKey))).IsTrue();
        await Assert.That(onIndex.Errors.ContainsKey(nameof(KeyRotation.WrappedIndexKey))).IsTrue();
    }

    /// <summary>
    /// An envelope whose leading version byte is not the one version defined today is refused, on both
    /// columns.
    /// </summary>
    /// <remarks>
    /// IFR-007 puts the version rule on the server because the successor does not exist: a staged row
    /// carrying version 2 is a client claiming a contract this deployment has never implemented, and
    /// promoting it files bytes no version of this system can interpret over bytes it could.
    /// </remarks>
    [Test]
    public async Task Begin_WithAnUnknownEnvelopeVersion_Throws()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        byte[] successor = Envelope(0xC0);
        successor[0] = EnvelopeVersion + 1;

        // Act
        ValidationException onContent = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), Guid.CreateVersion7(), successor, Envelope(0x1D), UtcNow));
        ValidationException onIndex = ThrowsValidationException(() => KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), Guid.CreateVersion7(), Envelope(0xC0), successor, UtcNow));

        // Assert
        await Assert.That(onContent.Errors.ContainsKey(nameof(KeyRotation.WrappedContentKey))).IsTrue();
        await Assert.That(onIndex.Errors.ContainsKey(nameof(KeyRotation.WrappedIndexKey))).IsTrue();
    }

    /// <summary>
    /// The entity copies both envelopes, so a caller still holding the buffer cannot change what was
    /// staged.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="WrappedAccountKeys.For" /> and <see cref="PasskeyPublicKey.Register" /> both
    /// keep. <c>ReadOnlyMemory&lt;byte&gt;</c> is a view, not a value: without a copy, the row and the
    /// caller's array are the same bytes. A rotation is where that bites hardest — the caller in this
    /// path is minting a generation of envelopes and has every reason to be reusing one buffer.
    /// </remarks>
    [Test]
    public async Task Begin_CopiesTheEnvelopesRatherThanAliasingThem()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        byte[] content = Envelope(0xC0);
        byte[] index = Envelope(0x1D);
        KeyRotation rotation = KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), Guid.CreateVersion7(), content, index, UtcNow);

        // Act
        content[EnvelopeLength - 1] ^= 0xFF;
        index[EnvelopeLength - 1] ^= 0xFF;

        // Assert — the expected values are cast, because TUnit's IsEqualTo(1) against a byte compiles
        // and then throws at run time on the comparison rather than failing the assertion.
        await Assert.That(rotation.WrappedContentKey.Span[EnvelopeLength - 1])
            .IsEqualTo((byte)0xC0);
        await Assert.That(rotation.WrappedIndexKey.Span[EnvelopeLength - 1])
            .IsEqualTo((byte)0x1D);
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
                null!, Guid.CreateVersion7(), Guid.CreateVersion7(), Envelope(0xC0), Envelope(0x1D), UtcNow))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// A well-formed envelope: the version byte the contract defines, then filler.
    /// </summary>
    /// <remarks>
    /// Built from this class's own <see cref="EnvelopeLength" /> and <see cref="EnvelopeVersion" />, which
    /// are literals, for the reason the class remarks give: <see cref="KeyRotation" /> reads
    /// <see cref="WrappedAccountKeys" />'s constants rather than restating them, so a helper that read
    /// them too would feed the type under test whatever that type currently believes and no case below
    /// could fail. The filler is not a nonce and not a ciphertext — nothing at this layer inspects
    /// either. What matters is that the two columns can be told apart by eye in a failure message.
    /// <paramref name="width" /> may be zero, which is the one input that leaves no leading byte to
    /// read; the guard below is what lets <see cref="Begin_WithAnEmptyEnvelope_Throws" /> use this helper
    /// rather than an empty array of its own.
    /// </remarks>
    private static byte[] Envelope(byte filler, int width = EnvelopeLength)
    {
        byte[] envelope = new byte[width];
        Array.Fill(envelope, filler);

        if (width > 0)
        {
            envelope[0] = EnvelopeVersion;
        }

        return envelope;
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
}
