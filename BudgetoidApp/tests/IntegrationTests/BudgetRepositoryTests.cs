using Domain.Budgets;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests;

public sealed class BudgetRepositoryTests
{
    [Test]
    public async Task Budgets_WithTheSameNameForOneUser_AreRejected()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new BudgetRepository(db);
        bool firstAdded = await repository.TryAddAsync(Budget.CreateDefault(userId, UtcAt(hour: 10)));

        // Act — the real PostgreSQL unique violation must surface as false, not as an escaping
        // DbUpdateException, because the provisioning handler's re-read path depends on it.
        bool secondAdded = await repository.TryAddAsync(Budget.CreateDefault(userId, UtcAt(hour: 11)));

        // Assert
        await Assert.That(firstAdded).IsTrue();
        await Assert.That(secondAdded).IsFalse();
        await Assert.That(await db.Budgets.CountAsync(budget => budget.UserId == userId)).IsEqualTo(1);
    }

    [Test]
    public async Task FindFirstForUserAsync_ReturnsTheEarliestBudget()
    {
        // Arrange — inserted newest-first, so a repository ordering by insertion or by Guid.CompareTo
        // over UUID v7 ids would return the wrong row. The ordering is a documented contract on
        // IBudgetRepository, which is what keeps InMemoryBudgetRepository honest.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new BudgetRepository(db);
        Budget later = Budget.Create(userId, "Later", UtcAt(hour: 18));
        Budget earlier = Budget.Create(userId, "Earlier", UtcAt(hour: 6));
        await repository.TryAddAsync(later);
        await repository.TryAddAsync(earlier);

        // Act
        Budget? found = await repository.FindFirstForUserAsync(userId);

        // Assert
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(earlier.Id);
        await Assert.That(found.Name).IsEqualTo("Earlier");
    }

    [Test]
    public async Task FindFirstForUserAsync_WithNoBudget_ReturnsNull()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new BudgetRepository(db);

        // Act
        Budget? found = await repository.FindFirstForUserAsync(userId);

        // Assert
        await Assert.That(found).IsNull();
    }

    private static BudgetoidDbContext CreateDb(RepositoryTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    private static DateTime UtcAt(int hour) =>
        new(2026, 7, 14, hour, 0, 0, DateTimeKind.Utc);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
