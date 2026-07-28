using Application.Accounts.CreateAccount;
using Application.Currencies;
using Domain.Accounts;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CreateAccountHandlerTests
{
    [Test]
    public async Task HandleAsync_StampsContextUserPersistsAndReturnsDto()
    {
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var currencies = new InMemoryCurrencyReadService();
        var handler = new CreateAccountHandler(repository, currencies, new StubBudgetContext(budgetId), new FakeTimeProvider(createdAtUtc));

        var dto = await handler.HandleAsync(new CreateAccountCommand("  Checking  ", AccountType.Checking, 100m, "usd"));
        var stored = await repository.GetByIdAsync(dto.Id);

        await Assert.That(repository.AddCallCount).IsEqualTo(1);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.BudgetId).IsEqualTo(budgetId);
        await Assert.That(dto.Name).IsEqualTo("Checking");
        await Assert.That(dto.Type).IsEqualTo(AccountType.Checking);
        await Assert.That(dto.OpeningBalance).IsEqualTo(100m);
        await Assert.That(dto.CreatedAtUtc).IsEqualTo(createdAtUtc.UtcDateTime);
        await Assert.That(dto.CurrencyCode).IsEqualTo("USD");
        await Assert.That(dto.CurrencyName).IsEqualTo("US Dollar");
        await Assert.That(dto.CurrencySymbol).IsEqualTo("$");
        await Assert.That(dto.CurrencyMinorUnit).IsEqualTo(2);
    }

    [Test]
    public async Task HandleAsync_WithUnknownCurrency_ThrowsValidationExceptionAndDoesNotPersist()
    {
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateAccountHandler(
            repository,
            new InMemoryCurrencyReadService(),
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new CreateAccountCommand("Checking", AccountType.Checking, 100m, "ZZZ")));

        await Assert.That(exception.Errors.ContainsKey("CurrencyCode")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_ValidatesTheOpeningBalanceAgainstTheResolvedCurrencyMinorUnit()
    {
        // Arrange — the handler already resolves the currency; this proves it passes that currency's
        // minor unit to the domain rather than a hard-coded 2. JPY has a minor unit of 0, so 100.50
        // is not a representable amount of yen, and a handler still passing 2 accepts it.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var currencies = new InMemoryCurrencyReadService();
        currencies.Add(new CurrencyDto("JPY", "Yen", "¥", 0));
        var handler = new CreateAccountHandler(
            repository,
            currencies,
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new CreateAccountCommand("Cash", AccountType.Checking, 100.50m, "JPY")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

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
