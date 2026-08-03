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
    public async Task CreateFederated_WithAnOverLongProvider_ThrowsValidationException()
    {
        // Arrange
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
