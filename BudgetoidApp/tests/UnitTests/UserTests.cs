using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class UserTests
{
    [Test]
    public async Task Create_WithValidInput_ReturnsInitializedUser()
    {
        DateTime createdAtUtc = UtcNow();

        User user = User.Create(" google-subject ", " person@example.com ", " Person ", createdAtUtc);

        await Assert.That(user.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(user.GoogleSubject).IsEqualTo("google-subject");
        await Assert.That(user.Email.Value).IsEqualTo("person@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("Person");
        await Assert.That(user.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithBlankGoogleSubject_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() => User.Create("   ", "person@example.com", null, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("GoogleSubject")).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankEmail_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() => User.Create("google-subject", "   ", null, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Email")).IsTrue();
    }

    [Test]
    public async Task UpdateProfile_RefreshesEmailAndDisplayName()
    {
        User user = User.Create("google-subject", "old@example.com", "Old", UtcNow());

        user.UpdateProfile(" new@example.com ", " New ");

        await Assert.That(user.Email.Value).IsEqualTo("new@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("New");
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
