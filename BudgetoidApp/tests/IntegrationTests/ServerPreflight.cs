using Npgsql;

namespace IntegrationTests;

/// <summary>What the suite reads from a PostgreSQL server before deciding it can run there.</summary>
internal sealed record ServerProbe(int Major, bool IsSuperuser, bool HasPostgresDatabase, int MaxConnections)
{
    /// <summary>Reads the probe over a connection to the server's <c>postgres</c> database.</summary>
    public static async Task<ServerProbe> ReadAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            select current_setting('server_version_num')::int / 10000,
                   (select rolsuper from pg_roles where rolname = current_user),
                   exists (select from pg_database where datname = 'postgres'),
                   current_setting('max_connections')::int
            """,
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new ServerProbe(reader.GetInt32(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt32(3));
    }
}

/// <summary>
/// The checks a server must pass before any test runs, as a pure function of a <see cref="ServerProbe" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every requirement is something the suite actually does.</b> The major is production's, because
/// SQLSTATEs and catalog shapes move between majors (18 reports a RESTRICT refusal as 23001 where 17
/// said 23503). Superuser, because the suite creates databases and roles and reads <c>pg_authid</c>.
/// The <c>postgres</c> database, because every create, drop, lease and sweep goes through it. And 500
/// connections, because one cluster serves every test in the assembly at once — the figure the
/// container has always been started with.
/// </para>
/// <para>
/// <b>Every shortfall at once, and fatal.</b> Fixing one and rerunning to find the next is a slow way
/// to learn about a server, and a run that limped on would fail somewhere unrelated instead.
/// </para>
/// </remarks>
internal static class ServerPreflight
{
    public const int ProductionMajor = 18;
    public const int RequiredConnections = 500;

    public static IReadOnlyList<string> Problems(ServerProbe probe)
    {
        List<string> problems = [];
        if (probe.Major != ProductionMajor)
        {
            problems.Add($"PostgreSQL {probe.Major}, production runs {ProductionMajor}");
        }

        if (!probe.IsSuperuser)
        {
            problems.Add("the user is not a superuser");
        }

        if (!probe.HasPostgresDatabase)
        {
            problems.Add("there is no `postgres` database");
        }

        if (probe.MaxConnections < RequiredConnections)
        {
            problems.Add($"max_connections is {probe.MaxConnections}, the suite needs at least {RequiredConnections}");
        }

        return problems;
    }

    /// <param name="source">Where the server came from, for the message: a variable name or the container.</param>
    public static void Ensure(ServerProbe probe, string source)
    {
        IReadOnlyList<string> problems = Problems(probe);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"The PostgreSQL server from {source} cannot run this suite: {string.Join("; ", problems)}.");
        }
    }
}
