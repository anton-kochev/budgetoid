using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class EmailTests
{
    [Test]
    public async Task Create_WithValidEmail_TrimsValue()
    {
        Email email = Email.Create(" person@example.com ");

        await Assert.That(email.Value).IsEqualTo("person@example.com");
    }

    [Test]
    public async Task Create_WithBlankEmail_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() => Email.Create("   "));

        await Assert.That(exception.Errors.ContainsKey("Email")).IsTrue();
    }

    [Test]
    public async Task Create_WithTheMaximumLength_IsAccepted()
    {
        // Arrange — RFC 5321's 254-character path limit, which the email column is sized to.
        string value = EmailOfLength(Email.MaxLength);

        // Act
        Email email = Email.Create(value);

        // Assert
        await Assert.That(email.Value.Length).IsEqualTo(Email.MaxLength);
    }

    [Test]
    public async Task Create_WithOneCharacterOverTheMaximum_ThrowsValidationException()
    {
        // Arrange
        string value = EmailOfLength(Email.MaxLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() => Email.Create(value));

        // Assert — the boundary is the domain's, and it has to match the column's: a value the
        // domain accepts and the column refuses is a 500 where a 400 belongs.
        await Assert.That(exception.Errors.ContainsKey("Email")).IsTrue();
    }

    [Test]
    public async Task Create_MeasuresLengthAfterTrimming()
    {
        // Arrange — surrounding whitespace must not tip a valid address over the edge, since the
        // value that reaches the column is the trimmed one.
        string value = $"   {EmailOfLength(Email.MaxLength)}   ";

        // Act
        Email email = Email.Create(value);

        // Assert
        await Assert.That(email.Value.Length).IsEqualTo(Email.MaxLength);
    }

    /// <summary>
    /// Builds a syntactically plausible address of exactly <paramref name="length"/> characters by
    /// padding the local part.
    /// </summary>
    private static string EmailOfLength(int length)
    {
        const string domain = "@example.com";
        return new string('a', length - domain.Length) + domain;
    }

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
