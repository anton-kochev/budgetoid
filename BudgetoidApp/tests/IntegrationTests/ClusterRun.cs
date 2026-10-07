using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// One test run's footprint on a PostgreSQL server: the prefix every database it creates carries, the
/// lease that says it is still alive, and the sweep that reclaims runs that are not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a prefix.</b> The suite used to own its server outright — a container born and killed with the
/// run — so fixed names like <c>budgetoid_template</c> were safe. A server the suite merely connects to
/// outlives runs and can serve two at once, so every name is <c>bt_&lt;run&gt;_…</c> with an eight-hex
/// run id, and nothing outside that shape is ever created or dropped. <c>budgetoid</c> in particular —
/// the database a Pithos session declares for the developer's own work — cannot match.
/// </para>
/// <para>
/// <b>Why a lease rather than a timestamp.</b> A session advisory lock lives exactly as long as the
/// connection holding it, so a crashed or killed run releases it the moment its socket closes, and a
/// live run holds it however long it takes. Age-based cleanup would have to guess how long a slow run
/// may legitimately last, and the guess is wrong in one direction or the other.
/// </para>
/// <para>
/// Every lock is taken in the <c>postgres</c> database: advisory locks are scoped to the database they
/// are taken in, so a lease and the sweep that tests it must agree on one.
/// </para>
/// </remarks>
internal sealed partial class ClusterRun
{
    /// <summary>The first key of every lease: "bt" in ASCII, keeping these locks apart from any other user's.</summary>
    private const int LeaseClass = 0x6274;

    /// <summary>
    /// The lock every writer of the application role's <c>pg_authid</c> row takes, server-wide: "br" in
    /// ASCII, a class of its own so no run's lease key can ever equal it.
    /// </summary>
    public const int RoleLockClass = 0x6272;

    public const int RoleLockKey = 0;

    private ClusterRun(string runId) => RunId = runId;

    public string RunId { get; }

    public string TemplateDatabase => $"bt_{RunId}_template";

    public static ClusterRun Create() => new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));

    public static bool IsRunDatabase(string name) => RunDatabase().IsMatch(name);

    public string TestDatabase(int number) => $"bt_{RunId}_t{number:d5}";

    /// <summary>
    /// Takes this run's lease on a dedicated connection and returns it; the lease ends when it is
    /// disposed or when the process ends.
    /// </summary>
    public async Task<NpgsqlConnection> AcquireLeaseAsync(string maintenanceConnectionString)
    {
        NpgsqlConnection connection = new(Unpooled(maintenanceConnectionString));
        try
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("select pg_advisory_lock($1, $2)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = LeaseClass });
            command.Parameters.Add(new NpgsqlParameter { Value = LeaseKey(RunId) });
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Drops every database whose run holds no lease. A run is tested by trying its lease: success
    /// means nobody holds it, and the sweep keeps it while it drops that run's databases, so the run
    /// cannot come back to life underneath.
    /// </summary>
    public static async Task SweepAsync(string maintenanceConnectionString)
    {
        await using NpgsqlConnection connection = new(Unpooled(maintenanceConnectionString));
        await connection.OpenAsync();

        List<string> databases = [];
        await using (NpgsqlCommand list = new("select datname from pg_database", connection))
        await using (NpgsqlDataReader reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                if (IsRunDatabase(name))
                {
                    databases.Add(name);
                }
            }
        }

        foreach (IGrouping<string, string> run in databases.GroupBy(name => name.Substring(3, 8)))
        {
            if (!await TryLockAsync(connection, "pg_try_advisory_lock", run.Key))
            {
                continue;
            }

            try
            {
                foreach (string database in run)
                {
                    // The name matched RunDatabase(), so it needs no quoting and carries no input.
                    await using NpgsqlCommand drop = new($"drop database if exists {database} with (force)", connection);
                    await drop.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await TryLockAsync(connection, "pg_advisory_unlock", run.Key);
            }
        }
    }

    /// <summary>
    /// Takes the server-wide role lock on a dedicated connection and returns it; disposing the
    /// connection releases the lock. Blocks until any other holder lets go.
    /// </summary>
    /// <remarks>
    /// Synchronous because its one caller that matters, <c>ApiFactory.CreateHost</c>, is a synchronous
    /// override; <see cref="HoldRoleLockAsync" /> is the same lock for async callers.
    /// </remarks>
    public static NpgsqlConnection HoldRoleLock(string maintenanceConnectionString)
    {
        NpgsqlConnection connection = new(Unpooled(maintenanceConnectionString));
        try
        {
            connection.Open();
            using NpgsqlCommand command = RoleLockCommand(connection);
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="HoldRoleLock" />
    public static async Task<NpgsqlConnection> HoldRoleLockAsync(string maintenanceConnectionString)
    {
        NpgsqlConnection connection = new(Unpooled(maintenanceConnectionString));
        try
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = RoleLockCommand(connection);
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static NpgsqlCommand RoleLockCommand(NpgsqlConnection connection)
    {
        NpgsqlCommand command = new("select pg_advisory_lock($1, $2)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = RoleLockClass });
        command.Parameters.Add(new NpgsqlParameter { Value = RoleLockKey });
        return command;
    }

    private static async Task<bool> TryLockAsync(NpgsqlConnection connection, string function, string runId)
    {
        await using NpgsqlCommand command = new($"select {function}($1, $2)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = LeaseClass });
        command.Parameters.Add(new NpgsqlParameter { Value = LeaseKey(runId) });
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The run id is 32 random bits, so it is the lock key itself and two runs never share one.</summary>
    private static int LeaseKey(string runId) => unchecked((int)Convert.ToUInt32(runId, 16));

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    [GeneratedRegex("^bt_[0-9a-f]{8}_(template|t[0-9]{5})$")]
    private static partial Regex RunDatabase();
}
