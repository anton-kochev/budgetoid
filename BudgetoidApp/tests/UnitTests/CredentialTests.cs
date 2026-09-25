using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class CredentialTests
{
    [Test]
    public async Task CreateFederated_WithValidInput_ReturnsInitializedCredential()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();

        // Act
        Credential credential = Credential.CreateFederated(userId, " google ", " google-subject ", createdAtUtc);

        // Assert
        await Assert.That(credential.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(credential.UserId).IsEqualTo(userId);
        await Assert.That(credential.Type).IsEqualTo(CredentialType.Federated);
        await Assert.That(credential.Provider).IsEqualTo("google");
        await Assert.That(credential.Subject).IsEqualTo("google-subject");
        await Assert.That(credential.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task CreateFederated_WithBlankProvider_ThrowsValidationException()
    {
        // Arrange, Act
        ValidationException exception = ThrowsValidationException(() =>
            Credential.CreateFederated(Guid.CreateVersion7(), "   ", "google-subject", UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Provider")).IsTrue();
    }

    [Test]
    public async Task CreateFederated_WithBlankSubject_ThrowsValidationException()
    {
        // Arrange, Act
        ValidationException exception = ThrowsValidationException(() =>
            Credential.CreateFederated(Guid.CreateVersion7(), Credential.GoogleProvider, "   ", UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Subject")).IsTrue();
    }

    [Test]
    public async Task CreateFederated_WithTheMaximumLengthSubject_IsAccepted()
    {
        // Arrange — Google's documented maximum for the sub claim, which the column is sized to.
        string subject = new('s', Credential.MaxSubjectLength);

        // Act
        Credential credential = Credential.CreateFederated(
            Guid.CreateVersion7(), Credential.GoogleProvider, subject, UtcNow());

        // Assert
        await Assert.That(credential.Subject?.Length).IsEqualTo(Credential.MaxSubjectLength);
    }

    [Test]
    public async Task CreateFederated_WithAnOverLongSubject_ThrowsValidationException()
    {
        // Arrange
        string subject = new('s', Credential.MaxSubjectLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Credential.CreateFederated(Guid.CreateVersion7(), Credential.GoogleProvider, subject, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Subject")).IsTrue();
    }

    [Test]
    public async Task CreateFederated_MeasuresSubjectLengthAfterTrimming()
    {
        // Arrange — the trimmed value is what reaches the column, so it is what the bound applies to.
        string subject = $"   {new string('s', Credential.MaxSubjectLength)}   ";

        // Act
        Credential credential = Credential.CreateFederated(
            Guid.CreateVersion7(), Credential.GoogleProvider, subject, UtcNow());

        // Assert
        await Assert.That(credential.Subject?.Length).IsEqualTo(Credential.MaxSubjectLength);
    }

    [Test]
    public async Task CreateFederated_WithANonCanonicalProviderSpelling_ThrowsValidationException()
    {
        // Arrange — 'Google' is the spelling an OIDC configuration or a hand-written call is most
        // likely to arrive with, and it is the one a lowercasing "fix" would make silently legal.
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Credential.CreateFederated(Guid.CreateVersion7(), "Google", "google-subject", UtcNow()));

        // Assert — refused, not folded. CK_credentials_provider will not accept 'Google' either, so
        // coercing it here would only move the failure to the insert; and UserRepository matches the
        // provider column case-sensitively, so a folded write would store a row its own lookup could
        // never find. ADR 0002: enforcement rejects, it does not coerce.
        await Assert.That(exception.Errors.ContainsKey("Provider")).IsTrue();
    }

    [Test]
    public async Task CreateFederated_WithAnUnsupportedProvider_ThrowsValidationException()
    {
        // Arrange — deliberately longer than the column as well as absent from the vocabulary. There
        // is no length branch to hit any more: nothing but 'google' gets that far, so the length
        // bound is unreachable from here and is owned at the column instead, by
        // UserRepositoryTests' Database_RejectsACredentialValueLongerThanItsColumn.
        string provider = new('p', Credential.MaxProviderLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Credential.CreateFederated(Guid.CreateVersion7(), provider, "google-subject", UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Provider")).IsTrue();
    }

    [Test]
    public async Task CreateFederated_WithSeveralInvalidFields_ReportsThemAllAtOnce()
    {
        // Arrange — every problem with the input is reported in one exception rather than the first
        // one found, so a caller sees the whole picture in a single round trip.
        string provider = new('p', Credential.MaxProviderLength + 1);
        string subject = new('s', Credential.MaxSubjectLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Credential.CreateFederated(Guid.CreateVersion7(), provider, subject, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.Keys.ToArray())
            .IsEquivalentTo(new[] { "Provider", "Subject" });
    }

    [Test]
    public async Task CreateFederated_WithAnEmptyUserId_ThrowsValidationException()
    {
        // Arrange, Act — a credential is only a way into an account; without one it is orphaned the
        // moment it is written.
        ValidationException exception = ThrowsValidationException(() =>
            Credential.CreateFederated(Guid.Empty, Credential.GoogleProvider, "google-subject", UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    [Test]
    public async Task CreatePasskey_ProducesACredentialWithNoProviderAndNoSubject()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();

        // Act
        Credential credential = Credential.CreatePasskey(userId, createdAtUtc);

        // Assert — a passkey is held by the authenticator, so there is no issuer to name and no
        // issuer-assigned identifier to record. Provider and Subject are nullable for exactly this
        // case, and leaving them null is what keeps the discovery lookup on the federated pair from
        // ever matching a passkey row.
        await Assert.That(credential.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(credential.UserId).IsEqualTo(userId);
        await Assert.That(credential.Type).IsEqualTo(CredentialType.Passkey);
        await Assert.That(credential.Provider).IsNull();
        await Assert.That(credential.Subject).IsNull();
        await Assert.That(credential.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task CreatePasskey_WithAnEmptyUserId_Throws()
    {
        // Arrange, Act — the same rule the federated factory applies, and it is stated again here
        // rather than assumed: a credential is only a way into an account, and one minted without an
        // owner is orphaned the moment it is written.
        ValidationException exception = ThrowsValidationException(() =>
            Credential.CreatePasskey(Guid.Empty, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    [Test]
    public async Task CreateRecoveryCodes_ProducesOneCredentialForTheWholeSet()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();

        // Act — one call per issued set, not per code. The codes themselves are rows on
        // recovery_code_hashes hanging off this credential, which is what lets redeeming one delete a
        // row while the set — and the account's ability to redeem the rest — survives, and lets
        // revoking the set be a single delete that the cascade carries the codes away with.
        Credential credential = Credential.CreateRecoveryCodes(userId, createdAtUtc);

        // Assert — nobody issued this credential either, so Provider and Subject stay null exactly as
        // they do for a passkey: a set of codes is generated by the product for the holder, and there
        // is no issuer to name and no issuer-assigned identifier to record. Leaving them null is also
        // what keeps the federated discovery lookup on the (provider, subject) pair from ever matching
        // one of these rows.
        //
        // Type is the assertion the pair of factories turns on. CK_credentials_type_shape cannot tell
        // recovery_codes from passkey — both arms read "provider is null and subject is null" — so the
        // spelling on this column is the only thing separating a set of codes from an authenticator
        // for everything downstream that cares, starting with which kind of session it opens. The
        // passkey test above is the standing control: a factory returning the wrong member here is
        // caught by this line, and the mirrored mistake next door is caught by that one.
        await Assert.That(credential.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(credential.UserId).IsEqualTo(userId);
        await Assert.That(credential.Type).IsEqualTo(CredentialType.RecoveryCodes);
        await Assert.That(credential.Provider).IsNull();
        await Assert.That(credential.Subject).IsNull();
        await Assert.That(credential.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task CreateRecoveryCodes_WithAnEmptyUserId_Throws()
    {
        // Arrange, Act — the rule both factories above already apply, stated a third time rather than
        // assumed: a credential is only a way into an account, and one minted without an owner is
        // orphaned the moment it is written. It matters more here than anywhere else on the schema —
        // a redemption arrives anonymous and adopts the user_id it finds, and recovery_code_hashes is
        // exempt from row-level security, so nothing beneath the application would notice.
        ValidationException exception = ThrowsValidationException(() =>
            Credential.CreateRecoveryCodes(Guid.Empty, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

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
