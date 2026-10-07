using System.Globalization;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins that a host outside Development reaches its database over TLS that validates the server's
/// certificate and host name (<c>SSL Mode=VerifyFull</c>), and refuses to start on a connection
/// string that names a weaker mode — every one but <c>Prefer</c>, which is forced up rather than refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> <c>Require</c> encrypts but checks nothing about who answered, so anything
/// able to sit on the path to the database can present its own certificate, read the traffic and
/// relay it. <c>VerifyCA</c> checks the chain but not the name, so any certificate the trusted
/// authority ever issued is accepted. Only <c>VerifyFull</c> ties the connection to this server.
/// </para>
/// <para>
/// <b>Why a weaker mode is refused rather than overwritten.</b> Enforcement means rejecting (ADR 0002),
/// the same argument <see cref="ConnectionStringOptionTests" /> pins for the three forbidden options: a
/// silent upgrade hides from whoever wrote <c>Require</c> that it never took effect. <c>Prefer</c> is
/// the one exception, by choice: an absent key already reads as <c>Prefer</c> and is forced to
/// <c>VerifyFull</c>, so an explicit <c>Prefer</c> is forced up the same way rather than refused. The
/// two are distinguishable — the builder's round-tripped string carries the key only when it was
/// written — so a forcing that upgraded only the absent key would leave an explicit <c>Prefer</c>
/// behind, and <see cref="NonDevelopmentHost_OpensItsDatabaseConnectionWithVerifyFull" /> covers both.
/// </para>
/// <para>
/// <b>How the effective mode is read.</b> The host's own <see cref="BudgetoidDbContext" /> is resolved
/// from a scope and <c>Database.GetDbConnection().ConnectionString</c> is parsed with Npgsql's builder.
/// That is the string the data source behind the Azure enrichment was built from, so it carries the
/// <c>SSL Mode</c> the application will open with (measured: the Development control reads back the
/// mode it configured, and a Production host today reads back <c>Require</c>). The connection is never
/// opened — the test cluster serves no TLS, so opening would fail for a reason that is not this one.
/// </para>
/// <para>
/// <b>Development is the control on every axis.</b> The local and test PostgreSQL images have no TLS,
/// so Development is left alone: no forcing, no refusal.
/// </para>
/// </remarks>
public sealed class DatabaseTransportSecurityTests
{
    /// <summary>The configuration key of the application connection string, which every refusal names.</summary>
    private const string AppConnectionStringKey = "ConnectionStrings:budgetoid";

    /// <summary>The canonical spelling, as Npgsql's builder writes it back, which every refusal names.</summary>
    private const string SslModeKeyword = "SSL Mode";

    [Test]
    [Arguments("Production", "")]
    [Arguments("Staging", "")]
    [Arguments("Production", ";SSL Mode=Prefer")]
    [Arguments("Staging", ";SSL Mode=Prefer")]
    public async Task NonDevelopmentHost_OpensItsDatabaseConnectionWithVerifyFull(
        string environment, string sslModeOption)
    {
        // Arrange — either no SSL Mode key at all, which the builder reads as Prefer, or Prefer written
        // out. The two round-trip to different strings, so a forcing that upgraded only an absent key
        // passes the first pair and fails the second. Staging is here so a fix keyed on IsProduction()
        // rather than !IsDevelopment() goes red. Every row starts its own host, so its own database
        // name keeps the string distinct and EF cannot hand it a data source cached from another row.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        string connectionString = WithoutSslMode(host.AppConnectionString) + sslModeOption;
        await using ApiFactory factory = new(
            connectionString,
            environment: environment,
            adminConnectionString: host.ConnectionString);

        // Act
        SslMode effective = EffectiveSslModeOf(factory);

        // Assert
        await Assert.That(effective).IsEqualTo(SslMode.VerifyFull);
    }

