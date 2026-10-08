using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins what a deployment test gets in place of a container of its own: an empty database and an
/// application role nobody else uses, on the shared server, gone again when the test ends.
/// </summary>
public sealed class ProvisioningSandboxTests
{
    [Test]
    public async Task CreateAsync_GivesAnEmptyDatabaseAsTheSuperuser()
    {
        // Act
        await using ProvisioningSandbox sandbox = await ProvisioningSandbox.CreateAsync();

        // Assert — empty is the point: ProvisionAsync is what migrates it, as it does a new server.
        await using NpgsqlConnection admin = await sandbox.OpenAdminAsync();
        await Assert.That(ClusterRun.IsRunDatabase(sandbox.Database)).IsTrue();
        await Assert.That(admin.Database).IsEqualTo(sandbox.Database);
        await Assert.That(await ScalarAsync<bool>(admin, "select rolsuper from pg_roles where rolname = current_user")).IsTrue();
        await Assert.That(await ScalarAsync<long>(admin, "select count(*) from pg_class where relnamespace = 'public'::regnamespace")).IsEqualTo(0);
    }

    [Test]
    public async Task CreateAsync_NamesARoleThatDoesNotExistYet()
    {
        // Act
        await using ProvisioningSandbox sandbox = await ProvisioningSandbox.CreateAsync();

        // Assert — the grants script creates it, as it creates budgetoid_app on a new server.
        await Assert.That(ClusterRun.IsRunRole(sandbox.Role.Name)).IsTrue();
        await Assert.That(await RoleExistsAsync(sandbox.MaintenanceConnectionString, sandbox.Role.Name)).IsFalse();
    }

    [Test]
    public async Task TwoSandboxes_ShareNeitherDatabaseNorRole()
    {
        // Act
        await using ProvisioningSandbox first = await ProvisioningSandbox.CreateAsync();
        await using ProvisioningSandbox second = await ProvisioningSandbox.CreateAsync();

        // Assert
        await Assert.That(second.Database).IsNotEqualTo(first.Database);
        await Assert.That(second.Role).IsNotEqualTo(first.Role);
        await Assert.That(second.HelperRole("middle")).IsNotEqualTo(first.HelperRole("middle"));
    }

    [Test]
    public async Task DisposeAsync_DropsTheDatabaseAndEveryRoleItNamed_EvenOnesHoldingAServerWidePrivilege()
    {
        // Arrange — the roles a deployment test leaves behind: the app role and a helper, one of them
        // holding something outside the sandbox database, and a session still open on it. The default
        // privilege is in the postgres database because its row is this role's alone; see
        // ClusterRunTests for why not a tablespace grant, and why to PUBLIC.
        ProvisioningSandbox sandbox = await ProvisioningSandbox.CreateAsync();
        string helper = sandbox.HelperRole("middle");
        await using NpgsqlConnection lingering = await sandbox.OpenAdminAsync();
        await ExecuteAsync(lingering, $"create role {sandbox.Role.Name}");
        await ExecuteAsync(lingering, $"create role {helper}");
        await using (NpgsqlConnection onPostgres = new(sandbox.MaintenanceConnectionString))
        {
            await onPostgres.OpenAsync();
            await ExecuteAsync(onPostgres, $"alter default privileges for role {helper} grant select on tables to public");
        }

        await ExecuteAsync(lingering, $"create table public.kept (id int)");
        await ExecuteAsync(lingering, $"grant select on public.kept to {sandbox.Role.Name}");

        // Act
        await sandbox.DisposeAsync();

        // Assert
        string maintenance = sandbox.MaintenanceConnectionString;
        await Assert.That(await DatabaseExistsAsync(maintenance, sandbox.Database)).IsFalse();
        await Assert.That(await RoleExistsAsync(maintenance, sandbox.Role.Name)).IsFalse();
        await Assert.That(await RoleExistsAsync(maintenance, helper)).IsFalse();
    }

    /// <summary>
    /// A test that changes something every sandbox's checks read — a PUBLIC grant on a tablespace — runs
    /// in an exclusive sandbox, which waits until no other sandbox is alive.
    /// </summary>
    [Test]
    [NotInParallel(SandboxLockTests)]
    public async Task CreateExclusiveAsync_WaitsUntilNoOtherSandboxIsAlive()
    {
        // Arrange
        ProvisioningSandbox shared = await ProvisioningSandbox.CreateAsync();

        // Act
        Task<ProvisioningSandbox> exclusive = ProvisioningSandbox.CreateExclusiveAsync();
        bool startedWhileSharedAlive = await CompletesWithinAsync(exclusive, TimeSpan.FromMilliseconds(500));
        await shared.DisposeAsync();

        // Assert — other tests' sandboxes may still hold it up after this one ends, hence the long wait.
        await Assert.That(startedWhileSharedAlive).IsFalse();
        await using ProvisioningSandbox acquired = await exclusive.WaitAsync(TimeSpan.FromMinutes(2));
    }

    [Test]
    [NotInParallel(SandboxLockTests)]
    public async Task CreateAsync_WaitsWhileAnExclusiveSandboxIsAlive()
    {
        // Arrange
        ProvisioningSandbox exclusive = await ProvisioningSandbox.CreateExclusiveAsync().WaitAsync(TimeSpan.FromMinutes(2));

        // Act
        Task<ProvisioningSandbox> shared = ProvisioningSandbox.CreateAsync();
        bool startedWhileExclusiveAlive = await CompletesWithinAsync(shared, TimeSpan.FromMilliseconds(500));
        await exclusive.DisposeAsync();

        // Assert
        await Assert.That(startedWhileExclusiveAlive).IsFalse();
        await using ProvisioningSandbox acquired = await shared.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// An exclusive sandbox that is only waiting holds nothing up. If it queued on the server, a test
    /// holding one sandbox and creating a second would wait for the exclusive one, which waits for the
    /// first — a hang the server cannot see as a deadlock.
    /// </summary>
    [Test]
    [NotInParallel(SandboxLockTests)]
    public async Task CreateAsync_IsNotHeldUpByAnExclusiveSandboxThatIsStillWaiting()
    {
        // Arrange
        ProvisioningSandbox first = await ProvisioningSandbox.CreateAsync();
        Task<ProvisioningSandbox> exclusive = ProvisioningSandbox.CreateExclusiveAsync();
        Task<ProvisioningSandbox>? second = null;
        bool secondStarted;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            // Act
            second = ProvisioningSandbox.CreateAsync();
            secondStarted = await CompletesWithinAsync(second, TimeSpan.FromSeconds(10));
        }
        finally
        {
            // Released in the order that unwinds even the hang this test is about, so a regression is
            // a red test rather than a suite that never finishes.
            await first.DisposeAsync();
            if (second is { IsCompletedSuccessfully: true })
            {
                await (await second).DisposeAsync();
            }

            await (await exclusive.WaitAsync(TimeSpan.FromMinutes(2))).DisposeAsync();
            if (second is { IsCompletedSuccessfully: false })
            {
                await (await second.WaitAsync(TimeSpan.FromMinutes(2))).DisposeAsync();
            }
        }

        // Assert
        await Assert.That(secondStarted).IsTrue();
    }

    private const string SandboxLockTests = "sandbox-lock";

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan time) =>
        await Task.WhenAny(task, Task.Delay(time)) == task;

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> RoleExistsAsync(string connectionString, string role)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select exists (select from pg_roles where rolname = $1)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = role });
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> DatabaseExistsAsync(string connectionString, string database)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select exists (select from pg_database where datname = $1)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = database });
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
