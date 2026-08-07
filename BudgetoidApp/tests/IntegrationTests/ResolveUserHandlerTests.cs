using Application.Users.EnsureUser;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
/// <c>EnsureUserHandlerTests</c> beside it. Both claims here are the same claim from two directions —
/// this path writes nothing, whether the subject resolves to nobody or to an account whose budget row
/// has gone missing — and both are claims about what a table holds.
/// </para>
/// </remarks>
public sealed class ResolveUserHandlerTests
{
    /// <summary>
    /// The resolve path writes nothing, even for the one state that used to make it write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This replaces the heal, and asserts its opposite.</b> An account without a budget was
    /// reachable while provisioning wrote in two saves: the user and its credential landed, the budget
    /// insert was lost, and every returning request paid for a find-or-create that repaired it. All
    /// three rows now go in one save, so the state is unreachable — production holds no data that
    /// could already be in it (ASM-007) — and a resolve that met it anyway would be meeting something
    /// the design says cannot happen. The honest answer to that is to fail, not to invent a tenant.
    /// </para>
    /// <para>
    /// The budget is deleted out of band rather than left unseeded, because
    /// <c>RepositoryTestHost.SeedUserAsync</c> would produce the same rows by omission, and a test
    /// that gets its premise from a helper's omission stops describing anything the moment the helper
    /// changes. Deleting it says out loud that something outside this handler removed a row nothing in
    /// the product removes on its own.
    /// </para>
    /// <para>
    /// The second half — that <c>budgets</c> is still empty afterwards — is the part that proves the
    /// heal is gone rather than merely relocated, and it pins a race a reviewer found in the erasure
    /// path. An erasure in flight holds a share lock on the pending <c>DELETE FROM users</c>; a
    /// resolve-path <c>INSERT INTO budgets</c> blocks on that foreign key and surfaces to the caller
    /// as a 500. After this there is no resolve-path insert left to block on it.
    /// </para>
    /// <para>
    /// <see cref="InvalidOperationException" /> and not a <c>NotFoundException</c>: a 404 would tell a
    /// signed-in person their account does not exist, when what happened is that the application's own
    /// invariant broke. The loud 500 is the honest one, and it is the same exception type the removed
    /// heal already raised when its re-read came back empty.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResolveUser_ForAnAccountWhoseBudgetRowIsMissing_FailsLoudlyAndHealsNothing()
    {
        // Arrange — a complete account, then its budget removed behind the application's back.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid userId, _) = await host.SeedOwnerAsync("google-1", "person@example.com");
        await DeleteBudgetsAsync(host, userId);
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
        ResolveUserHandler handler = CreateHandler(db);
        await Assert.That(await db.Budgets.CountAsync()).IsEqualTo(0);

        // Act
        Exception? escaped = await CaptureAsync(() => handler.HandleAsync(new ResolveUserCommand("google-1")));

        // Assert — it failed, and it failed as a broken invariant rather than as a missing account.
        await Assert.That(escaped).IsTypeOf<InvalidOperationException>();

        // And it healed nothing on the way out. This is the assertion the old heal test inverted.
        await using BudgetoidDbContext verify = CreateDb(host.ConnectionString);
        await Assert.That(await verify.Budgets.CountAsync()).IsEqualTo(0);

        // The account itself is untouched — failing loudly is not licence to remove anything.
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await verify.Credentials.CountAsync()).IsEqualTo(1);
        await Assert.That((await verify.Users.SingleAsync()).Id).IsEqualTo(userId);
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

    /// <summary>
    /// Removes an owner's budgets on the container superuser, standing in for whatever left the
    /// account in a state the application itself can no longer produce.
    /// </summary>
    private static async Task DeleteBudgetsAsync(RepositoryTestHost host, Guid userId)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("delete from budgets where user_id = @user_id", connection);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" />
    /// when nothing did. Deliberately untyped: the question is <i>which</i> exception surfaces, so
    /// catching a specific one here would decide the answer in the helper.
    /// </summary>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static ResolveUserHandler CreateHandler(BudgetoidDbContext db) => new(
        new UserRepository(db),
        new BudgetRepository(db),
        new UnpolicedUserContextWriter());

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
