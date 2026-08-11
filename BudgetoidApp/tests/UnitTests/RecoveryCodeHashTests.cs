using System.Security.Cryptography;
using Domain.Common;
using Domain.Users;

namespace UnitTests;

/// <summary>
/// What a stored recovery code is made of: the SHA-256 of a verifier, computed by the entity itself,
/// against a credential that stands for a set of recovery codes and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server never sees a code, and that is the fact every assertion here is arranged around.</b>
/// The browser mints a code with at least 128 bits of entropy, derives a verifier
/// <c>V = HKDF(code, …)</c> from it, and sends only <c>V</c>; this type stores <c>SHA-256(V)</c>. The
/// consequence for these tests is that <b>nothing below measures entropy</b> — a fixed-length opaque
/// value is all this layer ever receives, so entropy is verified by a client-side test and by nothing
/// in this codebase. What is checkable here is the verifier's exact width and what the entity does
/// with it, and that is what is checked.
/// </para>
/// <para>
/// The reason the split is worth the awkwardness is not FR-063's wording. Story 12.1 derives the
/// account's key-encryption key as <c>HKDF(code, …)</c> on a branch independent of the verifier, so a
/// code arriving at the server would hand the operator the KEK and make NFR-015 — "no party but a
/// holder of one of the account's own recovery factors obtains the keys" — false. A database reader
/// holding <c>SHA-256(V)</c> can neither redeem (no preimage) nor derive the KEK (wrong branch).
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a pin nothing can redden is decoration:
/// </para>
/// <list type="bullet">
/// <item>
/// "the stored value is the hash" — <see cref="From_StoresTheHashOfTheVerifierAndNeverTheVerifier" />
/// computes SHA-256 in the test rather than comparing against
/// <see cref="RecoveryCodeHash.HashOf" />, so a <c>From</c> that stored the verifier verbatim, or
/// salted the hash, is red. Comparing the two production members against each other would pass for an
/// entity that stored the raw bytes through both.
/// </item>
/// <item>
/// "the hash is unsalted, so the redemption lookup can find it" —
/// <see cref="HashOf_AgreesWithTheValueFromStores" /> pins the two spellings together, and
/// <see cref="HashOf_ForTwoVerifiers_ProducesTwoDifferentValues" /> is its control: without it a
/// <c>HashOf</c> returning a constant satisfies the agreement test perfectly and every code in the
/// system hashes to one row.
/// </item>
/// <item>
/// "exactly 32 bytes" — <see cref="From_WithAShortVerifier_Throws" /> and
/// <see cref="From_WithALongVerifier_Throws" /> take the bound from both sides, and the accepting
/// tests above are the control at the boundary itself: a length check written as <c>&gt;= 32</c> or
/// <c>&lt;= 32</c> fails exactly one of the pair, and one written as "any length" fails both while the
/// happy path stays green.
/// </item>
/// <item>
/// "a set's credential, not a passkey's" — <see cref="From_AgainstAPasskeyCredential_Throws" /> and
/// <see cref="From_AgainstAFederatedCredential_Throws" />, controlled by
/// <see cref="From_CopiesTheCredentialsIdentityOntoTheRow" />, which proves the refusal is about the
/// type rather than a factory that refuses everything.
/// </item>
/// </list>
/// </remarks>
public sealed class RecoveryCodeHashTests
{
    /// <summary>
    /// The exact width of the verifier the client derives, and the only thing about it this layer can
    /// judge.
    /// </summary>
    /// <remarks>
    /// Restated here rather than read off the entity on purpose. The number is the pin: a test that
    /// took its bound from the type under test would agree with any bound that type later chose, which
    /// is precisely the drift the <c>CK_recovery_code_hashes_verifier_hash_length</c> constraint and
    /// this pair of length tests exist to catch. Thirty-two is the HKDF output width the client is
    /// specified to produce, and — separately, by arithmetic rather than by coincidence — the width of
    /// the SHA-256 the column stores.
    /// </remarks>
    private const int VerifierLength = 32;

