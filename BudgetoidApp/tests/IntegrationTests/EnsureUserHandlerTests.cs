using Application.Users.EnsureUser;
using Domain.Common;
using Domain.Users;
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
            new EnsureUserCommand("google-1", "person@example.com"));

        await Assert.That(provisioned.UserId).IsNotEqualTo(Guid.Empty);
        await Assert.That(await db.Users.CountAsync()).IsEqualTo(1);

        // The credential is what the next sign-in resolves through, so "a user exists" is only half
        // of what provisioning owes. Asserted field by field because a row of the right shape
        // pointing at the wrong user, or carrying the wrong provider, would still count as one.
        Credential credential = await db.Credentials.SingleAsync();
        await Assert.That(credential.UserId).IsEqualTo(provisioned.UserId);
        await Assert.That(credential.Type).IsEqualTo(CredentialType.Federated);
        await Assert.That(credential.Provider).IsEqualTo(Credential.GoogleProvider);
        await Assert.That(credential.Subject).IsEqualTo("google-1");
    }

    [Test]
    public async Task EnsureUser_ExistingSubject_ReturnsSameIdNoDuplicateAndKeepsTheRegisteredEmail()
    {
        await using RepositoryTestHost host = await StartHostAsync();
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
        EnsureUserHandler handler = CreateHandler(db);
        ProvisionedUser original = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "old@example.com"));

        ProvisionedUser second = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "new@example.com"));

        await Assert.That(second.UserId).IsEqualTo(original.UserId);
        await Assert.That(second.BudgetId).IsEqualTo(original.BudgetId);
        await Assert.That(await db.Users.CountAsync()).IsEqualTo(1);

        // The second sign-in carried a different email, and the stored row ignored it: the provider
        // gates registration and is never consulted again, so changing an email is an exchange the
        // user initiates rather than something a later token silently applies.
        await Assert.That((await db.Users.SingleAsync()).Email.Value).IsEqualTo("old@example.com");

        // A repeat sign-in must not mint a second credential for an identity that already resolves.
        await Assert.That(await db.Credentials.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureUser_ConcurrentSameSubject_NoDuplicateRow()
    {
        await using RepositoryTestHost host = await StartHostAsync();

        ProvisionedUser[] provisioned = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
            EnsureUserHandler handler = CreateHandler(db);
            return await handler.HandleAsync(new EnsureUserCommand("google-1", "person@example.com"));
        }));

        // The budgets assertions are not confirming a schema invariant — there is deliberately no
        // one-budget-per-user constraint — they ARE the guard for FR-001 under concurrency.
        await using BudgetoidDbContext assertionDb = CreateDb(host.ConnectionString);
        await Assert.That(provisioned.Select(result => result.UserId).Distinct().Count()).IsEqualTo(1);
        await Assert.That(provisioned.Select(result => result.BudgetId).Distinct().Count()).IsEqualTo(1);
        await Assert.That(await assertionDb.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await assertionDb.Budgets.CountAsync()).IsEqualTo(1);

        // Seven of the eight racers lost their insert and adopted the winner's row. Each loser
        // carried its own credential into that save, so a second row here would mean one of them
        // half-landed.
        await Assert.That(await assertionDb.Credentials.CountAsync()).IsEqualTo(1);
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
            handler.HandleAsync(new EnsureUserCommand("google-2", "shared@example.com")));

        // Assert — the rejected sign-in provisioned nothing: no second user, and no budget for one.
        await Assert.That(exception.Message).IsNotEmpty();
        await using BudgetoidDbContext verify = CreateDb(host.ConnectionString);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await verify.Budgets.CountAsync()).IsEqualTo(1);

        // And no credential either. "google-2" landing on its own would make the next attempt from
        // that account resolve to the first user's row — the email conflict would silently become an
        // account takeover.
        await Assert.That(await verify.Credentials.CountAsync()).IsEqualTo(1);
        await Assert.That((await verify.Credentials.SingleAsync()).Subject).IsEqualTo("google-1");
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

    private static EnsureUserHandler CreateHandler(BudgetoidDbContext db)
    {
        // Built once and shared with the resolve half, because in the composition root they are the
        // same request-scoped writer and the same clock. Handing the two halves separate instances
        // would let a handler that published the identity only on its own writer still pass.
        UserRepository users = new(db);
        BudgetRepository budgets = new(db);
        UnpolicedUserContextWriter writer = new();

        return new EnsureUserHandler(
            users,
            budgets,
            writer,
            TimeProvider.System,
            new ResolveUserHandler(users, budgets, writer, TimeProvider.System));
    }

    /// <summary>
    /// Discards the published identity, because on this fixture nothing reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a silenced dependency. Every context in this file is built on
    /// <see cref="RepositoryTestHost.ConnectionString"/>, which is the container <b>superuser</b>,
    /// and PostgreSQL skips row-level security entirely for a superuser. The session setting the
    /// handler publishes is what the <c>user_isolation</c> policies read, so on this connection it
    /// is inert: publishing the right id, the wrong id or no id at all produces byte-identical
    /// results here. These tests are about what provisioning <em>writes</em> — one user, one budget,
    /// one credential, and nothing at all on the conflict path — not about which rows a policed
    /// session may then see.
    /// </para>
    /// <para>
    /// The publication itself is covered where it can actually be observed: the unit tests assert
    /// the sequence of published ids against a recording writer, and the app-role fixtures
    /// (<see cref="RepositoryTestHost.OpenAppConnectionForUserAsync"/>) exercise the policies on a
    /// connection that is subject to them. A test needing either belongs there, not here — pointing
    /// this helper at a real writer would not make these tests measure the policies.
    /// </para>
    /// <para>
    /// Nested and private rather than shared: <c>UnitTests.Fakes.RecordingUserContextWriter</c> lives
    /// in a project this one does not reference, and one call site does not justify adding that
    /// reference.
    /// </para>
    /// </remarks>
    private sealed class UnpolicedUserContextWriter : IUserContextWriter
    {
        public void ResolveUser(Guid userId)
        {
            // Intentionally empty — see the type's remarks.
        }

        public void ResolveBudget(Guid budgetId)
        {
            // Intentionally empty, and never reached: no handler publishes a budget, the middleware
            // does. Here to satisfy the interface — see the type's remarks.
        }
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
