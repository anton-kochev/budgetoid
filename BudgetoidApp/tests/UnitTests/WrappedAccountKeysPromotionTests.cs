using Domain.Users;
using ValidationException = Domain.Common.ValidationException;

namespace UnitTests;

/// <summary>
/// The one member that overwrites a live factor's copy of the account keys:
/// <c>WrappedAccountKeys.Promote(KeyRotationSeal)</c>, which adopts a staged seal's bytes on a
/// <b>loaded</b> row and refuses a seal that does not belong to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own file rather than rows added to <c>WrappedAccountKeysTests</c>.</b> That file is about
/// what may be <em>filed</em> — the factory's four refusals over two envelopes — and this member is
/// about what may be <em>destroyed</em>. The two are not degrees of the same thing: every refusal over
/// there costs a caller one rejected registration, and a wrong answer here costs the account the only
/// copy of the generation still in force, with no repair path and nothing thrown.
/// </para>
/// <para>
/// <b>An instance method taking the loaded seal, and both halves of that signature are load-bearing.</b>
/// An <em>instance</em> because the row has to be the one the change tracker is holding — a detached
/// one mutated here is an object no save will look at, which is a <c>200</c> that moved nothing, the
/// hazard <c>FactorManifest.Promote</c> spends a paragraph on. The <em>loaded seal</em> rather than a
/// loose <see cref="Guid" /> and a buffer, because that is what gives this member two independent
/// statements of an owner and two of a factor — the row's and the seal's — so it can <b>refuse when
/// they disagree</b>. It is the argument <see cref="KeyRotationSeal.For" /> already makes one ring out,
/// and it is worth more here: that factory's mistake is a row that fails to insert, and this one's is a
/// row that is overwritten with somebody else's ciphertext.
/// </para>
/// <para>
/// <b>What no test in this file can reach:</b> whether the mutation is flushed. That is a fact about
/// the change tracker and the save that follows, which needs a database —
/// <c>CompleteKeyRotationHandlerTests</c> holds the half a fake can express, and the integration tier
/// owns the rest.
/// </para>
/// </remarks>
public sealed class WrappedAccountKeysPromotionTests
{
    /// <summary>
    /// The one legal width of an encapsulated pair of account keys, the one legal width of a wrapped
    /// private key, and the one framing version defined today for either suite.
    /// </summary>
    /// <remarks>
    /// <b>Literals rather than the constants on <see cref="WrappedAccountKeys" />, which is the idiom
    /// <c>WrappedAccountKeysTests</c> keeps and states its reason for.</b> Every type on this path reads
    /// those constants, so a test that read them too would feed the code under test whatever that code
    /// currently believes, and a framing that moved by an ephemeral point would be accepted by a green
    /// suite. 158 is <c>version(1) ‖ ephemeral public key(65) ‖ nonce(12) ‖ ciphertext(64) ‖ tag(16)</c>;
    /// 167 is <c>version(1) ‖ nonce(12) ‖ ciphertext(138) ‖ tag(16)</c>. Both are widths and not caps.
    /// </remarks>
    private const int EncapsulatedBytes = 158;

    /// <inheritdoc cref="EncapsulatedBytes" />
    private const int WrappedPrivateKeyBytes = 167;

    /// <inheritdoc cref="EncapsulatedBytes" />
    private const byte EnvelopeVersion = 1;

    /// <summary>
    /// The fillers this file's payloads carry, one distinct value per role, so a value read back from
    /// the wrong place is visible rather than being two identical buffers.
    /// </summary>
    private const byte StoredFiller = 0x40;

    /// <inheritdoc cref="StoredFiller" />
    private const byte StagedFiller = 0x10;

    /// <inheritdoc cref="StoredFiller" />
    private const byte StrangerFiller = 0xF1;

    /// <summary>Fixed instant for every entity here, so nothing depends on the wall clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 9, 10, 11, 12, 13, DateTimeKind.Utc);