    /// <summary>Fixed instant, so nothing here depends on the wall clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 11, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The entity hashes the verifier itself, and the raw verifier reaches no property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hashing inside the factory is the design, not an implementation detail this test happens to
    /// pin.</b> A <c>From</c> taking a hash the caller computed would mean a raw verifier could be
    /// assigned to an object something can persist — and every one of the call sites that would have
    /// to be reviewed for it is a call site that could get it wrong once. Here there is no such call
    /// site, because there is no shape of the call that stores an unhashed value.
    /// </para>
    /// <para>
    /// The expected value is computed in the test with <see cref="SHA256" /> rather than read back from
    /// <see cref="RecoveryCodeHash.HashOf" />, which is what makes this an independent statement about
    /// the algorithm: unsalted SHA-256 and nothing else. A salt would be indistinguishable from this
    /// hash to a database reader and fatal to the product — the redemption request arrives carrying a
    /// verifier and no identity, so a per-row salt is a value the lookup cannot know before it has
    /// found the row it needs the salt to find.
    /// </para>
    /// </remarks>
    [Test]
    public async Task From_StoresTheHashOfTheVerifierAndNeverTheVerifier()
    {
        // Arrange
        Credential credential = RecoveryCodesCredential();
        ReadOnlyMemory<byte> verifier = Verifier();
        byte[] expected = SHA256.HashData(verifier.Span);

        // Act
        RecoveryCodeHash hash = RecoveryCodeHash.From(credential, verifier, UtcNow);

        // Assert — the hash, and then, separately, not the verifier. The second assertion is not the
        // first one restated: an entity that stored the verifier verbatim would fail the first, but so
        // would one that hashed twice or truncated, and only the second says which mistake would be
        // the dangerous one.
        await Assert.That(hash.VerifierHash.ToArray()).IsEquivalentTo(expected);
        await Assert.That(hash.VerifierHash.ToArray().SequenceEqual(verifier.ToArray())).IsFalse();
    }

    /// <summary>
    /// The credential's id, owner and type are copied off the credential rather than passed beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The argument <c>Session.Establish</c> and <c>PasskeyPublicKey.Register</c> both make, and it is
    /// sharper here than on either of them. All three columns are compared against
    /// <c>credentials(id, user_id, type)</c> by a composite foreign key, so a row whose owner disagreed
    /// with its credential's is unstorable — but the reason that matters is what happens when it is
    /// storable: a redemption arrives <b>anonymous</b> and adopts the <c>user_id</c> it finds on this
    /// row, and <c>recovery_code_hashes</c> is exempt from row-level security, so nothing beneath the
    /// application is watching. A <c>From</c> taking three loose ids is one transposed argument away
    /// from handing a redeemer somebody else's account.
    /// </para>
    /// <para>
    /// This is also the provable-fail control for the two type refusals below: without it, a factory
    /// that threw at every call would satisfy both.
    /// </para>
    /// </remarks>
    [Test]
    public async Task From_CopiesTheCredentialsIdentityOntoTheRow()
    {
        // Arrange
        Credential credential = RecoveryCodesCredential();

        // Act
        RecoveryCodeHash hash = RecoveryCodeHash.From(credential, Verifier(), UtcNow);

        // Assert
        await Assert.That(hash.CredentialId).IsEqualTo(credential.Id);
        await Assert.That(hash.UserId).IsEqualTo(credential.UserId);
        await Assert.That(hash.CredentialType).IsEqualTo(CredentialType.RecoveryCodes);
        await Assert.That(hash.CreatedAtUtc).IsEqualTo(UtcNow);
    }

