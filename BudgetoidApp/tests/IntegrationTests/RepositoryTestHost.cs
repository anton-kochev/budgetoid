using Domain.Budgets;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

public sealed class RepositoryTestHost : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("budgetoid")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    /// <summary>
    /// Password the grants script assigns to the application role inside this test container. A
    /// constant is fine: the container lives for one test and is unreachable from outside it.
    /// </summary>
    private const string AppRolePassword = "app-test-password";

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// Connects as the least-privilege application role instead of the container account. This
    /// property exists because <see cref="ConnectionString" /> cannot measure privileges at all:
    /// the host's own connection is the container superuser, and PostgreSQL skips every privilege
    /// check for a superuser — a permissions test run on the admin connection passes no matter
    /// what the grants say, including with no grants script at all. Only a statement sent through
    /// this connection string observes the role's real write surface.
    /// </summary>
    public string AppConnectionString => new NpgsqlConnectionStringBuilder(ConnectionString)
    {
        Username = DatabaseProvisioning.AppRoleName,
        Password = AppRolePassword,
    }.ConnectionString;

    /// <summary>
    /// Opens a connection as the least-privilege application role <b>with the ambient budget
    /// already on the session</b>, so that "an app-role connection" and "an app-role connection
    /// carrying its ambient budget" are the same thing rather than two states a caller can get
    /// wrong. Callers own the returned connection and dispose it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row-level security on the budget-owned tables keys its policies to
    /// <c>app.current_budget_id</c>. A raw connection that sets nothing sees none of those rows, and
    /// the damage is silent rather than loud: an UPDATE that should affect one row affects zero and
    /// reports success, so an assertion on the affected count fails while an assertion on a refusal
    /// passes for entirely the wrong reason. <paramref name="budgetId" /> must therefore be the
    /// budget the statements on this connection target rows in, not just any real budget.
    /// </para>
    /// <para>
    /// <c>set_config(..., false)</c> — not <c>SET LOCAL</c>. These tests send statements in
    /// autocommit, and <c>SET LOCAL</c> outside a transaction sets nothing and warns. The value is
    /// passed as text because <c>set_config</c> takes text: bind the <see cref="Guid" /> itself and
    /// Npgsql infers <c>uuid</c>, which no <c>set_config</c> overload accepts. Setting a custom GUC
    /// — one with a dotted namespace — needs no privilege and no policy, so this is inert until the
    /// policies exist.
    /// </para>
    /// </remarks>
    public async Task<NpgsqlConnection> OpenAppConnectionAsync(Guid budgetId)
    {
        NpgsqlConnection connection = new(AppConnectionString);

        try
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(
                "select set_config('app.current_budget_id', @budget, false)",
                connection);
            command.Parameters.AddWithValue("budget", budgetId.ToString());
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // The caller never receives the connection on this path, so nothing else can close it.
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    public async Task StartAsync()
    {
        await _container.StartAsync();
        await using var db = new BudgetoidDbContext(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(ConnectionString)
                .Options);
        await db.Database.MigrateAsync();

        // Two calls, because provisioning no longer decides how the role authenticates. ApplyGrantsAsync
        // creates the role credential-free and gives it its write surface and its isolation policies;
        // attaching a credential is a separate step, and production attaches an Entra identity instead.
        // Password auth is the local and test path, so the tests take the other branch here — which is
        // also why the branch has to be a separate call rather than a parameter.
        await DatabaseProvisioning.ApplyGrantsAsync(ConnectionString);
        await DatabaseProvisioning.AttachAppRolePasswordAsync(ConnectionString, AppRolePassword);
    }

    /// <summary>
    /// Persists a user together with its default budget and returns the <b>budget</b> id, so tests
    /// can satisfy the budgets foreign key on every owned entity with a real tenant row.
    /// </summary>
    /// <remarks>
    /// The seeding context is built without an <c>IBudgetContext</c>, which is only safe because
    /// <c>Budget</c> deliberately carries no global query filter — its owner scoping is explicit at
    /// every call site instead.
    /// </remarks>
    public async Task<Guid> SeedBudgetAsync(string googleSubject, string email)
    {
        Guid userId = await SeedUserAsync(googleSubject, email);
        await using var db = CreateSeedingDbContext();
        Budget budget = Budget.CreateDefault(userId, SeedInstant);
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();
        return budget.Id;
    }

    /// <summary>
    /// Persists a user together with the federated Google credential that resolves to it, and
    /// returns the <b>user</b> id. Tests of budget-owned entities want
    /// <see cref="SeedBudgetAsync"/>.
    /// </summary>
    /// <remarks>
    /// Both rows go in one <c>SaveChangesAsync</c>, mirroring the shape <c>UserRepository</c>
    /// inserts them in: a seeded user without its credential would be a state production can never
    /// produce, so tests written against it would be testing a schema nobody ships.
    /// <paramref name="googleSubject"/> is still the caller's handle on the identity, which is why
    /// this signature outlived the column it used to write.
    /// </remarks>
    public async Task<Guid> SeedUserAsync(string googleSubject, string email)
    {
        await using var db = CreateSeedingDbContext();
        User user = User.Create(email, SeedInstant);
        db.Users.Add(user);
        db.Credentials.Add(Credential.CreateFederated(
            user.Id, Credential.GoogleProvider, googleSubject, SeedInstant));
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// Adds another budget to an existing owner and returns its id, so a test can exercise two
    /// tenants without inventing a second user.
    /// </summary>
    public async Task<Guid> SeedAdditionalBudgetAsync(Guid userId, string name)
    {
        await using var db = CreateSeedingDbContext();
        Budget budget = Budget.Create(userId, name, SeedInstant);
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();
        return budget.Id;
    }

    /// <summary>
    /// Fixed UTC instant for all seeded rows. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing, not decoration.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private BudgetoidDbContext CreateSeedingDbContext() => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