    /// <summary>
    /// A factor handed its own staged seal adopts that seal's bytes and moves nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control, and without it every refusal below is satisfied by a member that refuses
    /// everything. The comparison is in order, because <c>IsEquivalentTo</c> defaults to
    /// <c>CollectionOrdering.Any</c> and for a value nothing on this side can read, position is the
    /// whole of what "the bytes the client staged" means.
    /// </para>
    /// <para>
    /// <b>The wrapped private key is asserted untouched beside it, and it is not decoration.</b> A
    /// rotation changes which keys the account is sealed under; it does not touch the factor's own key
    /// pair, whose private half is wrapped under a key-encryption key this run never had — that is the
    /// whole reason a rotation needs no authenticator but the one already in the person's hand. A
    /// promotion that wrote both columns would need a value for the second that nobody staged.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Promote_WithTheRowsOwnSeal_AdoptsTheStagedEncapsulatedAccountKeys()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        Guid factorId = Guid.CreateVersion7();
        Credential passkey = Credential.CreatePasskey(userId, UtcNow);
        WrappedAccountKeys factor = Factor(passkey, factorId, StoredFiller);
        KeyRotationSeal seal = Seal(passkey, factor, StagedFiller);

        // The premise, so the assertion below cannot pass on two buffers that happened to match.
        await Assert.That(factor.EncapsulatedAccountKeys.ToArray().SequenceEqual(
                seal.EncapsulatedAccountKeys.ToArray()))
            .IsFalse();

        // Act
        factor.Promote(seal);

        // Assert
        await Assert.That(factor.EncapsulatedAccountKeys.ToArray()
                .SequenceEqual(seal.EncapsulatedAccountKeys.ToArray()))
            .IsTrue();

