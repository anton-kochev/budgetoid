using Application.Currencies;
using Application.Transactions.CreateTransaction;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CreateTransactionHandlerTests
{
    [Test]
    public async Task HandleAsync_WithValidCommand_PersistsAndReturnsEnrichedAccountFields()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var date = new DateOnly(2026, 6, 12);

        // Act
        var dto = await fixture.Handler.HandleAsync(
            new CreateTransactionCommand(-42.50m, date, fixture.Account.Id, "Groceries"));
        var stored = (await fixture.Transactions.GetAllAsync()).Single();

        // Assert
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(1);
        await Assert.That(stored.BudgetId).IsEqualTo(fixture.BudgetId);
        await Assert.That(dto.Id).IsEqualTo(stored.Id);
        await Assert.That(dto.AccountId).IsEqualTo(fixture.Account.Id);
        await Assert.That(dto.AccountName).IsEqualTo("Checking");
        await Assert.That(dto.CurrencyCode).IsEqualTo("USD");
        await Assert.That(dto.CurrencySymbol).IsEqualTo("$");
        await Assert.That(dto.CategoryId).IsNull();
        await Assert.That(dto.CategoryName).IsNull();
        await Assert.That(dto.CategoryGroupId).IsNull();
        await Assert.That(dto.CategoryGroupName).IsNull();
    }

    [Test]
    public async Task HandleAsync_WithUnknownAccount_RejectsBeforeCreatingPayeeOrTransaction()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                -4.50m,
                new DateOnly(2026, 6, 12),
                Guid.CreateVersion7(),
                "Coffee",
                "Starbucks")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("AccountId")).IsTrue();
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
        await Assert.That(fixture.Payees.GetOrCreateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithPayee_CreatesAndLinksPayee()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();

        // Act
        var dto = await fixture.Handler.HandleAsync(new CreateTransactionCommand(
            -4.50m,
            new DateOnly(2026, 6, 12),
            fixture.Account.Id,
            "Coffee",
            "  Starbucks  "));
        var stored = (await fixture.Transactions.GetAllAsync()).Single();
        var payee = (await fixture.Payees.GetAllAsync()).Single();

        // Assert
        await Assert.That(stored.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(dto.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(dto.PayeeName).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task HandleAsync_WithCategory_LinksCategoryAndReturnsCurrentHierarchy()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        CategoryGroup categoryGroup = await fixture.CategoryGroups.CreateAsync("Essential Obligations");
        Category category = await fixture.Categories.CreateAsync(categoryGroup.Id, "Groceries");

        // Act
        var dto = await fixture.Handler.HandleAsync(new CreateTransactionCommand(
            -4.50m,
            new DateOnly(2026, 6, 12),
            fixture.Account.Id,
            "Food",
            CategoryId: category.Id));
        var stored = (await fixture.Transactions.GetAllAsync()).Single();

        // Assert
        await Assert.That(stored.CategoryId).IsEqualTo(category.Id);
        await Assert.That(dto.CategoryId).IsEqualTo(category.Id);
        await Assert.That(dto.CategoryName).IsEqualTo("Groceries");
        await Assert.That(dto.CategoryGroupId).IsEqualTo(categoryGroup.Id);
        await Assert.That(dto.CategoryGroupName).IsEqualTo("Essential Obligations");
    }

    [Test]
    public async Task HandleAsync_WithUnknownCategory_RejectsWithoutPersisting()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                -4.50m,
                new DateOnly(2026, 6, 12),
                fixture.Account.Id,
                "Coffee",
                PayeeName: "Starbucks",
                CategoryId: Guid.CreateVersion7())));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("CategoryId")).IsTrue();
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
        await Assert.That(fixture.Payees.GetOrCreateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithInvalidTransaction_DoesNotCreatePayeeOrPersist()
    {
        // Arrange — the invalid amount is one with too many decimal places for the account's USD.
        // A zero amount used to serve here and no longer can: zero is a legitimate ledger entry.
        Fixture fixture = await Fixture.CreateAsync();

        // Act
        _ = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                1.234m,
                new DateOnly(2026, 6, 12),
                fixture.Account.Id,
                "Invalid",
                "Starbucks")));

        // Assert
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
        await Assert.That(fixture.Payees.GetOrCreateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithZeroAmount_PersistsTheTransaction()
    {
        // Arrange — a fully discounted purchase is worth recording for its payee and date even
        // though it nets to nothing, and the handler must not stand in the way of that.
        Fixture fixture = await Fixture.CreateAsync();

        // Act
        var dto = await fixture.Handler.HandleAsync(new CreateTransactionCommand(
            0m,
            new DateOnly(2026, 6, 12),
            fixture.Account.Id,
            "Fully discounted"));
        var stored = (await fixture.Transactions.GetAllAsync()).Single();

        // Assert
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(1);
        await Assert.That(dto.Amount).IsEqualTo(0m);
        await Assert.That(stored.Amount).IsEqualTo(0m);
    }

    [Test]
    public async Task HandleAsync_ValidatesTheAmountAgainstTheAccountCurrencyMinorUnit()
    {
        // Arrange — the handler already resolves the account's currency; this proves it passes that
        // currency's minor unit to the domain rather than a hard-coded 2. JPY has a minor unit of 0,
        // so -42.50 is not a representable amount of yen, and a handler still passing 2 accepts it.
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Currencies.Add(new CurrencyDto("JPY", "Yen", "¥", 0));
        Account yenAccount = await fixture.Accounts.CreateAsync("Cash", AccountType.Checking, 0m, "JPY");

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                -42.50m,
                new DateOnly(2026, 6, 12),
                yenAccount.Id,
                "Ramen")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
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

    private sealed class Fixture
    {
        private Fixture()
        {
        }

        public required Guid BudgetId { get; init; }
        public required Account Account { get; init; }
        public required InMemoryAccountRepository Accounts { get; init; }
        public required InMemoryCurrencyReadService Currencies { get; init; }
        public required InMemoryTransactionRepository Transactions { get; init; }
        public required InMemoryPayeeRepository Payees { get; init; }
        public required InMemoryCategoryGroupRepository CategoryGroups { get; init; }
        public required InMemoryCategoryRepository Categories { get; init; }
        public required CreateTransactionHandler Handler { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var budgetId = Guid.CreateVersion7();
            var timeProvider = new FakeTimeProvider(
                new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
            var accounts = new InMemoryAccountRepository(budgetId, timeProvider);
            Account account = await accounts.CreateAsync("Checking");
            var transactions = new InMemoryTransactionRepository();
            var payees = new InMemoryPayeeRepository(budgetId, timeProvider);
            var categoryGroups = new InMemoryCategoryGroupRepository(budgetId, timeProvider);
            var categories = new InMemoryCategoryRepository(budgetId, timeProvider, categoryGroups);
            var currencies = new InMemoryCurrencyReadService();
            var handler = new CreateTransactionHandler(
                transactions,
                accounts,
                currencies,
                payees,
                categories,
                categoryGroups,
                new StubBudgetContext(budgetId),
                timeProvider);

            return new Fixture
            {
                BudgetId = budgetId,
                Account = account,
                Accounts = accounts,
                Currencies = currencies,
                Transactions = transactions,
                Payees = payees,
                CategoryGroups = categoryGroups,
                Categories = categories,
                Handler = handler,
            };
        }
    }
}
