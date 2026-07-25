using Application.Users.EnsureUser;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests;

public sealed class EnsureUserHandlerTests
{
    [Test]
    public async Task EnsureUser_NewSubject_CreatesExactlyOneUser()
    {
        await using RepositoryTestHost host = await StartHostAsync();
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
        EnsureUserHandler handler = CreateHandler(db);

        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com", "Person"));

        await Assert.That(provisioned.UserId).IsNotEqualTo(Guid.Empty);
        await Assert.That(await db.Users.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureUser_ExistingSubject_ReturnsSameIdNoDuplicateAndRefreshesProfile()
    {
        await using RepositoryTestHost host = await StartHostAsync();
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
        EnsureUserHandler handler = CreateHandler(db);
        ProvisionedUser original = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "old@example.com", "Old"));

        ProvisionedUser second = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "new@example.com", "New"));

        await Assert.That(second.UserId).IsEqualTo(original.UserId);
        await Assert.That(second.BudgetId).IsEqualTo(original.BudgetId);
        await Assert.That(await db.Users.CountAsync()).IsEqualTo(1);
        await Assert.That((await db.Users.SingleAsync()).Email.Value).IsEqualTo("new@example.com");
    }

    [Test]
    public async Task EnsureUser_ConcurrentSameSubject_NoDuplicateRow()
    {
        await using RepositoryTestHost host = await StartHostAsync();

        ProvisionedUser[] provisioned = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
            EnsureUserHandler handler = CreateHandler(db);
            return await handler.HandleAsync(new EnsureUserCommand("google-1", "person@example.com", "Person"));
        }));

        // The budgets assertions are not confirming a schema invariant — there is deliberately no
        // one-budget-per-user constraint — they ARE the guard for FR-001 under concurrency.
        await using BudgetoidDbContext assertionDb = CreateDb(host.ConnectionString);
        await Assert.That(provisioned.Select(result => result.UserId).Distinct().Count()).IsEqualTo(1);
        await Assert.That(provisioned.Select(result => result.BudgetId).Distinct().Count()).IsEqualTo(1);
        await Assert.That(await assertionDb.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await assertionDb.Budgets.CountAsync()).IsEqualTo(1);
    }

    private static EnsureUserHandler CreateHandler(BudgetoidDbContext db) => new(
        new UserRepository(db),
        new BudgetRepository(db),
        TimeProvider.System);

    private static BudgetoidDbContext CreateDb(string connectionString) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
