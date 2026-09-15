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
/// <em>different</em> bytes and names which is which. Two payloads filled with the same value would
/// let a factory that assigned one argument twice pass. Swapping the pair used to be the one mistake at
/// this layer that no width check, no version check and no database constraint could see; it is now
/// refused by each column's own width, which <see cref="For_WithTheTwoPayloadsTransposed_Throws" />
/// states outright.
/// </item>
/// <item>
/// "exactly its own column's width" — <see cref="For_WithAPayloadOfTheWrongWidth_Throws" /> takes
/// each bound from both sides and on both columns; the accepting tests are the control at the boundary
/// itself, so a check written <c>&gt;=</c> or <c>&lt;=</c> fails exactly one case rather than none.
/// </item>
/// <item>
/// "and each column is judged against ITS OWN suite" —
/// <see cref="For_WithTheTwoPayloadsTransposed_Throws" />, which the two widths diverging is what made
/// writable at all.
/// </item>
/// <item>
/// "a recognised envelope version" — <see cref="For_WithAnUnknownFramingVersion_Throws" />, whose
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
    /// The only legal width of the wrapped private key — the AEAD framing over a PKCS#8 P-256 private
    /// key.
    /// </summary>
    /// <remarks>
    /// Restated here rather than read off the entity, for the reason
    /// <c>RecoveryCodeHashTests.VerifierLength</c> gives: a test taking its bound from the type under
    /// test agrees with any bound that type later chooses. The arithmetic is
    /// <c>1 (version) + 12 (nonce) + 138 (ciphertext) + 16 (tag)</c> — AES-GCM ciphertext is the length
    /// of its plaintext, and the plaintext is one PKCS#8 private key, so this column has exactly one
    /// legal size. This is not a cap.
    /// </remarks>
    private const int WrappedPrivateKeyLength = 167;

    /// <summary>The AEAD framing version (IFR-007): AES-256-GCM, 96-bit nonce, 128-bit tag.</summary>
    private const byte WrappedPrivateKeyVersion = 1;

    /// <summary>
    /// The only legal width of the encapsulated account keys — the encapsulation framing over both
    /// 32-byte keys as one plaintext.
    /// </summary>
    /// <remarks>
    /// <b>A second pair of literals rather than a shared one, and that is the whole reason this file
    /// pins four numbers instead of two.</b> The arithmetic is
    /// <c>1 (version) + 65 (ephemeral public key) + 64 (ciphertext) + 16 (tag)</c> — a different suite
    /// with a different layout, whose only resemblance to the pair above is the leading byte. Written as
    /// one shared constant, a change to either suite would move both columns' expectations together and
    /// this file would go on agreeing with whatever the entity said. The two <em>version</em> literals
    /// hold the same number today, which is exactly why they are two: nothing in the build can tell a
    /// cross-read from a correct one, so the only thing that can is a second independent statement.
    /// </remarks>
    private const int EncapsulatedAccountKeysLength = 158;

    /// <summary>
    /// The encapsulation framing version (IFR-014, IFR-015): ECDH over NIST P-256, HKDF-SHA-256, then
    /// AES-256-GCM.
    /// </summary>
    private const byte EncapsulatedAccountKeysVersion = 1;

    /// <summary>Fixed instant, so nothing here depends on the wall clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The identity of the credential, the factor identifier and both envelopes reach the row, each in
    /// its own place.
    /// </summary>
    /// <remarks>
    /// The two payloads carry different filler bytes and different widths on purpose. This is the test
    /// that catches a factory assigning one argument to both columns — which no width check would
    /// notice if the two widths agreed, and which the associated data of each value would only expose
    /// in a browser, months later, with no server-side symptom at all.
    /// </remarks>
    [Test]
    public async Task For_CopiesTheCredentialsIdentityAndBothEnvelopesOntoTheRow()
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        Guid factorId = Guid.CreateVersion7();
        byte[] content = PrivateKey(0xC0);
        byte[] index = AccountKeys(0x1D);

        // Act
        WrappedAccountKeys wrapped = WrappedAccountKeys.For(credential, factorId, content, index, UtcNow);

        // Assert
        await Assert.That(wrapped.CredentialId).IsEqualTo(credential.Id);
        await Assert.That(wrapped.UserId).IsEqualTo(credential.UserId);
        await Assert.That(wrapped.CredentialType).IsEqualTo(CredentialType.Passkey);
        await Assert.That(wrapped.FactorId).IsEqualTo(factorId);
        await Assert.That(wrapped.WrappedPrivateKey.ToArray()).IsEquivalentTo(content);
        await Assert.That(wrapped.EncapsulatedAccountKeys.ToArray()).IsEquivalentTo(index);
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
            credential, Guid.CreateVersion7(), PrivateKey(0xC0), AccountKeys(0x1D), UtcNow);

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
            credential, Guid.CreateVersion7(), PrivateKey(0xC0), AccountKeys(0x1D), UtcNow));

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
            credential, Guid.Empty, PrivateKey(0xC0), AccountKeys(0x1D), UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(WrappedAccountKeys.FactorId))).IsTrue();
    }

    /// <summary>
    /// A payload that is not exactly its own column's width is refused, from both sides and on both
    /// columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refused rather than padded or truncated. Either repair would store a well-formed row holding
    /// bytes whose tag cannot verify, and the account would look registered until the day the keys were
    /// needed.
    /// </para>
    /// <para>
    /// <b>Each column is stepped off <em>its own</em> bound, which is why <c>offset</c> is the parameter
    /// rather than a width.</b> The two columns are values of two different suites at two different
    /// widths, so one shared width argument could only be right about one of them — and stepping the
    /// encapsulated column off the AEAD width would feed it a 166-byte or 168-byte value, refused for a
    /// reason that says nothing about the bound this case is written for.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(-1)]
    [Arguments(1)]
    public async Task For_WithAPayloadOfTheWrongWidth_Throws(int offset)
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException onPrivateKey = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential,
            Guid.CreateVersion7(),
            PrivateKey(0xC0, WrappedPrivateKeyLength + offset),
            AccountKeys(0x1D),
            UtcNow));
        ValidationException onAccountKeys = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential,
            Guid.CreateVersion7(),
            PrivateKey(0xC0),
            AccountKeys(0x1D, EncapsulatedAccountKeysLength + offset),
            UtcNow));

        // Assert
        await Assert.That(
            onPrivateKey.Errors.ContainsKey(nameof(WrappedAccountKeys.WrappedPrivateKey))).IsTrue();
        await Assert.That(
            onAccountKeys.Errors.ContainsKey(nameof(WrappedAccountKeys.EncapsulatedAccountKeys))).IsTrue();
    }

    /// <summary>
    /// A payload of the <em>other</em> column's exact width and version is refused on both columns.
    /// </summary>
    /// <remarks>
    /// <b>The transposition, said at the entity.</b> Both values are well-formed by their own suite's
    /// rules and both lead with the same byte, so nothing in the bytes says which column they belong in
    /// — the parameter position is the only discriminator. Until the two widths diverged this case could
    /// not have been written at all: a swapped pair satisfied every check at every layer, and the
    /// discovery happened in a browser on the day somebody needed the keys. It is written against the
    /// other suite's constants rather than against 158 and 167, so that making the two widths equal
    /// again reddens here instead of quietly deleting the guarantee.
    /// </remarks>
    [Test]
    public async Task For_WithTheTwoPayloadsTransposed_Throws()
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException transposed = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), AccountKeys(0x1D), PrivateKey(0xC0), UtcNow));

        // Assert — both members named, because a transposition is wrong about both at once and an
        // entity that only judged one of them would pass a single-key assertion.
        await Assert.That(
            transposed.Errors.ContainsKey(nameof(WrappedAccountKeys.WrappedPrivateKey))).IsTrue();
        await Assert.That(
            transposed.Errors.ContainsKey(nameof(WrappedAccountKeys.EncapsulatedAccountKeys))).IsTrue();
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
    public async Task For_WithAnUnknownFramingVersion_Throws()
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        byte[] successorPrivateKey = PrivateKey(0xC0);
        successorPrivateKey[0] = WrappedPrivateKeyVersion + 1;
        byte[] successorAccountKeys = AccountKeys(0x1D);
        successorAccountKeys[0] = EncapsulatedAccountKeysVersion + 1;

        // Act — each column's successor is built from its OWN suite's version constant. The two hold
        // the same number today, so a cross-read renders identical bytes and would go on passing; the
        // separation is what the day either suite is bumped depends on.
        ValidationException onPrivateKey = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), successorPrivateKey, AccountKeys(0x1D), UtcNow));
        ValidationException onAccountKeys = ThrowsValidationException(() => WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), PrivateKey(0xC0), successorAccountKeys, UtcNow));

        // Assert
        await Assert.That(
            onPrivateKey.Errors.ContainsKey(nameof(WrappedAccountKeys.WrappedPrivateKey))).IsTrue();
        await Assert.That(
            onAccountKeys.Errors.ContainsKey(nameof(WrappedAccountKeys.EncapsulatedAccountKeys))).IsTrue();
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
        byte[] content = PrivateKey(0xC0);
        byte[] index = AccountKeys(0x1D);
        WrappedAccountKeys wrapped = WrappedAccountKeys.For(
            credential, Guid.CreateVersion7(), content, index, UtcNow);

        // Act
        content[^1] ^= 0xFF;
        index[^1] ^= 0xFF;

        // Assert — the expected values are cast, because TUnit's IsEqualTo(1) against a byte compiles
        // and then throws at run time on the comparison rather than failing the assertion.
        await Assert.That(wrapped.WrappedPrivateKey.Span[^1]).IsEqualTo((byte)0xC0);
        await Assert.That(wrapped.EncapsulatedAccountKeys.Span[^1]).IsEqualTo((byte)0x1D);
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
                null!, Guid.CreateVersion7(), PrivateKey(0xC0), AccountKeys(0x1D), UtcNow))
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
    private static byte[] PrivateKey(byte filler, int width = WrappedPrivateKeyLength) =>
        Payload(width, WrappedPrivateKeyVersion, filler);

    /// <inheritdoc cref="PrivateKey" />
    private static byte[] AccountKeys(byte filler, int width = EncapsulatedAccountKeysLength) =>
        Payload(width, EncapsulatedAccountKeysVersion, filler);

    /// <summary>
    /// The shared body of the two above. The width and the version are parameters rather than read
    /// inside, because the one mistake this helper could make is pairing one suite's width with the
    /// other's version — which renders bytes the entity refuses for the wrong reason, or accepts when it
    /// should not, with nothing in the build able to say which.
    /// </summary>
    private static byte[] Payload(int width, byte version, byte filler)
    {
        byte[] payload = new byte[width];
        Array.Fill(payload, filler);

        if (width > 0)
        {
            payload[0] = version;
        }

        return payload;
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
