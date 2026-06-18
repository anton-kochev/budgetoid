using Application.Transactions.CreateTransaction;
using Domain.Common;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CreateTransactionHandlerTests
{
    [Test]
    public async Task HandleAsync_WithValidCommand_PersistsWithContextUserIdAndReturnsDto()
    {
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var handler = new CreateTransactionHandler(repository, new StubUserContext(userId));
        var date = new DateOnly(2026, 6, 12);

        var dto = await handler.HandleAsync(new CreateTransactionCommand(-42.50m, date, "Groceries"));
        var stored = await repository.GetAllAsync();

        await Assert.That(repository.AddCallCount).IsEqualTo(1);
        await Assert.That(stored.Count).IsEqualTo(1);
        await Assert.That(stored[0].UserId).IsEqualTo(userId);
        await Assert.That(dto.Id).IsEqualTo(stored[0].Id);
        await Assert.That(dto.Amount).IsEqualTo(-42.50m);
        await Assert.That(dto.Date).IsEqualTo(date);
        await Assert.That(dto.Description).IsEqualTo("Groceries");
        await Assert.That(dto.CreatedAtUtc).IsEqualTo(stored[0].CreatedAtUtc);
    }

    [Test]
    public async Task HandleAsync_WithInvalidCommand_DoesNotPersist()
    {
        var repository = new InMemoryTransactionRepository();
        var handler = new CreateTransactionHandler(repository, new StubUserContext(Guid.CreateVersion7()));

        try
        {
            await handler.HandleAsync(new CreateTransactionCommand(0m, DateOnly.FromDateTime(DateTime.UtcNow), "Invalid"));
        }
        catch (ValidationException)
        {
            await Assert.That(repository.AddCallCount).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
