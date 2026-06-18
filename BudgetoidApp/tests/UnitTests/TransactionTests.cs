using System.Reflection;
using Domain.Common;
using Domain.Transactions;

namespace UnitTests;

public sealed class TransactionTests
{
    [Test]
    public async Task UserId_IsImmutableAfterCreation()
    {
        // Write-side half of the data-isolation invariant: the read-side query filter cannot
        // stop SaveChanges from moving a row between users, so UserId must never be reassignable.
        PropertyInfo userId = typeof(Transaction).GetProperty(nameof(Transaction.UserId))!;

        bool hasPublicSetter = userId.SetMethod is { IsPublic: true };

        await Assert.That(hasPublicSetter).IsFalse();
    }

    [Test]
    public async Task Create_WithValidInput_ReturnsInitializedTransaction()
    {
        var userId = Guid.CreateVersion7();
        var date = new DateOnly(2026, 6, 12);

        var transaction = Transaction.Create(userId, -42.50m, date, " Groceries ");

        await Assert.That(transaction.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(transaction.UserId).IsEqualTo(userId);
        await Assert.That(transaction.Amount).IsEqualTo(-42.50m);
        await Assert.That(transaction.Date).IsEqualTo(date);
        await Assert.That(transaction.Description).IsEqualTo("Groceries");
        await Assert.That(transaction.CreatedAtUtc.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    public async Task Create_WithEmptyUserId_ThrowsValidationException()
    {
        var exception = ThrowsValidationException(() => Transaction.Create(Guid.Empty, 1m, DateOnly.FromDateTime(DateTime.UtcNow), "Test"));
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    [Test]
    public async Task Create_WithZeroAmount_ThrowsValidationException()
    {
        var exception = ThrowsValidationException(() => Transaction.Create(Guid.CreateVersion7(), 0m, DateOnly.FromDateTime(DateTime.UtcNow), "Test"));
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
    }

    [Test]
    public async Task Create_WithMoreThanTwoDecimalPlaces_ThrowsValidationException()
    {
        var exception = ThrowsValidationException(() => Transaction.Create(Guid.CreateVersion7(), 1.234m, DateOnly.FromDateTime(DateTime.UtcNow), "Test"));
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
    }

    [Test]
    public async Task Create_WithAmountAbsoluteValueOverLimit_ThrowsValidationException()
    {
        var exception = ThrowsValidationException(() => Transaction.Create(Guid.CreateVersion7(), 1_000_000_000.01m, DateOnly.FromDateTime(DateTime.UtcNow), "Test"));
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankDescription_ThrowsValidationException()
    {
        var exception = ThrowsValidationException(() => Transaction.Create(Guid.CreateVersion7(), 1m, DateOnly.FromDateTime(DateTime.UtcNow), "   "));
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
    }

    [Test]
    public async Task Create_WithDescriptionLongerThan500Characters_ThrowsValidationException()
    {
        var exception = ThrowsValidationException(() => Transaction.Create(Guid.CreateVersion7(), 1m, DateOnly.FromDateTime(DateTime.UtcNow), new string('x', 501)));
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
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
