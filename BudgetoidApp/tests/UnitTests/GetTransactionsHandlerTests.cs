using Application.Transactions.CreateTransaction;
using Application.Transactions.GetTransactions;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetTransactionsHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsTransactionsNewestFirst()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var olderTime = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 11, 13, 14, 15, TimeSpan.Zero));
        var newerTime = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
        var accounts = new InMemoryAccountRepository(userId, olderTime);
        Account account = await accounts.CreateAsync();
        var categoryGroups = new InMemoryCategoryGroupRepository(userId, olderTime);
        var categories = new InMemoryCategoryRepository(userId, olderTime, categoryGroups);

        await CreateAsync(repository, accounts, categoryGroups, categories, olderTime, account, "Older");
        await CreateAsync(repository, accounts, categoryGroups, categories, newerTime, account, "Newest");

        // Act
        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        // Assert
        await Assert.That(response.Items.Count).IsEqualTo(2);
        await Assert.That(response.Items[0].Description).IsEqualTo("Newest");
        await Assert.That(response.Items[1].Description).IsEqualTo("Older");
    }

    [Test]
    public async Task HandleAsync_ReturnsCurrentCategoryAndCategoryGroupProjection()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
        var accounts = new InMemoryAccountRepository(userId, timeProvider);
        Account account = await accounts.CreateAsync();
        var categoryGroups = new InMemoryCategoryGroupRepository(userId, timeProvider);
        var categories = new InMemoryCategoryRepository(userId, timeProvider, categoryGroups);
        CategoryGroup categoryGroup = await categoryGroups.CreateAsync("Essential Obligations");
        Category category = await categories.CreateAsync(categoryGroup.Id, "Groceries");
        await CreateAsync(
            repository,
            accounts,
            categoryGroups,
            categories,
            timeProvider,
            account,
            "Food",
            category.Id);
        repository.SetCategoryProjection(
            category.Id,
            category.Name,
            categoryGroup.Id,
            categoryGroup.Name);

        // Act
        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        // Assert
        var dto = response.Items.Single();
        await Assert.That(dto.CategoryId).IsEqualTo(category.Id);
        await Assert.That(dto.CategoryName).IsEqualTo("Groceries");
        await Assert.That(dto.CategoryGroupId).IsEqualTo(categoryGroup.Id);
        await Assert.That(dto.CategoryGroupName).IsEqualTo("Essential Obligations");
    }

    [Test]
    public async Task HandleAsync_WhenNoTransactions_ReturnsEmptyList()
    {
        // Act
        var response = await new GetTransactionsHandler(new InMemoryTransactionRepository())
            .HandleAsync(new GetTransactionsQuery());

        // Assert
        await Assert.That(response.Items.Count).IsEqualTo(0);
    }

    private static Task CreateAsync(
        InMemoryTransactionRepository transactions,
        InMemoryAccountRepository accounts,
        InMemoryCategoryGroupRepository categoryGroups,
        InMemoryCategoryRepository categories,
        TimeProvider timeProvider,
        Account account,
        string description,
        Guid? categoryId = null)
    {
        var handler = new CreateTransactionHandler(
            transactions,
            accounts,
            new InMemoryCurrencyReadService(),
            new InMemoryPayeeRepository(account.UserId, timeProvider),
            categories,
            categoryGroups,
            new StubUserContext(account.UserId),
            timeProvider);
        return handler.HandleAsync(new CreateTransactionCommand(
            20m,
            DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime),
            account.Id,
            description,
            CategoryId: categoryId));
    }
}
