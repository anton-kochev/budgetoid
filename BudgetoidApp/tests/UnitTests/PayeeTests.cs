using Domain.Common;
using Domain.Payees;

namespace UnitTests;

public sealed class PayeeTests
{
    [Test]
    public async Task Create_WithValidInput_TrimsNameAndPreservesCase()
    {
        // Arrange
        var budgetId = Guid.CreateVersion7();
        DateTime createdAtUtc = new(2026, 6, 24, 13, 14, 15, DateTimeKind.Utc);

        // Act
        Payee payee = Payee.Create(budgetId, "  Starbucks  ", createdAtUtc);

        // Assert
        await Assert.That(payee.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(payee.BudgetId).IsEqualTo(budgetId);
        await Assert.That(payee.Name).IsEqualTo("Starbucks");
        await Assert.That(payee.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithEmptyBudgetId_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Payee.Create(Guid.Empty, "Starbucks", UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
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

    [Test]
    public async Task Rename_WithValidInput_ReplacesNameAndTrimsIt()
    {
        // Arrange
        Payee payee = Payee.Create(Guid.CreateVersion7(), "Starbuks", UtcNow());

        // Act
        payee.Rename("  Starbucks  ");

        // Assert
        await Assert.That(payee.Name).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task Rename_WithValidInput_LeavesIdBudgetIdAndCreatedAtUtcUnchanged()
    {
        // Arrange — BudgetId is the tenancy rule: a payee that changed budget would carry its
        // transaction history into someone else's ledger.
        var budgetId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        Payee payee = Payee.Create(budgetId, "Starbuks", createdAtUtc);
        Guid id = payee.Id;

        // Act
        payee.Rename("Starbucks");

        // Assert
        await Assert.That(payee.Id).IsEqualTo(id);
        await Assert.That(payee.BudgetId).IsEqualTo(budgetId);
        await Assert.That(payee.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task Rename_WithBlankName_ThrowsValidationException(string? name)
    {
        // Arrange
        Payee payee = Payee.Create(Guid.CreateVersion7(), "Starbucks", UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() => payee.Rename(name!));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(exception.Errors["Name"].Single()).IsEqualTo("Name is required.");
    }

    [Test]
    public async Task Rename_WithNameLongerThan200Characters_ThrowsValidationException()
    {
        // Arrange
        Payee payee = Payee.Create(Guid.CreateVersion7(), "Starbucks", UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            payee.Rename(new string('x', 201)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(exception.Errors["Name"].Single())
            .IsEqualTo("Name must be 200 characters or fewer.");
    }

    [Test]
    public async Task Rename_WithNameOfExactly200Characters_ReplacesName()
    {
        // Arrange
        Payee payee = Payee.Create(Guid.CreateVersion7(), "Starbucks", UtcNow());
        string name = new('x', 200);

        // Act
        payee.Rename(name);

        // Assert
        await Assert.That(payee.Name).IsEqualTo(name);
    }

    [Test]
    public async Task Rename_WithCaseOnlyChange_ReplacesName()
    {
        // Arrange — the payee row's own entry in the case-insensitive unique index is replaced in
        // the same update, so it cannot collide with itself. This looks like it should collide and
        // someone will try to "fix" it.
        Payee payee = Payee.Create(Guid.CreateVersion7(), "starbucks", UtcNow());

        // Act
        payee.Rename("Starbucks");

        // Assert
        await Assert.That(payee.Name).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task Rename_WithTheNameItAlreadyHas_LeavesNameUnchanged()
    {
        // Arrange
        Payee payee = Payee.Create(Guid.CreateVersion7(), "Starbucks", UtcNow());

        // Act
        payee.Rename("Starbucks");

        // Assert
        await Assert.That(payee.Name).IsEqualTo("Starbucks");
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
