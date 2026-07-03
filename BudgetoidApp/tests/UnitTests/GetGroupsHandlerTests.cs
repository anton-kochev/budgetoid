using Application.Groups.GetGroups;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetGroupsHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsReadServiceGroupsOrderedByName()
    {
        // Arrange
        var repository = new InMemoryGroupRepository(
            Guid.CreateVersion7(),
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 3, 13, 14, 15, TimeSpan.Zero)));
        await repository.CreateAsync("Travel");
        await repository.CreateAsync("Groceries");
        var handler = new GetGroupsHandler(repository);

        // Act
        var response = await handler.HandleAsync(new GetGroupsQuery());

        // Assert
        await Assert.That(response.Items.Count).IsEqualTo(2);
        await Assert.That(response.Items[0].Name).IsEqualTo("Groceries");
        await Assert.That(response.Items[1].Name).IsEqualTo("Travel");
    }
}
