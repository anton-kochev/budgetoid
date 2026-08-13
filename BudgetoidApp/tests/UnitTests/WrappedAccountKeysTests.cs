using Domain.Common;
using Domain.Users;

namespace UnitTests;

/// <summary>
/// What a stored pair of wrapped account keys is made of: two fixed-width AEAD envelopes, a
/// client-minted factor identifier, and a credential that stands for a recovery factor.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server can open neither envelope, and that is what every assertion here is arranged
/// around.</b> The account's content key and index key are generated in the browser and wrapped under
/// a key-encryption key derived from a factor this server never sees — a PRF output evaluated inside
/// an authenticator, or a recovery code stored only as a hash it cannot invert. So nothing below
/// decrypts anything, and nothing below can: what is checkable at this layer is the <em>shape</em> of
/// an envelope and what the entity does with it, and that is what is checked.
/// </para>
/// <para>
/// <b>Why the shape is worth refusing at all, given the server cannot read the contents.</b> An
/// envelope of the wrong width or an unrecognised version is a client and a server disagreeing about
/// the cryptographic contract, and the symptom is silent and late: the row stores, the account looks
/// registered, and the keys will not unwrap on the day somebody needs them. IFR-007 puts the version
/// rule on the server for exactly that reason, and the database restates both bounds — this factory
/// is where the mistake is cheap.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a pin nothing can redden is decoration:
/// </para>
/// <list type="bullet">
/// <item>
/// "both envelopes land in their own column" —
/// <see cref="For_CopiesTheCredentialsIdentityAndBothEnvelopesOntoTheRow" /> gives the two columns
/// <em>different</em> bytes and names which is which. Two envelopes filled with the same value would
/// let a factory that assigned one argument twice pass, and swapping the pair is the one mistake at
/// this layer that no width check, no version check and no database constraint can see.
/// </item>
/// <item>
/// "exactly 61 bytes" — <see cref="For_WithAnEnvelopeOfTheWrongWidth_Throws" /> takes the bound from
/// both sides and on both columns; the accepting tests are the control at the boundary itself, so a
/// check written <c>&gt;= 61</c> or <c>&lt;= 61</c> fails exactly one case rather than none.
/// </item>
/// <item>
/// "a recognised envelope version" — <see cref="For_WithAnUnknownEnvelopeVersion_Throws" />, whose
/// control is that the accepting tests use version 1 and would go red if the check refused
/// everything.
/// </item>
/// <item>
/// "a recovery factor, not any credential" — <see cref="For_AgainstAFederatedCredential_Throws" />
/// against <see cref="For_AgainstARecoveryCodeCredential_Stores" />, which proves the refusal is
/// about the type rather than a factory that admits passkeys only.
/// </item>
/// </list>
/// </remarks>
public sealed class WrappedAccountKeysTests
{
    /// <summary>
    /// The only legal width of an envelope over a 32-byte key.
    /// </summary>
    /// <remarks>
    /// Restated here rather than read off the entity, for the reason
    /// <c>RecoveryCodeHashTests.VerifierLength</c> gives: a test taking its bound from the type under
    /// test agrees with any bound that type later chooses. The arithmetic is
    /// <c>1 (version) + 12 (nonce) + 32 (ciphertext) + 16 (tag)</c> — AES-GCM ciphertext is the
    /// length of its plaintext, and the plaintext is a 32-byte key, so an envelope over a wrapped key
    /// has exactly one legal size. This is not a cap.
    /// </remarks>
    private const int EnvelopeLength = 61;

    /// <summary>The one envelope version defined today (IFR-007): AES-256-GCM, 96-bit nonce, 128-bit tag.</summary>
    private const byte EnvelopeVersion = 1;

