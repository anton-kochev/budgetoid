using Domain.Accounts;
using Domain.Common;

namespace UnitTests;

public sealed class AccountTests
{
    [Test]
    public async Task Create_WithValidInput_TrimsNameStoresTypeOpeningBalanceAndCreatedAtUtc()
    {
        var userId = Guid.CreateVersion7();
        DateTime createdAtUtc = new(2026, 6, 25, 13, 14, 15, DateTimeKind.Utc);

        Account account = Account.Create(userId, "  Checking  ", AccountType.Checking, 100.25m, " usd ", createdAtUtc);

        await Assert.That(account.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(account.UserId).IsEqualTo(userId);
        await Assert.That(account.Name).IsEqualTo("Checking");
        await Assert.That(account.Type).IsEqualTo(AccountType.Checking);
        await Assert.That(account.OpeningBalance).IsEqualTo(100.25m);
        await Assert.That(account.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithEmptyUserId_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.Empty, "Checking", AccountType.Checking, 0m, "USD", UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankName_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.CreateVersion7(), "   ", AccountType.Checking, 0m, "USD", UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithNameLongerThan200Characters_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.CreateVersion7(), new string('x', 201), AccountType.Checking, 0m, "USD", UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithUndefinedAccountType_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.CreateVersion7(), "Checking", (AccountType)999, 0m, "USD", UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Type")).IsTrue();
    }

    [Test]
    public async Task Create_WithOpeningBalanceMoreThanTwoDecimalPlaces_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 1.234m, "USD", UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
    }

    [Test]
    public async Task Create_WithOpeningBalanceAbsoluteValueOverLimit_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, -1_000_000_000.01m, "USD", UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
    }

    [Test]
    public async Task Update_WithValidInput_ReplacesNameTypeAndOpeningBalance()
    {
        Account account = Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UtcNow());

        account.Update("  Savings  ", AccountType.Savings, 50m);

        await Assert.That(account.Name).IsEqualTo("Savings");
        await Assert.That(account.Type).IsEqualTo(AccountType.Savings);
        await Assert.That(account.OpeningBalance).IsEqualTo(50m);
    }

    [Test]
    public async Task Update_WithInvalidInput_ThrowsValidationExceptionAndLeavesAccountUnchanged()
    {
        Account account = Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UtcNow());

        ValidationException exception = ThrowsValidationException(() =>
            account.Update("   ", AccountType.Savings, 50m));

        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(account.Name).IsEqualTo("Checking");
        await Assert.That(account.Type).IsEqualTo(AccountType.Checking);
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }


    [Test]
    public async Task Create_StoresNormalizedCurrencyCode()
    {
        Account account = Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, " usd ", UtcNow());

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
            Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, currencyCode, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("CurrencyCode")).IsTrue();
    }

    [Test]
    public async Task Update_DoesNotChangeCurrencyCode()
    {
        Account account = Account.Create(Guid.CreateVersion7(), "Checking", AccountType.Checking, 0m, "USD", UtcNow());

        account.Update("Savings", AccountType.Savings, 50m);

        await Assert.That(account.CurrencyCode).IsEqualTo("USD");
    }

    private static DateTime UtcNow() => new(2026, 6, 25, 13, 14, 15, DateTimeKind.Utc);

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
