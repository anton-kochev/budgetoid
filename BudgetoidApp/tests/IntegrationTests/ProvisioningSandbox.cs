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
/// <b>Server-wide sabotage.</b> Almost everything a deployment test breaks lives in its own database
/// or on its own role. A PUBLIC grant on a tablespace does not: every role inherits PUBLIC, so every
/// other sandbox's reach check would report it too. Each sandbox therefore holds a lock shared for its
/// life, and a test making that kind of change uses <see cref="CreateExclusiveAsync" />, which waits
/// until no other sandbox is alive and keeps them all out until it is disposed. The test still undoes
/// its change itself: <c>DROP OWNED BY</c> clears a role's grants, not PUBLIC's. One sandbox per test:
/// a test holding two can still only wait on an exclusive one that is alive, never on one that is
/// waiting, but there is no reason to need two.
/// </para>
/// <para>
/// <b>Cleanup.</b> The database goes first, taking every grant inside it; then each role the sandbox
/// named, through <see cref="ClusterRun.DropRoleAsync" />, which clears what a role holds on
/// server-wide objects before dropping it; then the lock. Failures are swallowed, for the reason
/// <see cref="SharedPostgresCluster.DropDatabaseAsync" /> gives: a cleanup error must not replace a
/// test's real one. Whatever survives carries the run's prefix, and the next run's sweep reclaims it.
/// </para>
/// </remarks>
internal sealed class ProvisioningSandbox : IAsyncDisposable
{
    private static int _counter;

    private readonly int _number;
    private readonly string _cluster;
    private readonly NpgsqlConnection _lock;
    private readonly List<string> _helperRoles = [];

    private ProvisioningSandbox(int number, string cluster, NpgsqlConnection sandboxLock)
    {
        _number = number;
        _cluster = cluster;
        _lock = sandboxLock;
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

    /// <summary>A sandbox alongside any number of others.</summary>
    public static Task<ProvisioningSandbox> CreateAsync() => CreateAsync(ClusterRun.HoldSandboxShareAsync);

    /// <summary>
    /// A sandbox no other sandbox is alive beside, for a test whose change every sandbox's checks would
    /// read.
    /// </summary>
    public static Task<ProvisioningSandbox> CreateExclusiveAsync() =>
        CreateAsync(ClusterRun.HoldSandboxExclusiveAsync);

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
        finally
        {
            // Last, so no exclusive sandbox starts while this one's leftovers still stand.
            await _lock.DisposeAsync();
        }
    }

    private static async Task<ProvisioningSandbox> CreateAsync(Func<string, Task<NpgsqlConnection>> holdLock)
    {
        string maintenance = await SharedPostgresCluster.MaintenanceConnectionStringAsync();
        NpgsqlConnection sandboxLock = await holdLock(maintenance);
        ProvisioningSandbox sandbox = new(Interlocked.Increment(ref _counter), maintenance, sandboxLock);
        try
        {
            await using NpgsqlConnection connection = new(sandbox.MaintenanceConnectionString);
            await connection.OpenAsync();

            // The name came from ClusterRun, so it needs no quoting and carries no input.
            await using NpgsqlCommand create = new($"create database {sandbox.Database} template template0", connection);
            await create.ExecuteNonQueryAsync();
            return sandbox;
        }
        catch
        {
            await sandbox.DisposeAsync();
            throw;
        }
    }

    private string Unpooled(string database) =>
        new NpgsqlConnectionStringBuilder(_cluster) { Database = database, Pooling = false }.ConnectionString;
}