    /// <summary>Fixed instant, so nothing here depends on the wall clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The identity of the credential, the factor identifier and both envelopes reach the row, each in
    /// its own place.
    /// </summary>
    /// <remarks>
    /// The two envelopes carry different filler bytes on purpose. This is the only test at this layer
    /// that can catch a factory assigning the content argument to the index column: both values are
    /// 61 bytes, both carry version 1, both columns are <c>NOT NULL</c>, so every width check, every
    /// version check and every database constraint is satisfied by the swap. The associated data of
    /// each envelope binds its purpose, so a swapped pair fails to open in the browser — months later,
    /// with no server-side symptom at all.
    /// </remarks>
    [Test]
    public async Task For_CopiesTheCredentialsIdentityAndBothEnvelopesOntoTheRow()
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        Guid factorId = Guid.CreateVersion7();
        byte[] content = Envelope(0xC0);
        byte[] index = Envelope(0x1D);

        // Act
        WrappedAccountKeys wrapped = WrappedAccountKeys.For(credential, factorId, content, index, UtcNow);

        // Assert
        await Assert.That(wrapped.CredentialId).IsEqualTo(credential.Id);
        await Assert.That(wrapped.UserId).IsEqualTo(credential.UserId);
        await Assert.That(wrapped.CredentialType).IsEqualTo(CredentialType.Passkey);
        await Assert.That(wrapped.FactorId).IsEqualTo(factorId);
        await Assert.That(wrapped.WrappedContentKey.ToArray()).IsEquivalentTo(content);
        await Assert.That(wrapped.WrappedIndexKey.ToArray()).IsEquivalentTo(index);
        await Assert.That(wrapped.CreatedAtUtc).IsEqualTo(UtcNow);
    }

    /// <summary>
    /// A set of recovery codes is a recovery factor, so it carries its own copy of both keys.
    /// </summary>
    /// <remarks>
    /// The control for the refusal below, and a rule in its own right: a passkey and a set of codes are
    /// the two factors the design has, and a check written <c>type == Passkey</c> would leave the
    /// backup factor unable to hold the keys it exists to hold.
    /// </remarks>
    [Test]
    public async Task For_AgainstARecoveryCodeCredential_Stores()
    {
        // Arrange
        Credential credential = Credential.CreateRecoveryCodes(Guid.CreateVersion7(), UtcNow);

        // Act
        WrappedAccountKeys wrapped = WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), Envelope(0xC0), Envelope(0x1D), UtcNow);

        // Assert
        await Assert.That(wrapped.CredentialType).IsEqualTo(CredentialType.RecoveryCodes);
    }

    /// <summary>
    /// Keys filed against the account's federated Google credential are refused.
    /// </summary>
    /// <remarks>
    /// Identity and key custody are two tiers and the provider is only ever on the first: OAuth has no
    /// PRF equivalent, so there is no key-encryption key a federated credential could have wrapped
    /// these envelopes under. A row here would be two envelopes nothing in the world can open, filed
    /// as if it were a way back into the account.
    /// </remarks>
    [Test]
    public async Task For_AgainstAFederatedCredential_Throws()
    {
        // Arrange
        Credential credential = Credential.CreateFederated(
            Guid.CreateVersion7(), Credential.GoogleProvider, "google-subject", UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), Envelope(0xC0), Envelope(0x1D), UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(WrappedAccountKeys.CredentialType))).IsTrue();
    }

    /// <summary>
    /// An empty factor identifier is refused.
    /// </summary>
    /// <remarks>
    /// All-zeros is a storable <c>uuid</c>, and it is what an unbound form control, a field read before
    /// it was set, or a client that forgot to mint one sends. It is also the one value two accounts can
    /// arrive at independently, so accepting it turns a unique index into a cross-account collision the
    /// second account experiences as a refusal to register. The identifier is the associated data of
    /// both envelopes, so nothing downstream can tell a deliberate zero from a mistake.
    /// </remarks>
    [Test]
    public async Task For_WithAnEmptyFactorIdentifier_Throws()
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential, Guid.Empty, Envelope(0xC0), Envelope(0x1D), UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(WrappedAccountKeys.FactorId))).IsTrue();
    }

    /// <summary>
    /// An envelope that is not exactly 61 bytes is refused, from both sides and on both columns.
    /// </summary>
    /// <remarks>
    /// Refused rather than padded or truncated. Either repair would store a well-formed row holding an
    /// envelope whose tag cannot verify, and the account would look registered until the day the keys
    /// were needed.
    /// </remarks>
    [Test]
    [Arguments(EnvelopeLength - 1)]
    [Arguments(EnvelopeLength + 1)]
    public async Task For_WithAnEnvelopeOfTheWrongWidth_Throws(int width)
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        byte[] misshapen = Envelope(0xC0, width);

        // Act
        ValidationException onContent = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), misshapen, Envelope(0x1D), UtcNow));
        ValidationException onIndex = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), Envelope(0xC0), misshapen, UtcNow));

        // Assert
        await Assert.That(onContent.Errors.ContainsKey(nameof(WrappedAccountKeys.WrappedContentKey))).IsTrue();
        await Assert.That(onIndex.Errors.ContainsKey(nameof(WrappedAccountKeys.WrappedIndexKey))).IsTrue();
    }

    /// <summary>
    /// An envelope whose leading version byte is not the one version defined today is refused, on both
    /// columns.
    /// </summary>
    /// <remarks>
    /// IFR-007 says the server rejects any value but <c>0x01</c> until a successor is defined, and the
    /// reason it is a server rule rather than a client one is that the successor does not exist yet: a
    /// row carrying version 2 is a client claiming a contract this deployment has never implemented.
    /// Accepting it stores bytes that no version of this system can interpret.
    /// </remarks>
    [Test]
    public async Task For_WithAnUnknownEnvelopeVersion_Throws()
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        byte[] successor = Envelope(0xC0);
        successor[0] = EnvelopeVersion + 1;

        // Act
        ValidationException onContent = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), successor, Envelope(0x1D), UtcNow));
        ValidationException onIndex = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), Envelope(0xC0), successor, UtcNow));

        // Assert
        await Assert.That(onContent.Errors.ContainsKey(nameof(WrappedAccountKeys.WrappedContentKey))).IsTrue();
        await Assert.That(onIndex.Errors.ContainsKey(nameof(WrappedAccountKeys.WrappedIndexKey))).IsTrue();
    }

    /// <summary>
    /// The entity copies both envelopes, so a caller still holding the buffer cannot change what was
    /// filed.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="PasskeyPublicKey.Register" /> already keeps for its COSE key.
    /// <c>ReadOnlyMemory&lt;byte&gt;</c> is a view, not a value: without a copy, the row and the
    /// caller's array are the same bytes, and a buffer reused for the next envelope rewrites a wrapped
    /// key that has already been accepted.
    /// </remarks>
    [Test]
    public async Task For_CopiesTheEnvelopesRatherThanAliasingThem()
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        byte[] content = Envelope(0xC0);
        byte[] index = Envelope(0x1D);
        WrappedAccountKeys wrapped = WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), content, index, UtcNow);

        // Act
        content[EnvelopeLength - 1] ^= 0xFF;
        index[EnvelopeLength - 1] ^= 0xFF;

        // Assert
        await Assert.That(wrapped.WrappedContentKey.Span[EnvelopeLength - 1]).IsEqualTo((byte)0xC0);
        await Assert.That(wrapped.WrappedIndexKey.Span[EnvelopeLength - 1]).IsEqualTo((byte)0x1D);
    }

    /// <summary>
    /// No credential, nothing to file — and the answer is an argument exception, not a validation one.
    /// </summary>
    /// <remarks>
    /// The distinction <see cref="RecoveryCodeHash.From" /> already draws: the owner and the type are
    /// read off the credential, so there is nothing to validate without one. No user typed this.
    /// </remarks>
    [Test]
    public async Task For_WithNoCredential_Throws()
    {
        // Act, Assert
        await Assert.That(() => WrappedAccountKeys.For(
                null!, Guid.CreateVersion7(), Envelope(0xC0), Envelope(0x1D), UtcNow))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// A well-formed envelope: the version byte the contract defines, then filler.
    /// </summary>
    /// <remarks>
    /// The filler is not a nonce and not a ciphertext — nothing at this layer inspects either. What
    /// matters is that the two columns can be told apart by eye in a failure message, which is why the
    /// callers above pass different bytes rather than the same ones.
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
