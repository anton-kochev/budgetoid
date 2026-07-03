using Application.Groups.DeleteGroup;
using Domain.Common;
using Domain.Groups;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class DeleteGroupHandlerTests
{
    [Test]
    public async Task HandleAsync_DeletesUnreferencedGroup()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var repository = new InMemoryGroupRepository(userId, new FakeTimeProvider(UtcNowOffset()));
        Group group = await repository.CreateAsync("Groceries");
        var handler = new DeleteGroupHandler(repository);

        // Act
        await handler.HandleAsync(new DeleteGroupCommand(group.Id));

        // Assert
        await Assert.That(repository.DeleteCallCount).IsEqualTo(1);
        await Assert.That(await repository.GetByIdAsync(group.Id)).IsNull();
    }

    [Test]
    public async Task HandleAsync_WithUnknownGroupId_ThrowsNotFoundException()
    {
        // Arrange
        var repository = new InMemoryGroupRepository(Guid.CreateVersion7(), new FakeTimeProvider(UtcNowOffset()));
        var handler = new DeleteGroupHandler(repository);

        // Act
        try
        {
            await handler.HandleAsync(new DeleteGroupCommand(Guid.CreateVersion7()));
        }
        catch (NotFoundException)
        {
            // Assert
            await Assert.That(repository.DeleteCallCount).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected NotFoundException.");
    }

    [Test]
    public async Task HandleAsync_WithReferencedGroup_ThrowsValidationExceptionAndDoesNotDelete()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var repository = new InMemoryGroupRepository(userId, new FakeTimeProvider(UtcNowOffset()));
        Group group = await repository.CreateAsync("Groceries");
        repository.MarkReferenced(group.Id);
        var handler = new DeleteGroupHandler(repository);

        // Act
        try
        {
            await handler.HandleAsync(new DeleteGroupCommand(group.Id));
        }
        catch (ValidationException exception)
        {
            // Assert
            await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
            await Assert.That(repository.DeleteCallCount).IsEqualTo(0);
            await Assert.That(await repository.GetByIdAsync(group.Id)).IsNotNull();
            return;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }

    private static DateTimeOffset UtcNowOffset() => new(2026, 7, 3, 13, 14, 15, TimeSpan.Zero);
}