    [Test]
    [Arguments("SSL Mode=Disable", "Production")]
    [Arguments("SSL Mode=Allow", "Production")]
    [Arguments("SSL Mode=Require", "Production")]
    [Arguments("SSL Mode=VerifyCA", "Production")]
    [Arguments("sslmode=require", "Production")]
    [Arguments("SSL Mode=Require", "Staging")]
    public async Task NonDevelopmentHost_WhoseConnectionStringNamesAWeakerSslMode_RefusesToStart(
        string option, string environment)
    {
        // Arrange — every mode weaker than VerifyFull that the builder can name, except Prefer, which is
        // forced up rather than refused; and Require again in a spelling the builder accepts as the same
        // key. A refusal that searched the text for one literal spelling passes the canonical rows and
        // misses that one. The Staging row
        // is there so a refusal keyed on IsProduction() rather than !IsDevelopment() goes red.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        string connectionString = $"{WithoutSslMode(host.AppConnectionString)};{option}";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(connectionString, host.ConnectionString, environment);

        // Assert — on the sentence naming the key and the option, not merely on something having thrown.
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheRefusal(failure)).IsTrue();
    }

    [Test]
    [Arguments("Production")]
    [Arguments("Staging")]
    public async Task NonDevelopmentHost_WhoseConnectionStringAlreadySaysVerifyFull_StartsAndKeepsIt(
        string environment)
    {
        // Arrange — the mode this story requires, written out. A refusal that fired on the key's mere
        // presence would refuse this host; a builder that still forced Require would downgrade it. The
        // Staging row is cheap insurance against a forcing that upgrades Prefer to VerifyFull but
        // still rewrites an explicit VerifyFull outside Production.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        string connectionString = new NpgsqlConnectionStringBuilder(host.AppConnectionString)
        {
            SslMode = SslMode.VerifyFull,
        }.ConnectionString;
        await using ApiFactory factory = new(
            connectionString,
            environment: environment,
            adminConnectionString: host.ConnectionString);

        // Act
        SslMode effective = EffectiveSslModeOf(factory);

        // Assert
        await Assert.That(effective).IsEqualTo(SslMode.VerifyFull);
    }

    /// <summary>A port no message would contain by coincidence; nothing listens on it.</summary>
    private const int DistinctivePort = 54_917;

    [Test]
    public async Task ARefusalForAWeakSslMode_NeverQuotesTheConnectionString()
    {
        // Arrange — the refusal is read by whoever reads the boot log, and the string it refuses carries
        // a password. Username, host, port and database are asserted too: a message that quoted the
        // string with the password masked out would still be quoting it. The port is set to one no
        // message would carry by coincidence: the refusal fires before any connection, and the server's
        // own port is 5432 whenever the suite runs on a server it did not start.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        NpgsqlConnectionStringBuilder refused = new(host.AppConnectionString)
        {
            SslMode = SslMode.Require,
            Port = DistinctivePort,
        };

        // Act
        Exception? failure = await CaptureStartupFailureAsync(
            refused.ConnectionString, host.ConnectionString, "Production");

        // Assert — every message in the chain, since the host builder is free to wrap the refusal and a
        // wrapper that copied the inner message would leak as surely as the refusal itself.
        await Assert.That(NamesTheRefusal(failure)).IsTrue();
        string messages = string.Join(Environment.NewLine, MessagesOf(failure));
        await Assert.That(messages).DoesNotContain(refused.Password!);
        await Assert.That(messages).DoesNotContain(refused.Host!);
        await Assert.That(messages).DoesNotContain(refused.Database!);
        await Assert.That(messages).DoesNotContain(refused.Username!);
        await Assert.That(messages).DoesNotContain(refused.Port.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task DevelopmentHost_LeavesTheConfiguredSslModeAlone()
    {
        // Arrange — the control for the forcing: the local cluster has no TLS, so a Development host that
        // forced any encrypted mode could not reach its own database. It also shows the accessor reads
        // the configured mode back at all.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        string connectionString = WithoutSslMode(host.AppConnectionString);
        await using ApiFactory factory = new(
            connectionString,
            environment: "Development",
            adminConnectionString: host.ConnectionString);

        // Act
        SslMode effective = EffectiveSslModeOf(factory);

        // Assert
        await Assert.That(effective).IsEqualTo(SslMode.Prefer);
    }

    [Test]
    public async Task DevelopmentHost_WhoseConnectionStringNamesRequire_StartsNormally()
    {
        // Arrange — the control for the refusal: a check that forgot the environment would refuse this
        // host. The Development block opens only the admin connection, so Require on the application
        // string never meets the TLS-less cluster during boot.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        string connectionString = new NpgsqlConnectionStringBuilder(host.AppConnectionString)
        {
            SslMode = SslMode.Require,
        }.ConnectionString;

        // Act
        Exception? failure = await CaptureStartupFailureAsync(connectionString, host.ConnectionString, "Development");

        // Assert
        await Assert.That(failure).IsNull();
    }

    /// <summary>The connection string with any <c>SSL Mode</c> key taken out, so the builder reads it as absent.</summary>
    private static string WithoutSslMode(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        builder.Remove(SslModeKeyword);
        return builder.ConnectionString;
    }

    /// <summary>
    /// The <see cref="SslMode" /> the host's own context would open its connection with, read without
    /// opening it.
    /// </summary>
    /// <remarks>
    /// Resolving the scope is also what builds and starts the host, so a refusal surfaces here as a
    /// throw rather than as a mode.
    /// </remarks>
    private static SslMode EffectiveSslModeOf(ApiFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        BudgetoidDbContext context = scope.ServiceProvider.GetRequiredService<BudgetoidDbContext>();
        string connectionString = context.Database.GetDbConnection().ConnectionString;
        return new NpgsqlConnectionStringBuilder(connectionString).SslMode;
    }

    /// <summary>
    /// Builds the host and hands back whatever it threw on the way up, or <see langword="null" /> if it
    /// came up.
    /// </summary>
    /// <remarks><see cref="ConnectionStringOptionTests" />' helper in the shape it has.</remarks>
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
    /// message names both the application connection string's configuration key and <c>SSL Mode</c>.
    /// </summary>
    /// <remarks>
    /// The configuration key is required as well as the keyword: Npgsql throws
    /// <see cref="InvalidOperationException" /> for its own reasons, and a message of its own that
    /// mentioned SSL would otherwise pass as this refusal. A host that came up is <see langword="null" />
    /// here and answers <see langword="false" />.
    /// </remarks>
    private static bool NamesTheRefusal(Exception? failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && current.Message.Contains(AppConnectionStringKey, StringComparison.Ordinal)
                && current.Message.Contains(SslModeKeyword, StringComparison.Ordinal))
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
}
