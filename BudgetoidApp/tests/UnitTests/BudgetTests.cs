using Domain.Budgets;
using Domain.Common;

namespace UnitTests;

public sealed class BudgetTests
{
    [Test]
    public async Task Create_WithValidInput_ReturnsBudgetWithNewIdAndNoBaseCurrency()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        DateTime createdAtUtc = new(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc);

        // Act
        Budget budget = Budget.Create(userId, "Household", createdAtUtc);

        // Assert
        await Assert.That(budget.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(budget.UserId).IsEqualTo(userId);
        await Assert.That(budget.Name).IsEqualTo("Household");
        await Assert.That(budget.BaseCurrencyCode).IsNull();
        await Assert.That(budget.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithEmptyUserId_ThrowsValidationExceptionForUserId()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Budget.Create(Guid.Empty, "Household", UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankName_ThrowsValidationExceptionForName()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Budget.Create(Guid.CreateVersion7(), "   ", UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithNameLongerThan200Characters_ThrowsValidationExceptionForName()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Budget.Create(Guid.CreateVersion7(), new string('x', 201), UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_TrimsName()
    {
        // Act
        Budget budget = Budget.Create(Guid.CreateVersion7(), "  Household  ", UtcNow());

        // Assert
        await Assert.That(budget.Name).IsEqualTo("Household");
    }

    [Test]
    public async Task CreateDefault_UsesTheDefaultBudgetName()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();

        // Act
        Budget budget = Budget.CreateDefault(userId, createdAtUtc);

        // Assert
        await Assert.That(budget.Name).IsEqualTo(Budget.DefaultName);
        await Assert.That(budget.UserId).IsEqualTo(userId);
        await Assert.That(budget.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    private static DateTime UtcNow() =>
        new(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc);

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
