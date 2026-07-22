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
        await Assert.That(stored.UserId).IsEqualTo(fixture.UserId);
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
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();

        // Act
        _ = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                0m,
                new DateOnly(2026, 6, 12),
                fixture.Account.Id,
                "Invalid",
                "Starbucks")));

        // Assert
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
        await Assert.That(fixture.Payees.GetOrCreateCallCount).IsEqualTo(0);
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

        public required Guid UserId { get; init; }
        public required Account Account { get; init; }
        public required InMemoryTransactionRepository Transactions { get; init; }
        public required InMemoryPayeeRepository Payees { get; init; }
        public required InMemoryCategoryGroupRepository CategoryGroups { get; init; }
        public required InMemoryCategoryRepository Categories { get; init; }
        public required CreateTransactionHandler Handler { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var userId = Guid.CreateVersion7();
            var timeProvider = new FakeTimeProvider(
                new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
            var accounts = new InMemoryAccountRepository(userId, timeProvider);
            Account account = await accounts.CreateAsync("Checking");
            var transactions = new InMemoryTransactionRepository();
            var payees = new InMemoryPayeeRepository(userId, timeProvider);
            var categoryGroups = new InMemoryCategoryGroupRepository(userId, timeProvider);
            var categories = new InMemoryCategoryRepository(userId, timeProvider, categoryGroups);
            var handler = new CreateTransactionHandler(
                transactions,
                accounts,
                new InMemoryCurrencyReadService(),
                payees,
                categories,
                categoryGroups,
                new StubUserContext(userId),
                timeProvider);

            return new Fixture
            {
                UserId = userId,
                Account = account,
                Transactions = transactions,
                Payees = payees,
                CategoryGroups = categoryGroups,
                Categories = categories,
                Handler = handler,
            };
        }
    }
}
