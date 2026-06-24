using Application.Payees.GetPayees;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetPayeesHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsRepositoryPayeesOrderedByName()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var repository = new InMemoryPayeeRepository(
            userId,
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero)));
        await repository.GetOrCreateAsync("Zoo");
        await repository.GetOrCreateAsync("Apple");

        // Act
        var response = await new GetPayeesHandler(repository).HandleAsync(new GetPayeesQuery());

        // Assert
        await Assert.That(response.Items.Count).IsEqualTo(2);
        await Assert.That(response.Items[0].Name).IsEqualTo("Apple");
        await Assert.That(response.Items[1].Name).IsEqualTo("Zoo");
    }

    [Test]
    public async Task HandleAsync_WhenNoPayees_ReturnsEmptyList()
    {
        // Act
        var response = await new GetPayeesHandler(new InMemoryPayeeRepository(
                Guid.CreateVersion7(),
                new FakeTimeProvider(new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero))))
            .HandleAsync(new GetPayeesQuery());

        // Assert
        await Assert.That(response.Items.Count).IsEqualTo(0);
    }
}
