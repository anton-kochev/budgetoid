using Domain.Common;
using Domain.Payees;

namespace UnitTests;

public sealed class PayeeTests
{
    [Test]
    public async Task Create_WithValidInput_TrimsNameAndPreservesCase()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        DateTime createdAtUtc = new(2026, 6, 24, 13, 14, 15, DateTimeKind.Utc);

        // Act
        Payee payee = Payee.Create(userId, "  Starbucks  ", createdAtUtc);

        // Assert
        await Assert.That(payee.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(payee.UserId).IsEqualTo(userId);
        await Assert.That(payee.Name).IsEqualTo("Starbucks");
        await Assert.That(payee.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithEmptyUserId_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Payee.Create(Guid.Empty, "Starbucks", UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankName_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Payee.Create(Guid.CreateVersion7(), "   ", UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithNameLongerThan200Characters_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Payee.Create(Guid.CreateVersion7(), new string('x', 201), UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    private static DateTime UtcNow() => new(2026, 6, 24, 13, 14, 15, DateTimeKind.Utc);

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
