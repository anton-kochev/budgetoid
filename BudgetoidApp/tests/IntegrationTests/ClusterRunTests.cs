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
    }

    /// <summary>
    /// The sweep can only ever reach names a run made. <c>budgetoid</c> is the database a Pithos session
    /// declares for the developer's own work, and dropping it would destroy that session's data.
    /// </summary>
    [Test]
    [Arguments("bt_deadbeef_t00001", true)]
    [Arguments("bt_deadbeef_template", true)]
    [Arguments("budgetoid", false)]
    [Arguments("postgres", false)]
    [Arguments("template1", false)]
    [Arguments("bt_dead_t00001", false)]
    [Arguments("bt_DEADBEEF_t00001", false)]
    [Arguments("xbt_deadbeef_t00001", false)]
    public async Task IsRunDatabase_MatchesOnlyRunPrefixedNames(string name, bool expected) =>
        await Assert.That(ClusterRun.IsRunDatabase(name)).IsEqualTo(expected);

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

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
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