        // And nothing else on the row moved. The factor's own key pair survives the rotation untouched.
        await Assert.That(factor.WrappedPrivateKey.ToArray()
                .SequenceEqual(Payload(WrappedPrivateKeyBytes, EnvelopeVersion, StoredFiller)))
            .IsTrue();
        await Assert.That(factor.UserId).IsEqualTo(userId);
        await Assert.That(factor.FactorId).IsEqualTo(factorId);
        await Assert.That(factor.CredentialId).IsEqualTo(passkey.Id);
        await Assert.That(factor.CreatedAtUtc).IsEqualTo(UtcNow);
    }

    /// <summary>
    /// A seal staged by <b>another account's</b> rotation is refused, and the row keeps its bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two identifiers are separated on purpose, and this case moves only the owner.</b> The
    /// stranger's seal names the <em>same</em> factor identifier, so the only thing wrong with it is
    /// whose run staged it — which means a member that compared factors alone waves it through and
    /// overwrites this account's copy of its keys with ciphertext encapsulated to a public key nobody
    /// here holds. The account would still have a well-formed 158-byte value of the right version on
    /// every row, and every factor would open nothing.
    /// </para>
    /// <para>
    /// <b>Keyed on <c>UserId</c>, which is the column the disagreement is about</b>, the spelling
    /// <see cref="KeyRotationSeal.For" /> already uses for the identical comparison. The error count is
    /// asserted too: exactly one, so this case cannot pass on a member that refuses everything with
    /// both keys set.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Promote_WithASealOfAnotherAccount_RefusesAndKeepsTheStoredValue()
    {
        // Arrange — one factor identifier, two accounts. Only the owner differs.
        Guid factorId = Guid.CreateVersion7();
        Credential mine = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        Credential theirs = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        WrappedAccountKeys factor = Factor(mine, factorId, StoredFiller);
        KeyRotationSeal stranger = Seal(theirs, Factor(theirs, factorId, StrangerFiller), StrangerFiller);

        // The premise: the factor identifiers agree, so nothing below can pass on a factor comparison.
        await Assert.That(stranger.FactorId).IsEqualTo(factor.FactorId);
        await Assert.That(stranger.UserId).IsNotEqualTo(factor.UserId);

        // Act
        ValidationException refusal = Throws<ValidationException>(() => factor.Promote(stranger));

        // Assert — keyed on the column the disagreement is about, and on nothing else.
        await Assert.That(refusal.Errors.ContainsKey(nameof(WrappedAccountKeys.UserId))).IsTrue();
        await Assert.That(refusal.Errors.Count).IsEqualTo(1);

        // And the row still holds what it held.
        await Assert.That(factor.EncapsulatedAccountKeys.ToArray()
                .SequenceEqual(Payload(EncapsulatedBytes, EnvelopeVersion, StoredFiller)))
            .IsTrue();
    }

    /// <summary>
    /// A seal staged for <b>another factor</b> of the same account is refused, and the row keeps its
    /// bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The direction an owner comparison alone misses, and the one a real run can actually reach.</b>
    /// Both seals here were staged by this account's own rotation, so every owner agrees; what is wrong
    /// is which factor the value was encapsulated to. A completion that walked its seals and its factors
    /// in two separately-ordered sequences — a dictionary's enumeration order beside a list's — would
    /// pair them off wrongly without a single identifier being foreign, and every row would end up
    /// holding a value only some other factor's private key can open. Twelve well-formed rows, no
    /// exception, no SQLSTATE, and the account opens with none of them.
    /// </para>
    /// <para>
    /// <b>Keyed on <c>FactorId</c></b>, because that is the column this disagreement is about — a
    /// different answer from the one above, so a client and a log can tell the two apart.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Promote_WithASealForAnotherFactor_RefusesAndKeepsTheStoredValue()
    {
        // Arrange — one account, two of its factors, and the seal belonging to the other one.
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        WrappedAccountKeys factor = Factor(passkey, Guid.CreateVersion7(), StoredFiller);
        WrappedAccountKeys sibling = Factor(passkey, Guid.CreateVersion7(), StoredFiller);
        KeyRotationSeal siblingsSeal = Seal(passkey, sibling, StagedFiller);

        // The premise: same account, different factor, so nothing below can pass on an owner comparison.
        await Assert.That(siblingsSeal.UserId).IsEqualTo(factor.UserId);
        await Assert.That(siblingsSeal.FactorId).IsNotEqualTo(factor.FactorId);

        // Act
        ValidationException refusal = Throws<ValidationException>(() => factor.Promote(siblingsSeal));

        // Assert
        await Assert.That(refusal.Errors.ContainsKey(nameof(WrappedAccountKeys.FactorId))).IsTrue();
        await Assert.That(refusal.Errors.Count).IsEqualTo(1);
        await Assert.That(factor.EncapsulatedAccountKeys.ToArray()
                .SequenceEqual(Payload(EncapsulatedBytes, EnvelopeVersion, StoredFiller)))
            .IsTrue();
    }

    /// <summary>
    /// No seal at all is an <see cref="ArgumentNullException" /> and not a validation error.
    /// </summary>
    /// <remarks>
    /// The rule every factory in this aggregate keeps: no user typed this, a caller handed over
    /// nothing, and there is nothing to compare the row against without it. A member that treated a
    /// missing seal as "nothing to adopt" would answer success to a factor it left behind.
    /// </remarks>
    [Test]
    public async Task Promote_WithNoSeal_ThrowsAndKeepsTheStoredValue()
    {
        // Arrange
        Credential passkey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        WrappedAccountKeys factor = Factor(passkey, Guid.CreateVersion7(), StoredFiller);

        // Act
        Throws<ArgumentNullException>(() => factor.Promote(null!));

        // Assert
        await Assert.That(factor.EncapsulatedAccountKeys.ToArray()
                .SequenceEqual(Payload(EncapsulatedBytes, EnvelopeVersion, StoredFiller)))
            .IsTrue();
    }

    /// <summary>One factor's row, built through its own factory and never by reflection.</summary>
    private static WrappedAccountKeys Factor(Credential credential, Guid factorId, byte filler) =>
        WrappedAccountKeys.For(
            credential,
            factorId,
            Payload(WrappedPrivateKeyBytes, EnvelopeVersion, filler),
            Payload(EncapsulatedBytes, EnvelopeVersion, filler),
            UtcNow);

    /// <summary>
    /// One staged seal for <paramref name="factor" />, under a rotation begun by
    /// <paramref name="passkey" />.
    /// </summary>
    /// <remarks>
    /// Through <see cref="KeyRotationSeal.For" /> rather than fabricated, so every seal this file feeds
    /// the member under test is one a real begin could have staged — which is what keeps the refusals
    /// below about the promotion rather than about a value the staging ring would never have built.
    /// </remarks>
    private static KeyRotationSeal Seal(Credential passkey, WrappedAccountKeys factor, byte filler) =>
        KeyRotationSeal.For(
            KeyRotation.Begin(passkey, Guid.CreateVersion7(), Manifest(filler), 2, UtcNow),
            factor,
            Payload(EncapsulatedBytes, EnvelopeVersion, filler));

    /// <summary>A staged manifest of distinct bytes. Nothing at this layer parses one.</summary>
    private static byte[] Manifest(byte seed) =>
        [.. Enumerable.Range(0, 24).Select(offset => (byte)(seed + offset))];

    private static byte[] Payload(int length, byte version, byte filler)
    {
        byte[] payload = new byte[length];
        Array.Fill(payload, filler);
        payload[0] = version;

        return payload;
    }

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so an exception of any other type
    /// escapes and fails the test as itself rather than as "the expected exception was not thrown" —
    /// which matters here, where a <see cref="ValidationException" /> and an
    /// <see cref="ArgumentNullException" /> are being told apart.
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
