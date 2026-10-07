using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Where the suite's PostgreSQL server comes from: a connection string named by the environment, or
/// a container the suite starts itself when none is named.
/// </summary>
/// <param name="Variable">The environment variable that supplied the server; null for a container.</param>
/// <param name="ConnectionString">A key=value Npgsql connection string; null for a container.</param>
/// <remarks>
/// <para>
/// <b>Precedence.</b> <c>BUDGETOID_TEST_DATABASE_URL</c> first, because it is the project's own and
/// someone set it on purpose; then <c>PITHOS_POSTGRES_URL</c>, the database a Pithos agent session runs
/// next to the agent; then a container. A blank value is unset.
/// </para>
/// <para>
/// <b>Both URI and key=value forms are accepted.</b> Pithos hands over a <c>postgresql://</c> URI, which
/// Npgsql does not parse; key=value passes through as written. The URI is translated here rather than
/// guessed at, and anything this translation does not understand stops the run instead of being
/// dropped: a silently ignored setting is a server reached on weaker terms than were asked for.
/// </para>
/// </remarks>
internal sealed record TestDatabaseSource(string? Variable, string? ConnectionString)
{
    /// <summary>The variables consulted, in order.</summary>
    public static readonly IReadOnlyList<string> Variables = ["BUDGETOID_TEST_DATABASE_URL", "PITHOS_POSTGRES_URL"];

    public bool StartsContainer => ConnectionString is null;

    public static TestDatabaseSource Resolve(Func<string, string?> environment)
    {
        foreach (string variable in Variables)
        {
            string? value = environment(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new TestDatabaseSource(variable, ToConnectionString(variable, value.Trim()));
            }
        }

        return new TestDatabaseSource(null, null);
    }

    public static TestDatabaseSource FromProcess() => Resolve(Environment.GetEnvironmentVariable);

    private static string ToConnectionString(string variable, string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            return new NpgsqlConnectionStringBuilder(value).ConnectionString;
        }

        // Messages name the variable and the part that is wrong, never the value: it carries a password.
        ArgumentException Malformed(string what) =>
            new($"{variable} is not a usable PostgreSQL URI: {what}.", nameof(value));

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw Malformed("it does not parse");
        }

        if (uri.Scheme is not ("postgres" or "postgresql"))
        {
            throw Malformed("the scheme must be postgres or postgresql");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw Malformed("it names no host");
        }

        string[] credentials = uri.UserInfo.Split(':', 2);
        NpgsqlConnectionStringBuilder builder = new()
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(credentials[0]),
        };
        if (credentials.Length == 2)
        {
            builder.Password = Uri.UnescapeDataString(credentials[1]);
        }

        string database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (database.Length > 0)
        {
            builder.Database = database;
        }

        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0]);
            string setting = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
            if (key != "sslmode")
            {
                throw Malformed($"the query parameter `{key}` is not supported");
            }

            builder.SslMode = setting switch
            {
                "disable" => SslMode.Disable,
                "allow" => SslMode.Allow,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => throw Malformed("sslmode must be one of disable, allow, prefer, require, verify-ca, verify-full"),
            };
        }

        return builder.ConnectionString;
    }
}
