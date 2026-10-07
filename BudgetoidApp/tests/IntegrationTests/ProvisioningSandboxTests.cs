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
