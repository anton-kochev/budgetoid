using System.Globalization;
using Domain.Accounts;
using Domain.Common;

namespace UnitTests;

public sealed class AccountTests
{
    /// <summary>
    /// Minor unit of a two-decimal currency such as USD. Named rather than inlined so a call site
    /// that does not care about precision does not read as if <c>2</c> were a magic rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Create_WithValidInput_TrimsNameStoresTypeOpeningBalanceAndCreatedAtUtc()
    {
        var budgetId = Guid.CreateVersion7();
        DateTime createdAtUtc = new(2026, 6, 25, 13, 14, 15, DateTimeKind.Utc);

        Account account = Account.Create(budgetId, "  Checking  ", AccountType.Checking, 100.25m, " usd ", UsdMinorUnit, createdAtUtc);

        await Assert.That(account.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(account.BudgetId).IsEqualTo(budgetId);
        await Assert.That(account.Name).IsEqualTo("Checking");
        await Assert.That(account.Type).IsEqualTo(AccountType.Checking);
        await Assert.That(account.OpeningBalance).IsEqualTo(100.25m);
        await Assert.That(account.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithEmptyBudgetId_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.Empty, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankName_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.CreateVersion7(), "   ", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithNameLongerThan200Characters_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.CreateVersion7(), new string('x', 201), AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithUndefinedAccountType_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.CreateVersion7(), "Checking", (AccountType)999, 0m, "USD", UsdMinorUnit, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Type")).IsTrue();
    }

    [Test]
    public async Task Create_WithZeroOpeningBalance_ReturnsAccount()
    {
        // Arrange — a brand-new account starts empty, so zero is the common case and not an edge.

        // Act
        Account account = Account.Create(
            Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    [Arguments(2, "1.234", "Opening balance must have no more than 2 decimal places.")]
    [Arguments(0, "10.5", "Opening balance must be a whole number.")]
    [Arguments(3, "10.0005", "Opening balance must have no more than 3 decimal places.")]
    public async Task Create_WithMoreDecimalPlacesThanTheMinorUnitAllows_ThrowsValidationExceptionStatingTheLimit(
        int minorUnit,
        string openingBalance,
        string expectedMessage)
    {
        // Arrange — precision follows the account's currency, so the same balance is legal in one
        // currency and not in another. Balances arrive as strings because decimal is not a legal
        // attribute argument type.
        //
        // This is the one message in the file asserted by value rather than by key, because it is
        // the one that is computed: it forks on the minor unit, and a fork that produced "no more
        // than 0 decimal places" for yen would be visible nonsense in a ledger that no key-only
        // assertion could see. Both sides of the fork are pinned, and the three-place row pins the
        // interpolated number rather than a coincidental 2. Update shares ValidateOrThrow with
        // Create, so pinning the sentence here covers both entry points.

        // Act
        ValidationException exception = ThrowsValidationException(() => Account.Create(
            Guid.CreateVersion7(),
            "Checking",
            AccountType.Checking,
            Money(openingBalance),
            "USD",
            minorUnit,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(exception.Errors["OpeningBalance"].Single()).IsEqualTo(expectedMessage);
    }

    [Test]
    [Arguments(2, "10.99")]
    [Arguments(0, "10")]
    [Arguments(3, "10.005")]
    [Arguments(4, "10.0005")]
    public async Task Create_WithDecimalPlacesTheMinorUnitAllows_ReturnsAccount(
        int minorUnit,
        string openingBalance)
    {
        // Arrange — the three-place case is the point of the change: BHD and KWD have a minor unit
        // of 3, so a hard-coded 2 cannot represent their smallest unit at all.
        decimal expected = Money(openingBalance);

        // Act
        Account account = Account.Create(
            Guid.CreateVersion7(),
            "Checking",
            AccountType.Checking,
            expected,
            "USD",
            minorUnit,
            UtcNow());

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1000000000.01")]
    [Arguments("-1000000000.01")]
    [Arguments("2000000000")]
    public async Task Create_WithOpeningBalanceBeyondTheMagnitudeLimit_ThrowsValidationException(
        string openingBalance)
    {
        // Arrange — the magnitude cap is unchanged, but it shares an else-if chain with the decimal
        // check that is being rewritten. The whole-number case passes the decimal check and so is
        // the one that proves the magnitude branch survived the rewrite.

        // Act
        ValidationException exception = ThrowsValidationException(() => Account.Create(
            Guid.CreateVersion7(),
            "Checking",
            AccountType.Checking,
            Money(openingBalance),
            "USD",
            UsdMinorUnit,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
    }

    [Test]
    [Arguments("1000000000")]
    [Arguments("-1000000000")]
    public async Task Create_WithOpeningBalanceExactlyAtTheMagnitudeLimit_ReturnsAccount(
        string openingBalance)
    {
        // Arrange — the rule refuses only above this value, so the limit itself is legitimate data.
        decimal expected = Money(openingBalance);

        // Act
        Account account = Account.Create(
            Guid.CreateVersion7(),
            "Checking",
            AccountType.Checking,
            expected,
            "USD",
            UsdMinorUnit,
            UtcNow());

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(5)]
    public async Task Create_WithMinorUnitOutsideTheSupportedRange_ThrowsArgumentOutOfRangeException(
        int minorUnit)
    {
        // Arrange — the minor unit is never user input: it comes from CurrencyDto.MinorUnit, which
        // the database bounds with CK_currencies_minor_unit. An out-of-range value is therefore a
        // programmer error, and a ValidationException here would leak it to the user as a form
        // error. A ValidationException escapes this helper uncaught, which is the failure we want.

        // Act
        ArgumentOutOfRangeException exception = ThrowsArgumentOutOfRangeException(() => Account.Create(
            Guid.CreateVersion7(),
            "Checking",
            AccountType.Checking,
            0m,
            "USD",
            minorUnit,
            UtcNow()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("minorUnit");
    }

    [Test]
    public async Task Update_WithValidInput_ReplacesNameTypeAndOpeningBalance()
    {
        Account account = Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        account.Update("  Savings  ", AccountType.Savings, 50m, UsdMinorUnit);

        await Assert.That(account.Name).IsEqualTo("Savings");
        await Assert.That(account.Type).IsEqualTo(AccountType.Savings);
        await Assert.That(account.OpeningBalance).IsEqualTo(50m);
    }

    [Test]
    public async Task Update_WithInvalidInput_ThrowsValidationExceptionAndLeavesAccountUnchanged()
    {
        Account account = Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        ValidationException exception = ThrowsValidationException(() =>
            account.Update("   ", AccountType.Savings, 50m, UsdMinorUnit));

        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(account.Name).IsEqualTo("Checking");
        await Assert.That(account.Type).IsEqualTo(AccountType.Checking);
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    [Arguments(2, "1.234")]
    [Arguments(0, "10.5")]
    [Arguments(3, "10.0005")]
    public async Task Update_WithMoreDecimalPlacesThanTheMinorUnitAllows_ThrowsValidationException(
        int minorUnit,
        string openingBalance)
    {
        // Arrange — Update is the path that had no currency at all until now: it re-validated
        // against the account's stored CurrencyCode while rounding to a hard-coded 2.
        Account account = Account.Create(
            Guid.CreateVersion7(), "Cash", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            account.Update("Cash", AccountType.Checking, Money(openingBalance), minorUnit));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    [Arguments(2, "10.99")]
    [Arguments(0, "10")]
    [Arguments(3, "10.005")]
    [Arguments(4, "10.0005")]
    public async Task Update_WithDecimalPlacesTheMinorUnitAllows_ReplacesOpeningBalance(
        int minorUnit,
        string openingBalance)
    {
        // Arrange
        Account account = Account.Create(
            Guid.CreateVersion7(), "Cash", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());
        decimal expected = Money(openingBalance);

        // Act
        account.Update("Cash", AccountType.Checking, expected, minorUnit);

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1000000000.01")]
    [Arguments("-1000000000.01")]
    [Arguments("2000000000")]
    public async Task Update_WithOpeningBalanceBeyondTheMagnitudeLimit_ThrowsValidationException(
        string openingBalance)
    {
        // Arrange — same regression guard as on Create: the magnitude branch must survive the
        // rewrite of the decimal branch it shares an else-if chain with.
        Account account = Account.Create(
            Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            account.Update("Checking", AccountType.Checking, Money(openingBalance), UsdMinorUnit));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    [Arguments("1000000000")]
    [Arguments("-1000000000")]
    public async Task Update_WithOpeningBalanceExactlyAtTheMagnitudeLimit_ReplacesOpeningBalance(
        string openingBalance)
    {
        // Arrange
        Account account = Account.Create(
            Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());
        decimal expected = Money(openingBalance);

        // Act
        account.Update("Checking", AccountType.Checking, expected, UsdMinorUnit);

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(5)]
    public async Task Update_WithMinorUnitOutsideTheSupportedRange_ThrowsArgumentOutOfRangeException(
        int minorUnit)
    {
        // Arrange
        Account account = Account.Create(
            Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        // Act — a programmer error, not user input, for the same reason as on Create.
        ArgumentOutOfRangeException exception = ThrowsArgumentOutOfRangeException(() =>
            account.Update("Checking", AccountType.Checking, 0m, minorUnit));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("minorUnit");
    }

    [Test]
    public async Task Create_StoresNormalizedCurrencyCode()
    {
        Account account = Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, " usd ", UsdMinorUnit, UtcNow());

        await Assert.That(account.CurrencyCode).IsEqualTo("USD");
    }

    [Arguments("")]
    [Arguments("US")]
    [Arguments("USDE")]
    [Arguments("1$2")]
    [Test]
    public async Task Create_WithInvalidCurrencyCode_ThrowsValidationException(string currencyCode)
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, currencyCode, UsdMinorUnit, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("CurrencyCode")).IsTrue();
    }

    [Test]
    public async Task Update_DoesNotChangeCurrencyCode()
    {
        Account account = Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        account.Update("Savings", AccountType.Savings, 50m, UsdMinorUnit);

        await Assert.That(account.CurrencyCode).IsEqualTo("USD");
    }

    private static DateTime UtcNow() => new(2026, 6, 25, 13, 14, 15, DateTimeKind.Utc);

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
