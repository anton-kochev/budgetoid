using System.Reflection;
using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class UserTests
{
    /// <summary>
    /// The account comes back under the identifier it was handed, with the address trimmed.
    /// </summary>
    /// <remarks>
    /// <b>The id is compared against the one passed in, not merely against
    /// <see cref="Guid.Empty" />.</b> That is what this test gained when the minting factory beside
    /// <see cref="User.CreateWithId" /> was deleted: while <c>User.Create</c> chose the id itself,
    /// "not empty" was the strongest thing a caller could say about it. It is now the whole point —
    /// the identifier is the WebAuthn user handle the authenticator was given when the ceremony
    /// opened, and a row written under any other value answers no assertion that device will ever
    /// produce, silently and permanently.
    /// </remarks>
    [Test]
    public async Task CreateWithId_WithValidInput_ReturnsInitializedUser()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();

        // Act
        User user = User.CreateWithId(id, " person@example.com ", createdAtUtc);

        // Assert
        await Assert.That(user.Id).IsEqualTo(id);
        await Assert.That(user.Email.Value).IsEqualTo("person@example.com");
        await Assert.That(user.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task CreateWithId_WithBlankEmail_ThrowsValidationException()
    {
        // Arrange, Act
        ValidationException exception = ThrowsValidationException(
            () => User.CreateWithId(Guid.CreateVersion7(), "   ", UtcNow()));

        // Assert
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

    [Test]
    public async Task ChangeEmail_WithSurroundingWhitespace_StoresTheTrimmedAddress()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UtcNow());

        // Act
        user.ChangeEmail("  moved@example.com\t");

        // Assert
        await Assert.That(user.Email.Value).IsEqualTo("moved@example.com");
    }

    [Test]
    public async Task ChangeEmail_Blank_Throws()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() => user.ChangeEmail("   "));

        // Assert — the key the creation path reports, and the address left as it was.
        await Assert.That(exception.Errors.ContainsKey("Email")).IsTrue();
        await Assert.That(user.Email.Value).IsEqualTo("person@example.com");
    }

    [Test]
    public async Task ChangeEmail_LongerThanTheLimit_Throws()
    {
        // Arrange — Email.MaxLength is 254, the RFC 5321 path limit less the angle brackets; one
        // character past it after trimming.
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UtcNow());
        string overLong = new string('a', Email.MaxLength + 1 - "@example.com".Length) + "@example.com";

        // Act
        ValidationException exception = ThrowsValidationException(() => user.ChangeEmail(overLong));

        // Assert
        await Assert.That(overLong.Length).IsEqualTo(255);
        await Assert.That(exception.Errors.ContainsKey("Email")).IsTrue();
        await Assert.That(user.Email.Value).IsEqualTo("person@example.com");
    }

    [Test]
    public async Task ChangeEmail_AtTheLimit_IsAccepted()
    {
        // Arrange — exactly Email.MaxLength (254) characters.
        User user = User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UtcNow());
        string atLimit = new string('a', Email.MaxLength - "@example.com".Length) + "@example.com";

        // Act
        user.ChangeEmail(atLimit);

        // Assert
        await Assert.That(atLimit.Length).IsEqualTo(254);
        await Assert.That(user.Email.Value).IsEqualTo(atLimit);
    }

    [Test]
    public async Task ChangeEmail_LeavesEveryOtherPropertyAsItWas()
    {
        // Arrange — users carries UPDATE (email) and nothing else, so nothing else may move.
        var id = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        User user = User.CreateWithId(id, "person@example.com", createdAtUtc);

        // Act
        user.ChangeEmail("moved@example.com");

        // Assert
        await Assert.That(user.Id).IsEqualTo(id);
        await Assert.That(user.CreatedAtUtc).IsEqualTo(createdAtUtc);
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
