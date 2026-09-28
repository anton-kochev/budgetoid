using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins the boot-time refusal of a connection string that asks Npgsql for the server's error detail.
/// </summary>
/// <remarks>
/// <para>
/// With <c>Include Error Detail=true</c>, a unique violation carries PostgreSQL's <c>DETAIL</c> line,
/// which quotes the key that collided. On a policed table row-level security withholds it; on the tables
/// exempt from it — <c>passkey_public_keys</c> and <c>credentials</c> — it does not, so a duplicate
/// passkey handle arrives as <c>Key (webauthn_credential_id)=(\x…)</c> inside
/// <c>PostgresException.Data["Detail"]</c>, and EF's save-failure record renders it into the log.
/// Nothing sets the option today and nothing refused it either, so one pasted connection string would
/// turn a refused enrolment into a logged credential handle with the whole suite green.
/// </para>
/// <para>
/// Each host here runs on a real database, so a host that is not refused comes up for real: in
/// Development the startup block migrates and provisions over it. A refusal that fired only after that
/// block — or only on the Production branch of the connection-string build — is a host that booted.
/// </para>
/// <para>
/// Lives here rather than in UnitTests because booting the composition root needs the Api project, and
/// UnitTests referencing it would move a <c>ProjectReferenceGraphTests</c> row.
/// </para>
/// </remarks>
public sealed class ConnectionStringOptionTests
{
    /// <summary>The option's canonical spelling, which every refusal is asserted to name.</summary>
    private const string IncludeErrorDetailKeyword = "Include Error Detail";

    /// <summary>The configuration key of the elevated connection string the Development block runs on.</summary>
    private const string AdminConnectionStringKey = "ConnectionStrings:budgetoid-admin";

    [Test]
    [Arguments("Development")]
    [Arguments("Production")]
    public async Task AHostWhoseConnectionStringIncludesErrorDetail_RefusesToStart(string environment)
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();
        string connectionString = $"{host.AppConnectionString};Include Error Detail=true";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(connectionString, host.ConnectionString, environment);

