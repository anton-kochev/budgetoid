using Application.Groups.CreateGroup;
using Domain.Groups;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CreateGroupHandlerTests
{
    [Test]
    public async Task HandleAsync_StampsContextUserPersistsAndReturnsDto()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 7, 3, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryGroupRepository(userId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateGroupHandler(repository, new StubUserContext(userId), new FakeTimeProvider(createdAtUtc));

        // Act
        var dto = await handler.HandleAsync(new CreateGroupCommand("  Groceries  ", "  Weekly food  "));
        Group? stored = await repository.GetByIdAsync(dto.Id);

        // Assert
        await Assert.That(repository.AddCallCount).IsEqualTo(1);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.UserId).IsEqualTo(userId);
        await Assert.That(dto.Name).IsEqualTo("Groceries");
        await Assert.That(dto.Description).IsEqualTo("Weekly food");
    }

    [Test]
    public async Task HandleAsync_WithBlankDescription_ReturnsNullDescription()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 7, 3, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryGroupRepository(userId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateGroupHandler(repository, new StubUserContext(userId), new FakeTimeProvider(createdAtUtc));

        // Act
        var dto = await handler.HandleAsync(new CreateGroupCommand("Groceries", "   "));

        // Assert
        await Assert.That(dto.Description).IsNull();
    }
}
