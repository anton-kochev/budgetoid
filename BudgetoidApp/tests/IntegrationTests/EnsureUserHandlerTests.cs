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
        var handler = new EnsureUserHandler(new UserRepository(db), TimeProvider.System);

        Guid userId = await handler.HandleAsync(new EnsureUserCommand("google-1", "person@example.com", "Person"));

        await Assert.That(userId).IsNotEqualTo(Guid.Empty);
        await Assert.That(await db.Users.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureUser_ExistingSubject_ReturnsSameIdNoDuplicateAndRefreshesProfile()
    {
        await using RepositoryTestHost host = await StartHostAsync();
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
        var handler = new EnsureUserHandler(new UserRepository(db), TimeProvider.System);
        Guid originalId = await handler.HandleAsync(new EnsureUserCommand("google-1", "old@example.com", "Old"));

        Guid secondId = await handler.HandleAsync(new EnsureUserCommand("google-1", "new@example.com", "New"));

        await Assert.That(secondId).IsEqualTo(originalId);
        await Assert.That(await db.Users.CountAsync()).IsEqualTo(1);
        await Assert.That((await db.Users.SingleAsync()).Email.Value).IsEqualTo("new@example.com");
    }

    [Test]
    public async Task EnsureUser_ConcurrentSameSubject_NoDuplicateRow()
    {
        await using RepositoryTestHost host = await StartHostAsync();

        Guid[] ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
            var handler = new EnsureUserHandler(new UserRepository(db), TimeProvider.System);
            return await handler.HandleAsync(new EnsureUserCommand("google-1", "person@example.com", "Person"));
        }));

        await using BudgetoidDbContext assertionDb = CreateDb(host.ConnectionString);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(1);
        await Assert.That(await assertionDb.Users.CountAsync()).IsEqualTo(1);
    }

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
