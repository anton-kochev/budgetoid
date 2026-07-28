using System.Globalization;
using System.Reflection;
using Domain.Common;
using Domain.Transactions;

namespace UnitTests;

public sealed class TransactionTests
{
    /// <summary>
    /// Minor unit of a two-decimal currency such as USD. Named rather than inlined so a call site
    /// that does not care about precision does not read as if <c>2</c> were a magic rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task BudgetId_IsImmutableAfterCreation()
    {
        // Write-side half of the data-isolation invariant: the read-side query filter cannot
        // stop SaveChanges from moving a row between budgets, so BudgetId must never be reassignable.
        PropertyInfo budgetId = typeof(Transaction).GetProperty(nameof(Transaction.BudgetId))!;

        bool hasPublicSetter = budgetId.SetMethod is { IsPublic: true };

        await Assert.That(hasPublicSetter).IsFalse();
    }

    [Test]
    public async Task Create_WithValidInput_ReturnsInitializedTransaction()
    {
        var budgetId = Guid.CreateVersion7();
        var date = new DateOnly(2026, 6, 12);

        DateTime createdAtUtc = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

        var accountId = Guid.CreateVersion7();

        var transaction = Transaction.Create(budgetId, accountId, -42.50m, UsdMinorUnit, date, " Groceries ", createdAtUtc);

        await Assert.That(transaction.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(transaction.BudgetId).IsEqualTo(budgetId);
        await Assert.That(transaction.AccountId).IsEqualTo(accountId);
        await Assert.That(transaction.Amount).IsEqualTo(-42.50m);
        await Assert.That(transaction.Date).IsEqualTo(date);
        await Assert.That(transaction.Description).IsEqualTo("Groceries");
        await Assert.That(transaction.CreatedAtUtc).IsEqualTo(createdAtUtc);
        await Assert.That(transaction.PayeeId).IsNull();
    }

    [Test]
    public async Task Create_WithZeroAmount_ReturnsTransactionCarryingZero()
    {
        // Arrange — a zero-net event (a fully discounted purchase, a refund that cancels out, a
        // zero-value invoice) is a real ledger entry whose value is the record, not the number.
        var budgetId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();

        // Act
        var transaction = Transaction.Create(
            budgetId,
            accountId,
            0m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Fully discounted",
            UtcNow());

        // Assert
        await Assert.That(transaction.Amount).IsEqualTo(0m);
        await Assert.That(transaction.BudgetId).IsEqualTo(budgetId);
        await Assert.That(transaction.AccountId).IsEqualTo(accountId);
    }

    [Test]
    public async Task AssignPayee_SetsPayeeId()
    {
        // Arrange
        var transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Groceries",
            UtcNow());
        var payeeId = Guid.CreateVersion7();

        // Act
        transaction.AssignPayee(payeeId);

        // Assert
        await Assert.That(transaction.PayeeId).IsEqualTo(payeeId);
    }

    [Test]
    public async Task AssignPayee_WithEmptyPayeeId_ThrowsArgumentException()
    {
        // Arrange
        var transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Groceries",
            UtcNow());

