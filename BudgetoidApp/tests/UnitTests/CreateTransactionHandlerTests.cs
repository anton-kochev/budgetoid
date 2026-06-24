using Application.Transactions.CreateTransaction;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CreateTransactionHandlerTests
{
    [Test]
    public async Task HandleAsync_WithValidCommand_PersistsWithContextUserIdAndReturnsDto()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var payees = new InMemoryPayeeRepository(userId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateTransactionHandler(
            repository,
            payees,
            new StubUserContext(userId),
            new FakeTimeProvider(createdAtUtc));
        var date = new DateOnly(2026, 6, 12);

        // Act
        var dto = await handler.HandleAsync(new CreateTransactionCommand(-42.50m, date, "Groceries"));
        var stored = await repository.GetAllAsync();

        // Assert
        await Assert.That(repository.AddCallCount).IsEqualTo(1);
        await Assert.That(stored.Count).IsEqualTo(1);
        await Assert.That(stored[0].UserId).IsEqualTo(userId);
        await Assert.That(dto.Id).IsEqualTo(stored[0].Id);
        await Assert.That(dto.Amount).IsEqualTo(-42.50m);
        await Assert.That(dto.Date).IsEqualTo(date);
        await Assert.That(dto.Description).IsEqualTo("Groceries");
        await Assert.That(dto.CreatedAtUtc).IsEqualTo(createdAtUtc.UtcDateTime);
        await Assert.That(stored[0].CreatedAtUtc).IsEqualTo(createdAtUtc.UtcDateTime);
        await Assert.That(dto.PayeeId).IsNull();
        await Assert.That(dto.PayeeName).IsNull();
        await Assert.That(stored[0].PayeeId).IsNull();
        await Assert.That(payees.GetOrCreateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithBlankDescription_ReturnsEmptyDescription()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var payees = new InMemoryPayeeRepository(userId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateTransactionHandler(
            repository,
            payees,
            new StubUserContext(userId),
            new FakeTimeProvider(createdAtUtc));

        // Act — blank description is stored as null on the entity but normalised to "" in the dto
        var dto = await handler.HandleAsync(new CreateTransactionCommand(-4.50m, new DateOnly(2026, 6, 12), "   "));
        var stored = (await repository.GetAllAsync()).Single();

        // Assert
        await Assert.That(stored.Description).IsNull();
        await Assert.That(dto.Description).IsEqualTo("");
    }

    [Test]
    public async Task HandleAsync_WithNewPayeeName_CreatesPayeeLinksTransactionAndReturnsPayeeFields()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var payees = new InMemoryPayeeRepository(userId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateTransactionHandler(
            repository,
            payees,
            new StubUserContext(userId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        var dto = await handler.HandleAsync(new CreateTransactionCommand(-4.50m, new DateOnly(2026, 6, 12), "Coffee", "  Starbucks  "));
        var stored = (await repository.GetAllAsync()).Single();
        var payee = (await payees.GetAllAsync()).Single();

        // Assert
        await Assert.That(stored.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(dto.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(dto.PayeeName).IsEqualTo("Starbucks");
        await Assert.That(payee.Name).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task HandleAsync_WithExistingPayeeNameDifferentCase_ReusesPayee()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var payees = new InMemoryPayeeRepository(userId, new FakeTimeProvider(createdAtUtc));
        await payees.GetOrCreateAsync("Starbucks");
        var existing = (await payees.GetAllAsync()).Single();
        var handler = new CreateTransactionHandler(
            repository,
            payees,
            new StubUserContext(userId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        var dto = await handler.HandleAsync(new CreateTransactionCommand(-4.50m, new DateOnly(2026, 6, 12), "Coffee", "starbucks"));

        // Assert
        await Assert.That((await payees.GetAllAsync()).Count).IsEqualTo(1);
        await Assert.That(dto.PayeeId).IsEqualTo(existing.Id);
        await Assert.That(dto.PayeeName).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task HandleAsync_WithInvalidCommand_DoesNotPersistOrCreatePayee()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var payees = new InMemoryPayeeRepository(userId, new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero)));
        var handler = new CreateTransactionHandler(
            repository,
            payees,
            new StubUserContext(userId),
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero)));

        // Act
        try
        {
            await handler.HandleAsync(new CreateTransactionCommand(0m, DateOnly.FromDateTime(DateTime.UtcNow), "Invalid", "Starbucks"));
        }
        catch (ValidationException)
        {
            // Assert
            await Assert.That(repository.AddCallCount).IsEqualTo(0);
            await Assert.That(payees.GetOrCreateCallCount).IsEqualTo(0);
            await Assert.That((await payees.GetAllAsync()).Count).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
