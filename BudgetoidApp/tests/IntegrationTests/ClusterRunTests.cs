using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins how test runs share one long-lived PostgreSQL server without touching each other's databases
/// or anything that is not a test database.
/// </summary>
/// <remarks>
/// <para>
/// A server the suite did not start — the one a Pithos session runs next to the agent, or one named by
/// <c>BUDGETOID_TEST_DATABASE_URL</c> — outlives a run. Two runs can overlap on it, and a run killed
/// part-way leaves databases behind that no later run would ever drop. Every run therefore names its
/// databases under its own prefix, holds a lease for its lifetime, and sweeps only prefixes nobody
/// leases.
/// </para>
/// </remarks>
public sealed class ClusterRunTests
{
    /// <summary>
    /// The two sweep tests never overlap. A sweep that finds another sweep already holding a dead run's
    /// lease skips that run, because the other one is dropping it; correct for the suite, but a test
    /// asserting the drop right after its own sweep would read the moment before the other finished.
    /// </summary>
    private const string SweepTests = "cluster-sweep";

    [Test]
    public async Task Create_NamesEveryDatabaseUnderItsOwnPrefix()
    {
        // Act
        ClusterRun run = ClusterRun.Create();

        // Assert
        await Assert.That(run.RunId).Matches("^[0-9a-f]{8}$");
        await Assert.That(run.TemplateDatabase).IsEqualTo($"bt_{run.RunId}_template");
        await Assert.That(run.TestDatabase(7)).IsEqualTo($"bt_{run.RunId}_t00007");
        await Assert.That(run.SandboxDatabase(7)).IsEqualTo($"bt_{run.RunId}_d00007");
    }

    [Test]
    public async Task Create_NamesEveryRoleUnderItsOwnPrefix()
    {
        // Act
        ClusterRun run = ClusterRun.Create();

        // Assert
        await Assert.That(run.SandboxAppRole(7).Name).IsEqualTo($"bt_{run.RunId}_a00007");
        await Assert.That(run.SandboxHelperRole(7, "middle")).IsEqualTo($"bt_{run.RunId}_h00007_middle");
    }

    [Test]
    [Arguments("")]
    [Arguments("Middle")]
    [Arguments("mid dle")]
    [Arguments("x; drop role postgres")]
    public async Task SandboxHelperRole_RefusesALabelThatIsNotLowercaseLettersAndUnderscores(string label) =>
        await Assert.That(() => ClusterRun.Create().SandboxHelperRole(1, label)).Throws<ArgumentException>();

    /// <summary>
    /// The sweep can only ever reach names a run made. <c>budgetoid</c> is the database a Pithos session
    /// declares for the developer's own work, and dropping it would destroy that session's data.
    /// </summary>
    [Test]
    [Arguments("bt_deadbeef_t00001", true)]
    [Arguments("bt_deadbeef_template", true)]
    [Arguments("bt_deadbeef_d00001", true)]
    [Arguments("budgetoid", false)]
    [Arguments("postgres", false)]
    [Arguments("template1", false)]
    [Arguments("bt_dead_t00001", false)]
    [Arguments("bt_DEADBEEF_t00001", false)]
    [Arguments("xbt_deadbeef_t00001", false)]
    public async Task IsRunDatabase_MatchesOnlyRunPrefixedNames(string name, bool expected) =>
        await Assert.That(ClusterRun.IsRunDatabase(name)).IsEqualTo(expected);

    /// <summary>
    /// Roles are server-wide, so the sweep reclaims them too — and can only ever reach names a run
    /// made. <c>budgetoid_app</c> is the role every run's API connects as and must survive every sweep.
    /// </summary>
    [Test]
    [Arguments("bt_deadbeef_a00001", true)]
    [Arguments("bt_deadbeef_h00001_middle", true)]
    [Arguments("budgetoid_app", false)]
    [Arguments("postgres", false)]
    [Arguments("bt_deadbeef_t00001", false)]
    [Arguments("bt_deadbeef_h00001_", false)]
    [Arguments("bt_DEADBEEF_a00001", false)]
    [Arguments("xbt_deadbeef_a00001", false)]
    public async Task IsRunRole_MatchesOnlyRunPrefixedNames(string name, bool expected) =>
        await Assert.That(ClusterRun.IsRunRole(name)).IsEqualTo(expected);

