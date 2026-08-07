using Application.Users.EnsureUser;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests;

/// <summary>
/// The resolve-only half of provisioning: it answers who is asking, and it is the only half a request
/// on a route that may not mint an account is allowed to run.
/// </summary>
/// <remarks>
/// <para>
/// <c>ResolveUserCommand</c> carries no email, and that absence is the design rather than an omission.
/// This path never writes a <c>users</c> row, so an address here would be a value carried through the
/// whole request for nothing to read — and the moment it exists, the next change to this handler can
/// quietly start writing it.
/// </para>
/// <para>
/// Written against the real repositories over a real database, in the style of
/// <c>EnsureUserHandlerTests</c> beside it: the two claims here are "one budget row appeared" and "no
/// row appeared anywhere", and both are claims about what a table holds.
/// </para>
/// </remarks>
public sealed class ResolveUserHandlerTests
{
    /// <summary>
    /// The one write the resolve path keeps.
    /// </summary>
    /// <remarks>
    /// A provisioning that inserted the user and its credential and then lost the budget insert leaves
    /// an account every budget-scoped query comes back empty for — a signed-in person staring at an
    /// application that has forgotten their money. The heal has to survive the split, and the resolve
    /// path is where it lands, because it is the path every returning request now takes.
    /// </remarks>
    [Test]
    public async Task ResolveUser_ForAnAccountWhoseBudgetInsertWasLost_HealsIt()
    {
        // Arrange — a user and the credential that resolves to it, and deliberately no budget: exactly
        // the state a half-landed provisioning leaves behind.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
        ResolveUserHandler handler = CreateHandler(db);
        await Assert.That(await db.Budgets.CountAsync()).IsEqualTo(0);

        // Act
        ProvisionedUser? resolved = await handler.HandleAsync(new ResolveUserCommand("google-1"));

        // Assert
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.UserId).IsEqualTo(userId);

        await using BudgetoidDbContext verify = CreateDb(host.ConnectionString);
        await Assert.That(await verify.Budgets.CountAsync()).IsEqualTo(1);

        // The budget it reported is the budget it wrote, not merely some budget: a handler that healed
        // the row and answered with a different id would leave every later statement in the request
        // scoped to a tenant that does not exist.
        await Assert.That((await verify.Budgets.SingleAsync()).Id).IsEqualTo(resolved.BudgetId);
        await Assert.That((await verify.Budgets.SingleAsync()).UserId).IsEqualTo(userId);

        // And the heal is a budget insert and nothing else — no second user, no second credential.
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await verify.Credentials.CountAsync()).IsEqualTo(1);
    }

    /// <summary>
    /// The provable-fail control for the heal: a subject nothing resolves gets no row of any kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion that catches a heal placed <b>before</b> the credential check — the
    /// obvious refactor when the budget logic is lifted out of one handler into another. Such a
    /// handler passes the heal test above perfectly and writes a budget for every unknown subject that
    /// knocks, which is the resurrection this whole change exists to stop.
    /// </para>
    /// <para>
    /// The three counts are compared to what they were rather than to zero, on a database that already
    /// holds an account. Against an empty one, "no row was written" and "the seeding never worked" are
    /// the same observation.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResolveUser_ForASubjectWithNoCredential_WritesNothing()
    {
        // Arrange — one real, complete account, so every count below starts non-zero.
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedOwnerAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
        ResolveUserHandler handler = CreateHandler(db);
        (int usersBefore, int credentialsBefore, int budgetsBefore) = (
            await db.Users.CountAsync(),
            await db.Credentials.CountAsync(),
            await db.Budgets.CountAsync());
        await Assert.That(usersBefore).IsEqualTo(1);
        await Assert.That(credentialsBefore).IsEqualTo(1);
        await Assert.That(budgetsBefore).IsEqualTo(1);

        // Act — a subject the product has never seen, which is what a stale token names after an
        // erasure.
        ProvisionedUser? resolved = await handler.HandleAsync(new ResolveUserCommand("google-unknown"));

        // Assert — nothing to report, and nothing written to report it about.
        await Assert.That(resolved).IsNull();

        await using BudgetoidDbContext verify = CreateDb(host.ConnectionString);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(usersBefore);
        await Assert.That(await verify.Credentials.CountAsync()).IsEqualTo(credentialsBefore);
        await Assert.That(await verify.Budgets.CountAsync()).IsEqualTo(budgetsBefore);
    }

    private static ResolveUserHandler CreateHandler(BudgetoidDbContext db) => new(
        new UserRepository(db),
        new BudgetRepository(db),
        new UnpolicedUserContextWriter(),
        TimeProvider.System);

    /// <summary>
    /// Discards the published identity, because on this fixture nothing reads it.
    /// </summary>
    /// <remarks>
    /// The same reasoning as <c>EnsureUserHandlerTests</c>' own writer, and for the same reason it is
    /// duplicated rather than shared: every context here is built on
    /// <see cref="RepositoryTestHost.ConnectionString" />, the container superuser, and PostgreSQL skips
    /// row-level security entirely for a superuser. The session setting the handler publishes is what
    /// the <c>user_isolation</c> policies read, so on this connection publishing the right id, the wrong
    /// id or none at all produces byte-identical results. What is measured here is what resolving
    /// <em>writes</em>, not which rows a policed session may then see.
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
