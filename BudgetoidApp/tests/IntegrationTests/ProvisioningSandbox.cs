using Infrastructure.Persistence.Provisioning;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// What a deployment test provisions into: an empty database and an application role of its own, on
/// the shared server, dropped again on dispose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not a container per test.</b> These tests used to start a fresh PostgreSQL container each,
/// because provisioning writes <c>budgetoid_app</c>, and a role is server-wide: on one shared server,
/// a test that renames or over-grants the role would break every other test reading it. A sandbox
/// gets the same isolation from names instead. Its database is <c>bt_&lt;run&gt;_d#####</c>, and its
/// role, <c>bt_&lt;run&gt;_a#####</c>, is passed to the internal provisioning overloads in place of
/// <c>budgetoid_app</c>, so a test can sabotage it as freely as it once sabotaged a role nobody else
/// could see. That leaves the suite needing nothing but a connection string, which is what lets it
/// run where Docker is unavailable.
/// </para>
/// <para>
/// <b>Why from <c>template0</c>.</b> A deployment test asks what provisioning does to a new database,
/// so it starts from an empty one and runs <c>ProvisionAsync</c> itself — the migrated, provisioned
/// run template would answer a different question.
/// </para>
/// <para>
/// <b>Cleanup.</b> The database goes first, taking every grant inside it; then each role the sandbox
/// named, through <see cref="ClusterRun.DropRoleAsync" />, which clears what a role holds on
/// server-wide objects before dropping it. Failures are swallowed, for the reason
/// <see cref="SharedPostgresCluster.DropDatabaseAsync" /> gives: a cleanup error must not replace a
/// test's real one. Whatever survives carries the run's prefix, and the next run's sweep reclaims it.
/// </para>
/// </remarks>
internal sealed class ProvisioningSandbox : IAsyncDisposable
{
    private static int _counter;

    private readonly int _number;
    private readonly string _cluster;
    private readonly List<string> _helperRoles = [];

    private ProvisioningSandbox(int number, string cluster)
    {
        _number = number;
        _cluster = cluster;
        Database = SharedPostgresCluster.Run.SandboxDatabase(number);
        Role = SharedPostgresCluster.Run.SandboxAppRole(number);
    }

    public string Database { get; }

    /// <summary>The role this sandbox's test provisions in place of <c>budgetoid_app</c>.</summary>
    public AppRole Role { get; }

    /// <summary>
    /// A superuser connection string to <see cref="Database" />, unpooled so every open is a real login
    /// and no idle session outlives the test on a database it is about to drop.
    /// </summary>
    public string AdminConnectionString => Unpooled(Database);

    /// <summary>The same superuser on the server's <c>postgres</c> database.</summary>
    public string MaintenanceConnectionString => Unpooled("postgres");

    public static async Task<ProvisioningSandbox> CreateAsync()
    {
        string maintenance = await SharedPostgresCluster.MaintenanceConnectionStringAsync();
        ProvisioningSandbox sandbox = new(Interlocked.Increment(ref _counter), maintenance);

        await using NpgsqlConnection connection = new(sandbox.MaintenanceConnectionString);
        await connection.OpenAsync();

        // The name came from ClusterRun, so it needs no quoting and carries no input.
        await using NpgsqlCommand create = new($"create database {sandbox.Database} template template0", connection);
        await create.ExecuteNonQueryAsync();
        return sandbox;
    }

    /// <summary>
    /// A role name for anything else the test creates — a bystander, a middle grantor — unique to this
    /// sandbox and dropped with it. The test creates the role itself.
    /// </summary>
    public string HelperRole(string label)
    {
        string role = SharedPostgresCluster.Run.SandboxHelperRole(_number, label);
        lock (_helperRoles)
        {
            if (!_helperRoles.Contains(role))
            {
                _helperRoles.Add(role);
            }
        }

        return role;
    }

    public async Task<NpgsqlConnection> OpenAdminAsync()
    {
        NpgsqlConnection connection = new(AdminConnectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using NpgsqlConnection connection = new(MaintenanceConnectionString);
            await connection.OpenAsync();
            await using (NpgsqlCommand drop = new($"drop database if exists {Database} with (force)", connection))
            {
                await drop.ExecuteNonQueryAsync();
            }

            string[] helpers;
            lock (_helperRoles)
            {
                helpers = [.. _helperRoles];
            }

            foreach (string role in helpers.Prepend(Role.Name))
            {
                await ClusterRun.DropRoleAsync(connection, role);
            }
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // See the remarks: a cleanup failure must not replace the test's own.
        }
    }

    private string Unpooled(string database) =>
        new NpgsqlConnectionStringBuilder(_cluster) { Database = database, Pooling = false }.ConnectionString;
}
