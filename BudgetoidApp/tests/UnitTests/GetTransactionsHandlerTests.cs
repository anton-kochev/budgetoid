using Application.Transactions.CreateTransaction;
using Application.Transactions.GetTransactions;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetTransactionsHandlerTests
{
    // Per-user isolation is enforced by the EF global query filter and verified in
    // IntegrationTests/TransactionIsolationTests. The in-memory fake has no such filter, so
    // these unit tests cover only what the handler itself does: return the repository's
    // transactions, newest first, mapped to DTOs.
    [Test]
    public async Task HandleAsync_ReturnsRepositoryTransactionsNewestFirst()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();

        await new CreateTransactionHandler(
                repository,
                new InMemoryPayeeRepository(userId, new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 13, 14, 15, TimeSpan.Zero))),
                new StubUserContext(userId),
                new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 13, 14, 15, TimeSpan.Zero)))
            .HandleAsync(new CreateTransactionCommand(10m, new DateOnly(2026, 6, 11), "Older"));
        await new CreateTransactionHandler(
                repository,
                new InMemoryPayeeRepository(userId, new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero))),
                new StubUserContext(userId),
                new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero)))
            .HandleAsync(new CreateTransactionCommand(20m, new DateOnly(2026, 6, 12), "Newest"));

        // Act
        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        // Assert
        await Assert.That(response.Items.Count).IsEqualTo(2);
        await Assert.That(response.Items[0].Description).IsEqualTo("Newest");
        await Assert.That(response.Items[1].Description).IsEqualTo("Older");
    }

    [Test]
    public async Task HandleAsync_ReturnsProjectedPayeeFields()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var payees = new InMemoryPayeeRepository(userId, new FakeTimeProvider(createdAtUtc));
        await new CreateTransactionHandler(
                repository,
                payees,
                new StubUserContext(userId),
                new FakeTimeProvider(createdAtUtc))
            .HandleAsync(new CreateTransactionCommand(20m, new DateOnly(2026, 6, 12), "Coffee", "Starbucks"));
        var payee = (await payees.GetAllAsync()).Single();
        repository.SetPayeeProjection(payee.Id, payee.Name);

        // Act
        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        // Assert
        await Assert.That(response.Items.Single().PayeeId).IsEqualTo(payee.Id);
        await Assert.That(response.Items.Single().PayeeName).IsEqualTo("Starbucks");
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
