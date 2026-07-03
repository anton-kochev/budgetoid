using Application.Transactions.CreateTransaction;
using Application.Transactions.GetTransactions;
using Domain.Accounts;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetTransactionsHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsRepositoryTransactionsNewestFirst()
    {
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var olderTime = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 13, 14, 15, TimeSpan.Zero));
        var newerTime = new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
        var accounts = new InMemoryAccountRepository(userId, olderTime);
        Account account = await accounts.CreateAsync();

        await new CreateTransactionHandler(
                repository,
                accounts,
                new InMemoryCurrencyReadService(),
                new InMemoryPayeeRepository(userId, olderTime),
                new InMemoryGroupRepository(userId, olderTime),
                new StubUserContext(userId),
                olderTime)
            .HandleAsync(new CreateTransactionCommand(10m, new DateOnly(2026, 6, 11), account.Id, "Older"));
        await new CreateTransactionHandler(
                repository,
                accounts,
                new InMemoryCurrencyReadService(),
                new InMemoryPayeeRepository(userId, newerTime),
                new InMemoryGroupRepository(userId, newerTime),
                new StubUserContext(userId),
                newerTime)
            .HandleAsync(new CreateTransactionCommand(20m, new DateOnly(2026, 6, 12), account.Id, "Newest"));

        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        await Assert.That(response.Items.Count).IsEqualTo(2);
        await Assert.That(response.Items[0].Description).IsEqualTo("Newest");
        await Assert.That(response.Items[1].Description).IsEqualTo("Older");
    }

    [Test]
    public async Task HandleAsync_ReturnsProjectedPayeeFields()
    {
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(createdAtUtc);
        var accounts = new InMemoryAccountRepository(userId, timeProvider);
        Account account = await accounts.CreateAsync();
        var payees = new InMemoryPayeeRepository(userId, timeProvider);
        await new CreateTransactionHandler(
                repository,
                accounts,
                new InMemoryCurrencyReadService(),
                payees,
                new InMemoryGroupRepository(userId, timeProvider),
                new StubUserContext(userId),
                timeProvider)
            .HandleAsync(new CreateTransactionCommand(20m, new DateOnly(2026, 6, 12), account.Id, "Coffee", "Starbucks"));
        var payee = (await payees.GetAllAsync()).Single();
        repository.SetPayeeProjection(payee.Id, payee.Name);

        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        await Assert.That(response.Items.Single().PayeeId).IsEqualTo(payee.Id);
        await Assert.That(response.Items.Single().PayeeName).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task HandleAsync_ReturnsProjectedGroupFields()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(createdAtUtc);
        var accounts = new InMemoryAccountRepository(userId, timeProvider);
        Account account = await accounts.CreateAsync();
        var groups = new InMemoryGroupRepository(userId, timeProvider);
        Domain.Groups.Group group = await groups.CreateAsync("Groceries");
        await new CreateTransactionHandler(
                repository,
                accounts,
                new InMemoryCurrencyReadService(),
                new InMemoryPayeeRepository(userId, timeProvider),
                groups,
                new StubUserContext(userId),
                timeProvider)
            .HandleAsync(new CreateTransactionCommand(20m, new DateOnly(2026, 6, 12), account.Id, "Coffee", GroupId: group.Id));
        repository.SetGroupProjection(group.Id, group.Name);

        // Act
        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        // Assert
        await Assert.That(response.Items.Single().GroupId).IsEqualTo(group.Id);
        await Assert.That(response.Items.Single().GroupName).IsEqualTo("Groceries");
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
}