        // Assert — on the sentence naming the option, not merely on something having thrown. A host that
        // fell over for an unrelated reason would satisfy a bare "it threw" and say nothing about this
        // refusal.
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheOption(failure)).IsTrue();
    }

    [Test]
    [Arguments("IncludeErrorDetail=true")]
    [Arguments("include error detail=True")]
    public async Task AHostWhoseConnectionStringSpellsIncludeErrorDetailDifferently_RefusesToStart(string option)
    {
        // Arrange — two spellings Npgsql's own builder accepts and resolves to the same option (probed
        // against the resolved Npgsql): the space-free alias, and the canonical key in another case. A
        // refusal that searched the text for one literal spelling passes the case above and misses both.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        string connectionString = $"{host.AppConnectionString};{option}";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(connectionString, host.ConnectionString, "Production");

        // Assert — the canonical spelling, whatever the configuration wrote, so the operator reading the
        // refusal can search the documentation for it.
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheOption(failure)).IsTrue();
    }

    [Test]
    [Arguments("Development")]
    [Arguments("Production")]
    public async Task AHostWhoseConnectionStringSetsIncludeErrorDetailFalse_StartsNormally(string environment)
    {
        // Arrange — the keyword present and switched off, which is the option's default spelled out. A
        // refusal that looked for the keyword rather than at the value would refuse this host.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        string connectionString = $"{host.AppConnectionString};Include Error Detail=false";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(connectionString, host.ConnectionString, environment);

        // Assert
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task ADevelopmentHostRefusedForIncludeErrorDetail_NeverMigratesTheDatabase()
    {
        // Arrange — a database nothing has migrated. Every PostgresTestHost database is cloned from a
        // migrated template, so its __EFMigrationsHistory exists before any boot and could not tell a
        // refusal ahead of the Development block from one behind it. An empty one can: the block's
        // first act is MigrateAsync on the admin connection, which creates that table.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using EmptyDatabase empty = await EmptyDatabase.CreateAsync(host.ConnectionString);
        string appConnectionString = new NpgsqlConnectionStringBuilder(host.AppConnectionString)
        {
            Database = empty.Name,
            IncludeErrorDetail = true,
        }.ConnectionString;

        // Act
        Exception? failure = await CaptureStartupFailureAsync(
            appConnectionString, empty.ConnectionString, "Development");

        // Assert — refused, and refused before the migration ran. A refusal placed after the
        // Development block still names the option; only the untouched catalog tells the two apart.
        await Assert.That(NamesTheOption(failure)).IsTrue();
        await Assert.That(await empty.HasMigrationsHistoryTableAsync()).IsFalse();
    }

    [Test]
    public async Task ARefusalForIncludeErrorDetail_NeverQuotesTheConnectionString()
    {
        // Arrange — the refusal is read by whoever reads the boot log, and the string it refuses
        // carries a password. Host and database are asserted too: a message that quoted the string
        // with the password masked out would still be quoting it.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        NpgsqlConnectionStringBuilder refused = new(host.AppConnectionString) { IncludeErrorDetail = true };

        // Act
        Exception? failure = await CaptureStartupFailureAsync(
            refused.ConnectionString, host.ConnectionString, "Production");

        // Assert — every message in the chain, since the host builder is free to wrap the refusal and
        // a wrapper that copied the inner message would leak as surely as the refusal itself.
        await Assert.That(NamesTheOption(failure)).IsTrue();
        string messages = string.Join(Environment.NewLine, MessagesOf(failure));
        await Assert.That(messages).DoesNotContain(refused.Password!);
        await Assert.That(messages).DoesNotContain(refused.Host!);
        await Assert.That(messages).DoesNotContain(refused.Database!);
    }

    [Test]
    public async Task ADevelopmentHostWhoseAdminConnectionStringIncludesErrorDetail_RefusesToStart()
    {
        // Arrange — the application string clean and the admin one carrying the option. The admin
        // connection is the one the Development block migrates and provisions over, so it is checked
        // on its own line, and the refusal has to say which of the two strings was wrong. An empty
        // database again, so a check placed behind the migration is visible too.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using EmptyDatabase empty = await EmptyDatabase.CreateAsync(host.ConnectionString);
        string appConnectionString =
            new NpgsqlConnectionStringBuilder(host.AppConnectionString) { Database = empty.Name }.ConnectionString;
        string adminConnectionString = $"{empty.ConnectionString};Include Error Detail=true";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(
            appConnectionString, adminConnectionString, "Development");

        // Assert
        await Assert.That(NamesTheOption(failure)).IsTrue();
        await Assert.That(MessagesOf(failure).Any(message =>
            message.Contains(AdminConnectionStringKey, StringComparison.Ordinal))).IsTrue();
        await Assert.That(await empty.HasMigrationsHistoryTableAsync()).IsFalse();
    }

    [Test]
    [Arguments("Development")]
    [Arguments("Production")]
    public async Task AHostWhoseConnectionStringSetsOtherOptions_StartsNormally(string environment)
    {
        // Arrange — two options beside it that the application has no quarrel with, and the forbidden
        // one switched off. A refusal that fired on any option it did not recognise, or on any key
        // containing a word it did, would refuse this host.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        string connectionString =
            $"{host.AppConnectionString};Timeout=30;Command Timeout=45;Include Error Detail=false";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(connectionString, host.ConnectionString, environment);

        // Assert
        await Assert.That(failure).IsNull();
    }

    /// <summary>
    /// Builds a host with <paramref name="appConnectionString" /> as <c>ConnectionStrings:budgetoid</c>
    /// and <paramref name="adminConnectionString" /> as <c>ConnectionStrings:budgetoid-admin</c>, and
    /// returns whatever starting it threw, or <see langword="null" /> when it came up.
    /// </summary>
    /// <remarks>
    /// <c>PasskeyConfigurationTests</c>' helper in the shape it has, with the environment and both
    /// connection strings lifted to parameters.
    /// </remarks>
    private static async Task<Exception?> CaptureStartupFailureAsync(
        string appConnectionString,
        string adminConnectionString,
        string environment)
    {
        await using ApiFactory factory = new(
            appConnectionString,
            environment: environment,
            adminConnectionString: adminConnectionString);

        try
        {
            // Resolving anything is what forces the host to be built and started.
            _ = factory.Services.GetRequiredService<IServiceProvider>();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Whether the failure, or anything it wraps, is an <see cref="InvalidOperationException" /> whose
    /// message names the option by its canonical spelling.
    /// </summary>
    /// <remarks>
    /// The chain is walked because the host builder is free to wrap what a startup guard threw. A host
    /// that came up at all is <see langword="null" /> here and answers <see langword="false" />.
    /// </remarks>
    private static bool NamesTheOption(Exception? failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && current.Message.Contains(IncludeErrorDetailKeyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The message of the failure and of everything it wraps, outermost first.</summary>
    private static IEnumerable<string> MessagesOf(Exception? failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            yield return current.Message;
        }
    }

    /// <summary>
    /// A database in the shared cluster that nothing has migrated, dropped on disposal.
    /// </summary>
    /// <remarks>
    /// Made here rather than by <see cref="SharedPostgresCluster" />, which clones every database it
    /// hands out from the migrated template. <c>CREATE DATABASE</c> without a template copies
    /// <c>template1</c>, which carries no application table. The application role is cluster-level,
    /// so it can already log in here; it has no grant on anything, which a refused host never asks for.
    /// </remarks>
    private sealed class EmptyDatabase : IAsyncDisposable
    {
        private EmptyDatabase(string name, string connectionString)
        {
            Name = name;
            ConnectionString = connectionString;
        }

        public string Name { get; }

        /// <summary>The superuser connection string reaching this database.</summary>
        public string ConnectionString { get; }

        public static async Task<EmptyDatabase> CreateAsync(string clusterConnectionString)
        {
            string name = $"budgetoid_empty_{Guid.NewGuid():N}";
            NpgsqlConnectionStringBuilder maintenance = new(clusterConnectionString)
            {
                Database = "postgres",
                Pooling = false,
            };

            await using (NpgsqlConnection connection = new(maintenance.ConnectionString))
            {
                await connection.OpenAsync();
                await using NpgsqlCommand command = new($"create database {name}", connection);
                await command.ExecuteNonQueryAsync();
            }

            string connectionString =
                new NpgsqlConnectionStringBuilder(clusterConnectionString) { Database = name }.ConnectionString;
            return new EmptyDatabase(name, connectionString);
        }

        /// <summary>Whether EF's migration history table exists, which the first migration creates.</summary>
        public async Task<bool> HasMigrationsHistoryTableAsync()
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(
                """select to_regclass('public."__EFMigrationsHistory"') is not null""", connection);
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync() => await SharedPostgresCluster.DropDatabaseAsync(ConnectionString);
    }
}
