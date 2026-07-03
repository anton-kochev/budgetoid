using Application.Groups.UpdateGroup;
using Domain.Common;
using Domain.Groups;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class UpdateGroupHandlerTests
{
    [Test]
    public async Task HandleAsync_RenamesGroupAndUpdatesDescription()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var repository = new InMemoryGroupRepository(userId, new FakeTimeProvider(UtcNowOffset()));
        Group group = await repository.CreateAsync("Groceries", "old");
        var handler = new UpdateGroupHandler(repository);

        // Act
        await handler.HandleAsync(new UpdateGroupCommand(group.Id, "  Food  ", "  new  "));

        // Assert
        await Assert.That(repository.UpdateCallCount).IsEqualTo(1);
        await Assert.That(group.Name).IsEqualTo("Food");
        await Assert.That(group.Description).IsEqualTo("new");
    }

    [Test]
    public async Task HandleAsync_WithUnknownGroupId_ThrowsNotFoundExceptionAndDoesNotUpdate()
    {
        // Arrange
        var repository = new InMemoryGroupRepository(Guid.CreateVersion7(), new FakeTimeProvider(UtcNowOffset()));
        var handler = new UpdateGroupHandler(repository);

        // Act
        try
        {
            await handler.HandleAsync(new UpdateGroupCommand(Guid.CreateVersion7(), "Food", null));
        }
        catch (NotFoundException)
        {
            // Assert
            await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected NotFoundException.");
    }

    private static DateTimeOffset UtcNowOffset() => new(2026, 7, 3, 13, 14, 15, TimeSpan.Zero);
}
