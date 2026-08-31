using Domain.Budgets;

namespace UnitTests.Fakes;

/// <summary>
/// Covers the one behaviour of <see cref="InMemoryBudgetRepository"/> that the provisioning tests
/// silently lean on. Everything else about the fake is exercised through the handler tests; this
/// collision is not, and it would degrade without anything going red.
/// </summary>
public sealed class InMemoryBudgetRepositoryTests
{
    [Test]
    public async Task TryAddAsync_WithASecondNamelessBudgetForOneUser_ReportsTheCollision()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var repository = new InMemoryBudgetRepository();
        bool firstAdded = await repository.TryAddAsync(
            Budget.CreateDefault(Guid.CreateVersion7(), userId, UtcAt(hour: 10)));

        // Act — a second id, because the collision under test is the one on (user_id, name) and a
        // repeated id would model the primary key instead.
        bool secondAdded = await repository.TryAddAsync(
            Budget.CreateDefault(Guid.CreateVersion7(), userId, UtcAt(hour: 11)));

        // Assert — asserted here rather than left to fall out of the fake's name comparison, because
        // it is the whole provisioning race guarantee in miniature: with no name to collide on, the
        // real index rejects the second row only because it is NULLS NOT DISTINCT, and a fake that
        // accepted the row would let a handler that creates two budgets per user pass every test.
        await Assert.That(firstAdded).IsTrue();
        await Assert.That(secondAdded).IsFalse();
        await Assert.That(repository.Budgets.Count).IsEqualTo(1);
    }

    private static DateTime UtcAt(int hour) =>
        new(2026, 7, 14, hour, 0, 0, DateTimeKind.Utc);
}
