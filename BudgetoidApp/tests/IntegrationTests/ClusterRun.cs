using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Infrastructure.Persistence.Provisioning;
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

    /// <summary>Whether a role name is one a run made, and so one a sweep may drop.</summary>
    public static bool IsRunRole(string name) => RunRole().IsMatch(name);

    public string TestDatabase(int number) => $"bt_{RunId}_t{number:d5}";

    /// <summary>
    /// The empty database of one <see cref="ProvisioningSandbox" />: created from <c>template0</c>, not
    /// cloned from <see cref="TemplateDatabase" />, because a deployment test provisions it itself.
    /// </summary>
    public string SandboxDatabase(int number) => $"bt_{RunId}_d{number:d5}";

    /// <summary>The application role one <see cref="ProvisioningSandbox" /> provisions in place of <c>budgetoid_app</c>.</summary>
    public AppRole SandboxAppRole(int number) => AppRole.For($"bt_{RunId}_a{number:d5}");

    /// <summary>
    /// Any other role a sandbox's test creates — a bystander, a middle grantor. Labelled so a failure
    /// that names the role says which one it was.
    /// </summary>
    /// <exception cref="ArgumentException">The label is not lowercase letters and underscores.</exception>
    public string SandboxHelperRole(int number, string label)
    {
        if (!HelperLabel().IsMatch(label))
        {
            throw new ArgumentException("A helper role label is lowercase letters and underscores.", nameof(label));
        }

        return $"bt_{RunId}_h{number:d5}_{label}";
    }

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
    /// Drops every database and role whose run holds no lease. A run is tested by trying its lease:
    /// success means nobody holds it, and the sweep keeps it while it drops that run's leftovers, so
    /// the run cannot come back to life underneath.
    /// </summary>
    /// <remarks>
    /// Databases go before roles: a role's grants inside a database go with the database, and what is
    /// left — privileges on server-wide objects such as a tablespace — is what <see cref="DropRoleAsync" />
    /// clears before the role itself.
    /// </remarks>
    public static async Task SweepAsync(string maintenanceConnectionString)
    {
        await using NpgsqlConnection connection = new(Unpooled(maintenanceConnectionString));
        await connection.OpenAsync();

        List<string> databases = await ReadNamesAsync(connection, "select datname from pg_database", IsRunDatabase);
        List<string> roles = await ReadNamesAsync(connection, "select rolname from pg_roles", IsRunRole);

        foreach (string runId in databases.Concat(roles).Select(name => name.Substring(3, 8)).Distinct())
        {
            if (!await TryLockAsync(connection, "pg_try_advisory_lock", runId))
            {
                continue;
            }

            try
            {
                foreach (string database in databases.Where(name => name.Substring(3, 8) == runId))
                {
                    // The name matched RunDatabase(), so it needs no quoting and carries no input.
                    await using NpgsqlCommand drop = new($"drop database if exists {database} with (force)", connection);
                    await drop.ExecuteNonQueryAsync();
                }

                foreach (string role in roles.Where(name => name.Substring(3, 8) == runId))
                {
                    await DropRoleAsync(connection, role);
                }
            }
            finally
            {
                await TryLockAsync(connection, "pg_advisory_unlock", runId);
            }
        }
    }

    /// <summary>
    /// Drops a run's role, first revoking whatever it still holds where <paramref name="connection" />
    /// can reach: its database, and the server-wide objects every database shares.
    /// </summary>
    /// <remarks>
    /// A bare <c>DROP ROLE</c> refuses a role that holds any privilege (2BP01), and a deployment test's
    /// whole job is to leave its role holding privileges it should not. <c>DROP OWNED BY</c> clears them,
    /// but only in the current database and on shared objects — so the role's own databases must be
    /// gone first, which both callers ensure.
    /// </remarks>
    public static async Task DropRoleAsync(NpgsqlConnection connection, string role)
    {
        if (!IsRunRole(role))
        {
            throw new ArgumentException($"{role} is not a role a run made.", nameof(role));
        }

        await using NpgsqlCommand exists = new("select exists (select from pg_roles where rolname = $1)", connection);
        exists.Parameters.Add(new NpgsqlParameter { Value = role });
        if (!(bool)(await exists.ExecuteScalarAsync())!)
        {
            return;
        }

        // The name matched RunRole(), so it needs no quoting and carries no input.
        await using NpgsqlCommand drop = new($"drop owned by {role}; drop role if exists {role}", connection);
        await drop.ExecuteNonQueryAsync();
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

    private static async Task<List<string>> ReadNamesAsync(
        NpgsqlConnection connection, string sql, Func<string, bool> belongsToARun)
    {
        List<string> names = [];
        await using NpgsqlCommand list = new(sql, connection);
        await using NpgsqlDataReader reader = await list.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string name = reader.GetString(0);
            if (belongsToARun(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    [GeneratedRegex(@"\Abt_[0-9a-f]{8}_(template|t[0-9]{5}|d[0-9]{5})\z")]
    private static partial Regex RunDatabase();

    [GeneratedRegex(@"\Abt_[0-9a-f]{8}_(a[0-9]{5}|h[0-9]{5}_[a-z_]+)\z")]
    private static partial Regex RunRole();

    [GeneratedRegex(@"\A[a-z_]+\z")]
    private static partial Regex HelperLabel();
}
