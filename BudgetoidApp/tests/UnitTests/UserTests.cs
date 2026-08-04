using System.Reflection;
using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class UserTests
{
    [Test]
    public async Task Create_WithValidInput_ReturnsInitializedUser()
    {
        DateTime createdAtUtc = UtcNow();

        User user = User.Create(" person@example.com ", createdAtUtc);

        await Assert.That(user.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(user.Email.Value).IsEqualTo("person@example.com");
        await Assert.That(user.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithBlankEmail_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() => User.Create("   ", UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Email")).IsTrue();
    }

    [Test]
    public async Task User_PinsEveryPublicInstanceProperty()
    {
        // Arrange
        PropertyInfo[] properties = typeof(User).GetProperties(
            BindingFlags.Public | BindingFlags.Instance);

        // Act
        string[] names = properties
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Assert — an account row is an internal identifier, an email address and a creation
        // timestamp, and nothing else. Everything an identity provider says about a person is read
        // to answer who is asking and then dropped, so a DisplayName, a picture URL or a locale
        // reappearing here is the rule breaking rather than a field being added. Joined into one
        // string so a failure names the offender instead of reporting a count.
        string[] expected = ["CreatedAtUtc", "Email", "Id"];
        await Assert.That(string.Join(", ", names)).IsEqualTo(string.Join(", ", expected));

        // Without this, a reflection query that silently returned nothing would pass the assertion
        // above while proving nothing at all.
        await Assert.That(properties.Length).IsGreaterThan(0);
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