    /// <summary>
    /// A code filed against a passkey credential is refused.
    /// </summary>
    /// <remarks>
    /// <c>CK_credentials_type_shape</c> cannot tell the two apart — the <c>recovery_codes</c> and
    /// <c>passkey</c> arms read the same predicate — so the type spelling is the only thing separating
    /// a set of codes from an authenticator for everything downstream. The database refuses this row
    /// too, through <c>CK_recovery_code_hashes_credential_type</c> and the composite foreign key; the
    /// refusal is stated here as well because a row the database turns down is a request that has
    /// already reached it, and this factory is where the mistake is cheap.
    /// </remarks>
    [Test]
    public async Task From_AgainstAPasskeyCredential_Throws()
    {
        // Arrange
        Credential credential = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            RecoveryCodeHash.From(credential, Verifier(), UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(RecoveryCodeHash.CredentialType))).IsTrue();
    }

    /// <summary>
    /// A code filed against the account's federated Google credential is refused.
    /// </summary>
    /// <remarks>
    /// The second arm rather than a duplicate of the first. A refusal written as
    /// <c>type == Passkey</c> — the shape a reader copying <c>PasskeyPublicKey.Register</c> backwards
    /// would produce — passes the test above and lets a recovery code hang off the credential the
    /// identity provider owns, which is the one credential type whose session may not reach budget
    /// content at all.
    /// </remarks>
    [Test]
    public async Task From_AgainstAFederatedCredential_Throws()
    {
        // Arrange
        Credential credential = Credential.CreateFederated(
            Guid.CreateVersion7(), Credential.GoogleProvider, "google-subject", UtcNow);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            RecoveryCodeHash.From(credential, Verifier(), UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(RecoveryCodeHash.CredentialType))).IsTrue();
    }

    /// <summary>
    /// No credential, nothing to file — and the answer is an argument exception, not a validation one.
    /// </summary>
    /// <remarks>
    /// The distinction <see cref="PasskeyPublicKey.Register" /> already draws: the owner and the type
    /// are read off the credential, so there is nothing to validate without one. No user typed this; a
    /// caller handed over nothing.
    /// </remarks>
    [Test]
    public async Task From_WithNoCredential_Throws()
    {
        // Arrange, Act
        ReadOnlyMemory<byte> verifier = Verifier();

        // Assert
        await Assert.That(() => RecoveryCodeHash.From(null!, verifier, UtcNow))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// A verifier one byte short of the width is refused.
    /// </summary>
    /// <remarks>
    /// One byte rather than an obviously wrong length, because the bound is the assertion. A short
    /// verifier is a shorter secret than the design claims, and it would hash to a perfectly
    /// well-formed 32-byte row that nothing downstream could tell from a real one — the column's own
    /// length check watches the <em>hash</em>, which is 32 bytes whatever went into it, so the database
    /// cannot catch this one and this factory is the only place it stops.
    /// </remarks>
    [Test]
    public async Task From_WithAShortVerifier_Throws()
    {
        // Arrange
        Credential credential = RecoveryCodesCredential();

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            RecoveryCodeHash.From(credential, Verifier(VerifierLength - 1), UtcNow));

        // Assert — keyed on the property the value ends up in, as PasskeyPublicKey.Register keys its
        // own length refusals.
        await Assert.That(exception.Errors.ContainsKey(nameof(RecoveryCodeHash.VerifierHash))).IsTrue();
    }

    /// <summary>
    /// A verifier one byte over the width is refused, and this is the half a lower bound alone misses.
    /// </summary>
    /// <remarks>
    /// The direction that looks harmless and is not. An over-long verifier means the client and the
    /// server disagree about what a verifier is, and since the client also derives the account's
    /// key-encryption key from the same code, a width the server quietly accepts is a width nobody
    /// notices is wrong until a key fails to unwrap. Refused, not truncated: truncating would store a
    /// hash of a prefix, and no code would ever redeem.
    /// </remarks>
    [Test]
    public async Task From_WithALongVerifier_Throws()
    {
        // Arrange
        Credential credential = RecoveryCodesCredential();

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            RecoveryCodeHash.From(credential, Verifier(VerifierLength + 1), UtcNow));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(RecoveryCodeHash.VerifierHash))).IsTrue();
    }

    /// <summary>
    /// The hash the redemption lookup computes is the hash the row was stored under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two spellings of one value, and if they ever disagree no recovery code in the system
    /// redeems.</b> A redemption arrives carrying a verifier and nothing else — no credential, no
    /// identity — so it cannot go through <see cref="RecoveryCodeHash.From" /> to work out what row to
    /// look for; it needs the hash without an entity, which is what
    /// <see cref="RecoveryCodeHash.HashOf" /> is. Two independent implementations of "the same hash" is
    /// exactly the arrangement where one of them later grows a salt, a prefix or a second round, and
    /// the symptom is silent: every code the account was ever issued simply stops matching.
    /// </para>
    /// <para>
    /// Stated as an equality between the two rather than against a computed SHA-256, deliberately —
    /// that job belongs to <see cref="From_StoresTheHashOfTheVerifierAndNeverTheVerifier" />, and this
    /// one is about agreement. Its own control is the test below.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HashOf_AgreesWithTheValueFromStores()
    {
        // Arrange
        ReadOnlyMemory<byte> verifier = Verifier();
        RecoveryCodeHash stored = RecoveryCodeHash.From(RecoveryCodesCredential(), verifier, UtcNow);

        // Act
        ReadOnlyMemory<byte> looked = RecoveryCodeHash.HashOf(verifier);

        // Assert
        await Assert.That(looked.ToArray()).IsEquivalentTo(stored.VerifierHash.ToArray());
    }

    /// <summary>
    /// The provable-fail control for the agreement above: two verifiers, two different values.
    /// </summary>
    /// <remarks>
    /// Without it, a <see cref="RecoveryCodeHash.HashOf" /> returning a constant — or the verifier
    /// itself, if <see cref="RecoveryCodeHash.From" /> also stored it raw — satisfies the agreement
    /// test perfectly. The failure it prevents is not subtle: one value for every code means one row
    /// for every code, and the primary key on <c>verifier_hash</c> would make a ten-code set a
    /// one-code set at the first insert.
    /// </remarks>
    [Test]
    public async Task HashOf_ForTwoVerifiers_ProducesTwoDifferentValues()
    {
        // Arrange — two verifiers a client could plausibly have derived from two different codes.
        ReadOnlyMemory<byte> first = Verifier();
        ReadOnlyMemory<byte> second = Verifier();

        // Act
        byte[] firstHash = RecoveryCodeHash.HashOf(first).ToArray();
        byte[] secondHash = RecoveryCodeHash.HashOf(second).ToArray();

        // Assert
        await Assert.That(firstHash.SequenceEqual(secondHash)).IsFalse();
    }

    /// <summary>
    /// The same verifier hashes to the same bytes every time it is presented.
    /// </summary>
    /// <remarks>
    /// The unsalted property stated where it is about to be relied on. A person redeems a code weeks
    /// after it was issued, on another device, in another process; anything per-call in the hash — a
    /// salt, a nonce, a keyed MAC over a secret this deployment holds — turns "look the row up by its
    /// hash" into a query with no argument to give it. It is a separate test from the agreement above
    /// because the two fail for different reasons and a reader deleting one should not believe they
    /// still have the other.
    /// </remarks>
    [Test]
    public async Task HashOf_IsUnsaltedSoTheSameVerifierAlwaysProducesTheSameValue()
    {
        // Arrange
        ReadOnlyMemory<byte> verifier = Verifier();

        // Act
        byte[] first = RecoveryCodeHash.HashOf(verifier).ToArray();
        byte[] second = RecoveryCodeHash.HashOf(verifier).ToArray();

        // Assert
        await Assert.That(first).IsEquivalentTo(second);
    }

    /// <summary>The credential a set of codes hangs off.</summary>
    private static Credential RecoveryCodesCredential() =>
        Credential.CreateRecoveryCodes(Guid.CreateVersion7(), UtcNow);

    /// <summary>
    /// A verifier of the given width, random so that no two calls in one test collide.
    /// </summary>
    /// <remarks>
    /// Random rather than a fixed vector, and it costs nothing here: no assertion in this file depends
    /// on the <em>value</em> of a verifier — every expected hash is computed from the bytes the test
    /// itself just produced — so repeatability is not at stake. What randomness buys is that
    /// <see cref="HashOf_ForTwoVerifiers_ProducesTwoDifferentValues" /> compares two genuinely
    /// different inputs without either being spelled out as a magic constant a reader would have to
    /// check for collision by eye.
    /// </remarks>
    private static ReadOnlyMemory<byte> Verifier(int length = VerifierLength) =>
        RandomNumberGenerator.GetBytes(length);

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
