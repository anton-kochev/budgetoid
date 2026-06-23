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