        // Act
        ArgumentException? caught = null;
        try
        {
            transaction.AssignPayee(Guid.Empty);
        }
        catch (ArgumentException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.ParamName).IsEqualTo("payeeId");
    }

    [Test]
    public async Task AssignCategory_SetsCategoryId()
    {
        // Arrange
        var transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Groceries",
            UtcNow());
        var categoryId = Guid.CreateVersion7();

        // Act
        transaction.AssignCategory(categoryId);

        // Assert
        await Assert.That(transaction.CategoryId).IsEqualTo(categoryId);
    }

    [Test]
    public async Task AssignCategory_WithEmptyCategoryId_ThrowsArgumentException()
    {
        // Arrange
        var transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Groceries",
            UtcNow());

        // Act
        ArgumentException? caught = null;
        try
        {
            transaction.AssignCategory(Guid.Empty);
        }
        catch (ArgumentException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.ParamName).IsEqualTo("categoryId");
    }

    [Test]
    public async Task Create_WithEmptyBudgetId_ThrowsValidationException()
    {
        var exception = ThrowsValidationException(() => Transaction.Create(Guid.Empty, Guid.CreateVersion7(), 1m, UsdMinorUnit, DateOnly.FromDateTime(DateTime.UtcNow), "Test", UtcNow()));
        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
    }

    [Test]
    [Arguments(2, "10.005", "Amount must have no more than 2 decimal places.")]
    [Arguments(0, "10.5", "Amount must be a whole number.")]
    [Arguments(3, "10.0005", "Amount must have no more than 3 decimal places.")]
    public async Task Create_WithMoreDecimalPlacesThanTheMinorUnitAllows_ThrowsValidationExceptionStatingTheLimit(
        int minorUnit,
        string amount,
        string expectedMessage)
    {
        // Arrange — the minor unit comes from the account's currency, so the same amount is legal
        // in one currency and not in another. Amounts arrive as strings because decimal is not a
        // legal attribute argument type.
        //
        // This is the one message in the file asserted by value rather than by key, because it is
        // the one that is computed: it forks on the minor unit, and a fork that produced "no more
        // than 0 decimal places" for yen would be visible nonsense in a ledger that no key-only
        // assertion could see. Both sides of the fork are pinned, and the three-place row pins the
        // interpolated number rather than a coincidental 2.

        // Act
        ValidationException exception = ThrowsValidationException(() => Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Money(amount),
            minorUnit,
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Test",
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
        await Assert.That(exception.Errors["Amount"].Single()).IsEqualTo(expectedMessage);
    }

    [Test]
    [Arguments(2, "10.99")]
    [Arguments(0, "10")]
    [Arguments(3, "10.005")]
    [Arguments(4, "10.0005")]
    public async Task Create_WithDecimalPlacesTheMinorUnitAllows_ReturnsTransaction(
        int minorUnit,
        string amount)
    {
        // Arrange — the three- and four-place cases are the point of the change: BHD and KWD have a
        // minor unit of 3, so a hard-coded 2 cannot represent their smallest unit at all.
        decimal expected = Money(amount);

        // Act
        var transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            expected,
            minorUnit,
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Test",
            UtcNow());

        // Assert
        await Assert.That(transaction.Amount).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1000000000.01")]
    [Arguments("-1000000000.01")]
    [Arguments("2000000000")]
    public async Task Create_WithAmountBeyondTheMagnitudeLimit_ThrowsValidationException(string amount)
    {
        // Arrange — the magnitude cap is unchanged, so this looks like a test of nothing new. It is
        // the regression guard for the validation chain: the zero-amount rule used to be the first
        // branch of an else-if chain whose later branches are the decimal-places and magnitude
        // checks. Deleting the zero branch without care takes the magnitude check with it, and only
        // a whole-number over-limit amount (which passes the decimal check) proves it survived.

        // Act
        ValidationException exception = ThrowsValidationException(() => Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Money(amount),
            UsdMinorUnit,
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Test",
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
    }

    [Test]
    [Arguments("1000000000")]
    [Arguments("-1000000000")]
    public async Task Create_WithAmountExactlyAtTheMagnitudeLimit_ReturnsTransaction(string amount)
    {
        // Arrange — the rule refuses only above this value, so the limit itself is legitimate data.
        decimal expected = Money(amount);

        // Act
        var transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            expected,
            UsdMinorUnit,
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Test",
            UtcNow());

        // Assert
        await Assert.That(transaction.Amount).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(5)]
    public async Task Create_WithMinorUnitOutsideTheSupportedRange_ThrowsArgumentOutOfRangeException(int minorUnit)
    {
        // Arrange — the minor unit is never user input: it comes from CurrencyDto.MinorUnit, which
        // the database bounds with CK_currencies_minor_unit. An out-of-range value is therefore a
        // programmer error, and a ValidationException here would leak it to the user as a form
        // error. A ValidationException escapes this helper uncaught, which is the failure we want.

        // Act
        ArgumentOutOfRangeException exception = ThrowsArgumentOutOfRangeException(() => Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1m,
            minorUnit,
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Test",
            UtcNow()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("minorUnit");
    }

    [Test]
    public async Task Create_WithBlankDescription_SetsDescriptionToNull()
    {
        // Act — description is optional, so a blank value is allowed
        var transaction = Transaction.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, UsdMinorUnit, DateOnly.FromDateTime(DateTime.UtcNow), "   ", UtcNow());

        // Assert
        await Assert.That(transaction.Description).IsNull();
    }

    [Test]
    public async Task Create_WithNullDescription_SetsDescriptionToNull()
    {
        // Act
        var transaction = Transaction.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, UsdMinorUnit, DateOnly.FromDateTime(DateTime.UtcNow), null, UtcNow());

        // Assert
        await Assert.That(transaction.Description).IsNull();
    }

    [Test]
    public async Task Create_WithDescriptionLongerThan500Characters_ThrowsValidationException()
    {
        var exception = ThrowsValidationException(() => Transaction.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, UsdMinorUnit, DateOnly.FromDateTime(DateTime.UtcNow), new string('x', 501), UtcNow()));
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
    }

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Parses a money literal the culture-invariant way. The values arrive as strings because
    /// <c>decimal</c> is not a legal attribute argument type.
    /// </summary>
    private static decimal Money(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

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

    private static ArgumentOutOfRangeException ThrowsArgumentOutOfRangeException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ArgumentOutOfRangeException.");
    }
}
