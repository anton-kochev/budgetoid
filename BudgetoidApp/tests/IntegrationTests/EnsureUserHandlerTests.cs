using Application.Users.EnsureUser;
using Domain.Common;
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

    [Test]
    public async Task EnsureUser_NewSubjectWithAnEmailAnotherAccountHolds_ThrowsConflictException()
    {
        // Arrange — a real row, provisioned the ordinary way, already holds this email under a
        // different google subject.
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedBudgetAsync("google-1", "shared@example.com");
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
        EnsureUserHandler handler = CreateHandler(db);

        // Act — the insert is refused, and the re-read by "google-2" comes back empty. No row to
        // adopt means this was not a race, which leaves only one honest reading: someone else has
        // the address. The handler decides that, because it is the layer holding the re-read.
        ConflictException exception = await ThrowsConflictExceptionAsync(() =>
            handler.HandleAsync(new EnsureUserCommand("google-2", "shared@example.com", "Second")));

        // Assert — the rejected sign-in provisioned nothing: no second user, and no budget for one.
        await Assert.That(exception.Message).IsNotEmpty();
        await using BudgetoidDbContext verify = CreateDb(host.ConnectionString);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await verify.Budgets.CountAsync()).IsEqualTo(1);
    }

    /// <summary>
    /// Relocated here from <c>UserRepositoryTests</c>, which no longer expects a throw from the
    /// repository. Kept private rather than shared, because one caller does not yet justify a
    /// test-wide helper type.
    /// </summary>
    private static async Task<ConflictException> ThrowsConflictExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ConflictException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ConflictException.");
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
