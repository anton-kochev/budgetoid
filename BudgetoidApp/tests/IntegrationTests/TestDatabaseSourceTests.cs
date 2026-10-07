using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins how the suite chooses its PostgreSQL server: a connection string from the environment, or a
/// container it starts itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a server can come from outside at all.</b> Inside a Pithos agent session there is no Docker,
/// but there is a PostgreSQL next to the agent, handed over as <c>PITHOS_POSTGRES_URL</c>. A developer
/// or a CI job can name any other server with <c>BUDGETOID_TEST_DATABASE_URL</c>. With neither, the suite
/// starts its own container exactly as before.
/// </para>
/// <para>
/// These run against a fake environment, so none of them needs a database.
/// </para>
/// </remarks>
public sealed class TestDatabaseSourceTests
{
    private const string Ours = "BUDGETOID_TEST_DATABASE_URL";
    private const string Pithos = "PITHOS_POSTGRES_URL";

    [Test]
    public async Task Resolve_WithNoVariable_StartsAContainer()
    {
        // Act
        TestDatabaseSource source = TestDatabaseSource.Resolve(Env());

        // Assert
        await Assert.That(source.StartsContainer).IsTrue();
        await Assert.That(source.Variable).IsNull();
    }

    [Test]
    public async Task Resolve_PrefersTheProjectVariableOverPithos()
    {
        // Act
        TestDatabaseSource source = TestDatabaseSource.Resolve(Env(
            (Ours, "Host=ours;Username=u;Password=p"),
            (Pithos, "postgresql://postgres:x@pithos-postgres:5432/app")));

        // Assert
        await Assert.That(source.Variable).IsEqualTo(Ours);
        await Assert.That(new NpgsqlConnectionStringBuilder(source.ConnectionString).Host).IsEqualTo("ours");
    }

    /// <summary>
    /// An exported-but-empty variable is how a shell says "unset" more often than it means "connect to
    /// nothing"; reading it as a server would fail every test with a parse error about an empty string.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Resolve_TreatsABlankVariableAsUnset(string blank)
    {
        // Act
        TestDatabaseSource source = TestDatabaseSource.Resolve(Env(
            (Ours, blank),
            (Pithos, "postgresql://postgres:x@pithos-postgres:5432/app")));

        // Assert
        await Assert.That(source.Variable).IsEqualTo(Pithos);
    }

    [Test]
    public async Task Resolve_TurnsAPostgresUriIntoAConnectionString()
    {
        // Act
        TestDatabaseSource source = TestDatabaseSource.Resolve(Env(
            (Pithos, "postgresql://postgres:s%40cr%3At@pithos-postgres:6543/budgetoid?sslmode=require")));

        // Assert
        NpgsqlConnectionStringBuilder builder = new(source.ConnectionString);
        await Assert.That(builder.Host).IsEqualTo("pithos-postgres");
        await Assert.That(builder.Port).IsEqualTo(6543);
        await Assert.That(builder.Username).IsEqualTo("postgres");
        await Assert.That(builder.Password).IsEqualTo("s@cr:t");
        await Assert.That(builder.Database).IsEqualTo("budgetoid");
        await Assert.That(builder.SslMode).IsEqualTo(SslMode.Require);
    }

    [Test]
    public async Task Resolve_DefaultsAUriWithoutPortOrDatabase()
    {
        // Act
        TestDatabaseSource source = TestDatabaseSource.Resolve(Env((Ours, "postgres://u:p@db")));

        // Assert
        NpgsqlConnectionStringBuilder builder = new(source.ConnectionString);
        await Assert.That(builder.Port).IsEqualTo(5432);
        await Assert.That(builder.Database).IsNull();
    }

    [Test]
    public async Task Resolve_PassesAKeyValueConnectionStringThrough()
    {
        // Act
        TestDatabaseSource source = TestDatabaseSource.Resolve(Env(
            (Ours, "Host=db;Port=5433;Username=u;Password=p;Database=x")));

        // Assert
        NpgsqlConnectionStringBuilder builder = new(source.ConnectionString);
        await Assert.That(builder.Port).IsEqualTo(5433);
        await Assert.That(builder.Database).IsEqualTo("x");
    }

    /// <summary>
    /// A malformed value stops the run with a message that names the variable and never the password.
    /// </summary>
    /// <remarks>
    /// An unknown query parameter is refused rather than dropped: silently ignoring, say, a TLS
    /// setting would connect to the server with weaker guarantees than the person who wrote it asked
    /// for, and nothing would say so.
    /// </remarks>
    [Test]
    [Arguments("mysql://u:hunter2@db/x")]
    [Arguments("postgresql://u:hunter2@db/x?application_name=y")]
    [Arguments("postgresql://u:hunter2@db/x?sslmode=sometimes")]
    [Arguments("postgresql://u:hunter2@/x")]
    public async Task Resolve_RefusesAMalformedUri_NamingTheVariableNotTheSecret(string value)
    {
        // Act
        ArgumentException exception = await Assert.That(() => TestDatabaseSource.Resolve(Env((Ours, value))))
            .Throws<ArgumentException>();

        // Assert
        await Assert.That(exception.Message).Contains(Ours);
        await Assert.That(exception.Message).DoesNotContain("hunter2");
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;
}