    [Test]
    [NotInParallel(SweepTests)]
    public async Task SweepAsync_DropsTheRolesOfARunNobodyLeases_EvenOnesHoldingAServerWidePrivilege()
    {
        // Arrange — a dead run's role that still holds something outside any database it made, which
        // a bare DROP ROLE refuses with 2BP01. A default privilege in the postgres database rather than
        // a tablespace grant: its catalog row is this role's alone, where pg_default's ACL is one row
        // every test granting on it would be writing at once. To PUBLIC because a role's default
        // privileges to itself are the built-in ones, and PostgreSQL stores no row for those.
        string maintenance = await SharedPostgresCluster.MaintenanceConnectionStringAsync();
        ClusterRun dead = ClusterRun.Create();
        string role = dead.SandboxAppRole(1).Name;
        await ExecuteAsync(maintenance, $"create role {role}");
        await ExecuteAsync(maintenance, $"alter default privileges for role {role} grant select on tables to public");

        // Act
        await ClusterRun.SweepAsync(maintenance);

        // Assert
        await Assert.That(await RoleExistsAsync(maintenance, role)).IsFalse();
    }

    [Test]
    [NotInParallel(SweepTests)]
    public async Task SweepAsync_DropsTheDatabasesOfARunNobodyLeases()
    {
        // Arrange — a run that created a database and then died: nothing holds its lease.
        string maintenance = await SharedPostgresCluster.MaintenanceConnectionStringAsync();
        ClusterRun dead = ClusterRun.Create();
        await ExecuteAsync(maintenance, $"create database {dead.TestDatabase(1)}");

        // Act
        await ClusterRun.SweepAsync(maintenance);

        // Assert
        await Assert.That(await DatabaseExistsAsync(maintenance, dead.TestDatabase(1))).IsFalse();
    }

    [Test]
    [NotInParallel(SweepTests)]
    public async Task SweepAsync_LeavesALeasedRunAloneUntilItsLeaseEnds()
    {
        // Arrange — a live run in another process, as far as the server can tell.
        string maintenance = await SharedPostgresCluster.MaintenanceConnectionStringAsync();
        ClusterRun live = ClusterRun.Create();
        await using (await live.AcquireLeaseAsync(maintenance))
        {
            await ExecuteAsync(maintenance, $"create database {live.TestDatabase(1)}");

            // Act
            await ClusterRun.SweepAsync(maintenance);

            // Assert
            await Assert.That(await DatabaseExistsAsync(maintenance, live.TestDatabase(1))).IsTrue();
        }

        // The lease ended with its connection; the next sweep may reclaim the database.
        await ClusterRun.SweepAsync(maintenance);
        await Assert.That(await DatabaseExistsAsync(maintenance, live.TestDatabase(1))).IsFalse();
    }

    [Test]
    public async Task CreateDatabaseAsync_NamesTheDatabaseUnderThisRunsPrefix()
    {
        // Act
        string connectionString = await SharedPostgresCluster.CreateDatabaseAsync();

        try
        {
            // Assert
            string database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
            await Assert.That(ClusterRun.IsRunDatabase(database)).IsTrue();
        }
        finally
        {
            await SharedPostgresCluster.DropDatabaseAsync(connectionString);
        }
    }

    /// <summary>
    /// The role gate is server-wide, not just process-wide: while one run's API boot writes the
    /// application role, no other process can take the same lock.
    /// </summary>
    /// <remarks>
    /// The grants script's first statement writes the role's <c>pg_authid</c> row, which every database
    /// on the server shares. Two concurrent writers of one row fail with <c>XX000 tuple concurrently
    /// updated</c> instead of waiting, and a second run on the same server is exactly a second writer
    /// that the in-process semaphore cannot see.
    /// </remarks>
    [Test]
    public async Task UnderRoleGate_HoldsTheServerWideRoleLockForTheWholeAction()
    {
        // Arrange
        string maintenance = await SharedPostgresCluster.MaintenanceConnectionStringAsync();

        // Act
        bool takenElsewhereDuring = SharedPostgresCluster.UnderRoleGate(() => TryRoleLock(maintenance));

        // Assert — only "during" is asserted. Whether the lock is free afterwards depends on every
        // other test booting an API at that moment, and a gate that never released it would hang the
        // suite rather than fail one test.
        await Assert.That(takenElsewhereDuring).IsFalse();
    }

    /// <summary>Tries the role lock on a fresh connection, as another process would, and lets it go.</summary>
    private static bool TryRoleLock(string maintenance)
    {
        using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(maintenance) { Pooling = false }.ConnectionString);
        connection.Open();
        using NpgsqlCommand command = new("select pg_try_advisory_lock($1, $2)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = ClusterRun.RoleLockClass });
        command.Parameters.Add(new NpgsqlParameter { Value = ClusterRun.RoleLockKey });
        return (bool)command.ExecuteScalar()!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
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
