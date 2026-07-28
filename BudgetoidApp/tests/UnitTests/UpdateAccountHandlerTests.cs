using Application.Accounts.UpdateAccount;
using Application.Currencies;
using Domain.Accounts;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class UpdateAccountHandlerTests
{
    [Test]
    public async Task HandleAsync_UpdatesExistingAccount()
    {
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking");
        var handler = new UpdateAccountHandler(repository, new InMemoryCurrencyReadService());

        await handler.HandleAsync(new UpdateAccountCommand(account.Id, "  Savings  ", AccountType.Savings, 25m));

        await Assert.That(repository.UpdateCallCount).IsEqualTo(1);
        await Assert.That(account.Name).IsEqualTo("Savings");
        await Assert.That(account.Type).IsEqualTo(AccountType.Savings);
        await Assert.That(account.OpeningBalance).IsEqualTo(25m);
    }

    [Test]
    public async Task HandleAsync_WithUnknownAccountId_ThrowsNotFoundException()
    {
        var repository = new InMemoryAccountRepository(Guid.CreateVersion7(), new FakeTimeProvider(UtcNowOffset()));
        var handler = new UpdateAccountHandler(repository, new InMemoryCurrencyReadService());

        try
        {
            await handler.HandleAsync(new UpdateAccountCommand(Guid.CreateVersion7(), "Savings", AccountType.Savings, 25m));
        }
        catch (NotFoundException)
        {
            await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected NotFoundException.");
    }

    [Test]
    public async Task HandleAsync_WithAFractionalBalanceOnAZeroDecimalCurrencyAccount_ThrowsValidationException()
    {
        // Arrange — the domain rule is already covered by AccountTests; what this proves is the
        // wiring. The handler must resolve the currency of the account it loaded and pass that
        // minor unit down, so a handler still passing a hard-coded 2 accepts 25.5 yen and fails
        // here. JPY has a minor unit of 0, and there is no such thing as half a yen.
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Cash", AccountType.Checking, 0m, "JPY");
        var currencies = new InMemoryCurrencyReadService();
        currencies.Add(new CurrencyDto("JPY", "Yen", "¥", 0));
        var handler = new UpdateAccountHandler(repository, currencies);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new UpdateAccountCommand(account.Id, "Cash", AccountType.Checking, 25.5m)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    public async Task HandleAsync_WithAWholeBalanceOnAZeroDecimalCurrencyAccount_UpdatesTheAccount()
    {
        // Arrange — the companion to the rejection above: a zero-decimal currency must still accept
        // its own whole units, or the handler would be refusing every yen amount.
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Cash", AccountType.Checking, 0m, "JPY");
        var currencies = new InMemoryCurrencyReadService();
        currencies.Add(new CurrencyDto("JPY", "Yen", "¥", 0));
        var handler = new UpdateAccountHandler(repository, currencies);

        // Act
        await handler.HandleAsync(new UpdateAccountCommand(account.Id, "Cash", AccountType.Checking, 2500m));

        // Assert
        await Assert.That(repository.UpdateCallCount).IsEqualTo(1);
        await Assert.That(account.OpeningBalance).IsEqualTo(2500m);
    }

    [Test]
    public async Task HandleAsync_WithACurrencyTheReadServiceCannotFind_ThrowsInvalidOperationException()
    {
        // Arrange — the RESTRICT foreign key on accounts.currency_code guarantees the currency row
        // exists, so a missing one is a broken invariant rather than bad user input. It must not be
        // dressed up as a validation error the client could act on.
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking", AccountType.Checking, 0m, "ZZZ");
        var handler = new UpdateAccountHandler(repository, new InMemoryCurrencyReadService());

        // Act
        InvalidOperationException? caught = null;
        try
        {
            await handler.HandleAsync(new UpdateAccountCommand(account.Id, "Checking", AccountType.Checking, 25m));
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        // Assert — the code is asserted rather than the whole sentence: naming it is what makes the
        // failure diagnosable, and the exact wording is not a contract.
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("ZZZ");
        await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
    }

    private static DateTimeOffset UtcNowOffset() => new(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);

    private static async Task<ValidationException> ThrowsValidationExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
