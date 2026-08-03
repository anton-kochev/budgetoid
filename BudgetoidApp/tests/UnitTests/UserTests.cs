using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class UserTests
{
    [Test]
    public async Task Create_WithValidInput_ReturnsInitializedUser()
    {
        DateTime createdAtUtc = UtcNow();

        User user = User.Create(" person@example.com ", " Person ", createdAtUtc);

        await Assert.That(user.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(user.Email.Value).IsEqualTo("person@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("Person");
        await Assert.That(user.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithBlankEmail_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() => User.Create("   ", null, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Email")).IsTrue();
    }

    [Test]
    public async Task Create_WithTheMaximumLengthDisplayName_IsAccepted()
    {
        // Arrange
        string displayName = new('d', User.MaxDisplayNameLength);

        // Act
        User user = User.Create("person@example.com", displayName, UtcNow());

        // Assert
        await Assert.That(user.DisplayName?.Length).IsEqualTo(User.MaxDisplayNameLength);
    }

    [Test]
    public async Task Create_WithAnOverLongDisplayName_ThrowsValidationException()
    {
        // Arrange
        string displayName = new('d', User.MaxDisplayNameLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            User.Create("person@example.com", displayName, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("DisplayName")).IsTrue();
    }

    [Test]
    public async Task Create_MeasuresDisplayNameLengthAfterTrimming()
    {
        // Arrange
        string displayName = $"   {new string('d', User.MaxDisplayNameLength)}   ";

        // Act
        User user = User.Create("person@example.com", displayName, UtcNow());

        // Assert
        await Assert.That(user.DisplayName?.Length).IsEqualTo(User.MaxDisplayNameLength);
    }

    [Test]
    public async Task Create_WithAWhitespaceOnlyDisplayName_StoresNull()
    {
        // Arrange, Act — a missing display name is normal (Google need not supply one) and is not an
        // error; only an over-long one is.
        User user = User.Create("person@example.com", "   ", UtcNow());

        // Assert
        await Assert.That(user.DisplayName).IsNull();
    }

    [Test]
    public async Task Create_WithSeveralOverLongFields_ReportsThemAllAtOnce()
    {
        // Arrange — every field is validated before any is reported, so a caller fixing one problem
        // does not discover the next only on the following attempt. Both fields below are invalid,
        // and both must come back from a single call rather than the first one failing fast.
        string email = new string('a', Email.MaxLength + 1 - "@example.com".Length) + "@example.com";
        string displayName = new('d', User.MaxDisplayNameLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            User.Create(email, displayName, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.Keys.ToArray())
            .IsEquivalentTo(new[] { "Email", "DisplayName" });
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
