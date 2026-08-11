using Infrastructure.Persistence.Provisioning;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

/// <summary>
/// Runs the whole deploy-time provisioning step as the kind of principal production actually has: a
/// role that may create roles and owns the schema, and is <b>not</b> a superuser. Azure Database for
/// PostgreSQL hands out no superuser to anyone — the deploy identity is a member of
/// <c>azure_pg_admin</c> — so every privilege check inside <c>app-role-grants.sql</c> is unverified by
/// a test that runs the script as the container account.
/// </summary>
/// <remarks>
/// <para>
/// That gap has already shipped a broken deploy once. The script carried
/// <c>ALTER ROLE budgetoid_app SET app.current_budget_id = ''</c>, which PostgreSQL allows only to a
/// superuser because <c>app.current_budget_id</c> is a <i>placeholder</i> parameter — no loaded
/// extension has registered it, so the server cannot validate the value and refuses to store it as a
/// role default for anyone else. Under Testcontainers the statement ran as superuser and passed; in
/// production it failed with <c>42501</c> and took the deploy with it.
/// </para>
/// <para>
/// <c>DeploymentProvisioningTests</c> deliberately observes the schema through the superuser
/// connection, and that is right for what it asserts: <c>pg_class</c> and <c>pg_policy</c> read the
/// same whoever asks. This file asserts the one thing that connection structurally cannot — that the
/// statements provisioning sends are within reach of the identity that will send them.
/// </para>
/// </remarks>
public sealed class NonSuperuserDeploymentProvisioningTests
{
    /// <summary>
    /// The deploy principal this file provisions through: <c>LOGIN CREATEROLE</c>, owner of the
    /// application database, and no superuser attribute.
    /// </summary>
    /// <remarks>
    /// <c>CREATEROLE</c> alone is not enough to reach <c>budgetoid_app</c>: PostgreSQL grants a
    /// <c>CREATEROLE</c> non-superuser authority only over roles it created (or was given
    /// <c>ADMIN OPTION</c> on), so a <c>budgetoid_app</c> that already existed by another hand would
    /// refuse every <c>ALTER ROLE</c> here with "Only roles with the CREATEROLE attribute and the
    /// ADMIN option". The script's <c>DO</c> block creates the role itself, which is what makes this
    /// work and is also how production reaches the same state.
    /// </remarks>
    private const string DeployAdminRole = "deploy_admin";

    /// <summary>
    /// Password for <see cref="DeployAdminRole"/>. A constant is fine: the container lives for one
    /// test and is unreachable from outside it.
    /// </summary>
    private const string DeployAdminPassword = "deploy-admin-password";

    /// <summary>
    /// The session setting the isolation policies read. Setting it as a role default is the statement
    /// that broke the deploy, and re-sending it below is what proves this container reproduces the
    /// Azure restriction rather than merely running a different script successfully.
    /// </summary>
    private const string PlaceholderParameter = "app.current_budget_id";

    [Test]
    public async Task ProvisionAsync_AsANonSuperuserCreateroleAdmin_ProvisionsTheDatabase()
    {
        // Arrange — a bare database plus a deploy principal built to match what Azure gives out:
        // allowed to create roles, owner of the database (which in PostgreSQL 15+ is what carries
        // CREATE on schema public, so the migration and the grants have somewhere to land), and
        // without the superuser attribute that makes every privilege check below vacuous.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await using (NpgsqlConnection superuser = await OpenSuperuserAsync(container))
        {
            await ExecuteAsync(
                superuser,
                $"create role {DeployAdminRole} with login createrole "
                + $"password '{DeployAdminPassword}'");
            await ExecuteAsync(superuser, $"alter database budgetoid owner to {DeployAdminRole}");
        }

        string deployAdminConnectionString = BuildDeployAdminConnectionString(container);

        // Act
        Exception? provisioningFailure = null;
        try
        {
            await DeploymentDatabaseProvisioning.ProvisionAsync(deployAdminConnectionString);
        }
        catch (Exception exception)
        {
            provisioningFailure = exception;
        }

        await using NpgsqlConnection admin = await OpenSuperuserAsync(container);
        bool deployAdminIsSuperuser = await IsSuperuserAsync(admin, DeployAdminRole);
        (bool roleExists, bool canLogin, bool hasNoPassword) = await ReadAppRoleAsync(admin);

        // The control that makes this test mean what it claims. Provisioning succeeding proves the
        // script is within the deploy principal's reach only if that principal is genuinely
        // restricted — so the removed statement is sent again, by the same role, on the same server.
        await using NpgsqlConnection deployAdmin = new(deployAdminConnectionString);
        await deployAdmin.OpenAsync();
        string? placeholderDefaultSqlState = await TrySetRoleDefaultAsync(
            deployAdmin, $"{PlaceholderParameter} = ''");

        // And the counter-control: the same role, the same ALTER ROLE ... SET grammar, a parameter
        // the server knows. Without this, the refusal above would be equally explained by a principal
        // that cannot alter budgetoid_app at all, and the test would be pinning the wrong restriction.
        string? knownParameterSqlState = await TrySetRoleDefaultAsync(
            deployAdmin, "statement_timeout = '5s'");

        // Assert — the failure first and as the exception rather than a boolean, so a regression
        // reports the SQLSTATE and the statement that produced it instead of "expected true".
        await Assert.That(provisioningFailure).IsNull();

        await Assert.That(deployAdminIsSuperuser).IsFalse();
        await Assert.That(placeholderDefaultSqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(knownParameterSqlState).IsNull();

        // The role provisioning was supposed to leave behind, in the same shape the superuser path
        // produces. A run that swallowed a privilege failure part-way through the script would still
        // satisfy "no exception" — this is what says the whole script ran.
        await Assert.That(roleExists).IsTrue();
        await Assert.That(canLogin).IsTrue();
        await Assert.That(hasNoPassword).IsTrue();
    }

    /// <summary>
    /// Starts an empty PostgreSQL container. Same builder as the test hosts, so this runs against the
    /// same server version as the rest of the suite; what is missing is everything they do afterwards.
    /// </summary>
    /// <remarks>
    /// The try/catch is a leak guard, and the reasoning behind it is written out once on
    /// <c>DeploymentProvisioningTests.StartBareContainerAsync</c> — the same helper, the same shape,
    /// in the other class that deliberately keeps a container of its own. In short: the call site
    /// binds its <c>await using</c> variable only after this method returns, so a throw here leaves a
    /// container Docker has already started with nothing left to dispose it, and each such leak makes
    /// the next start likelier to time out. Guarded by shape-match to that documented failure mode,
    /// not because a failure was captured here.
    /// </remarks>
    private static async Task<PostgreSqlContainer> StartBareContainerAsync()
    {
        PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17")
            .WithDatabase("budgetoid")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        try
        {
            await container.StartAsync();
            return container;
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens a connection as the container account, which is a superuser. Used only to build the
    /// restricted principal and to read the catalogs afterwards — never to provision, which is the
    /// entire point of this file.
    /// </summary>
    private static async Task<NpgsqlConnection> OpenSuperuserAsync(PostgreSqlContainer container)
    {
        NpgsqlConnection connection = new(container.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// The container's connection string re-pointed at <see cref="DeployAdminRole"/>, so provisioning
    /// reaches the same database over the same options as everything else here.
    /// </summary>
    private static string BuildDeployAdminConnectionString(PostgreSqlContainer container) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = DeployAdminRole,
            Password = DeployAdminPassword,
        }.ConnectionString;

    /// <summary>
    /// Sends <c>ALTER ROLE budgetoid_app SET <paramref name="assignment"/></c> and reports the
    /// SQLSTATE it was refused with, or <see langword="null"/> if it was accepted.
    /// </summary>
    /// <remarks>
    /// The assignment is spliced rather than bound because <c>ALTER ROLE ... SET</c> takes no
    /// parameter; both call sites pass a constant of this class.
    /// </remarks>
    private static async Task<string?> TrySetRoleDefaultAsync(
        NpgsqlConnection connection,
        string assignment)
    {
        try
        {
            await ExecuteAsync(
                connection, $"alter role {DatabaseProvisioning.AppRoleName} set {assignment}");
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    /// <summary>Reports whether a role carries the superuser attribute.</summary>
    private static async Task<bool> IsSuperuserAsync(NpgsqlConnection connection, string role)
    {
        await using NpgsqlCommand command = new(
            "select coalesce((select rolsuper from pg_roles where rolname = @role), false)",
            connection);
        command.Parameters.AddWithValue("role", role);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Reads whether the application role exists, may log in, and has no password, from
    /// <c>pg_authid</c> in one row — the same three facts <c>DeploymentProvisioningTests</c> pins on
    /// the superuser path, asserted here about the restricted one.
    /// </summary>
    private static async Task<(bool Exists, bool CanLogin, bool HasNoPassword)> ReadAppRoleAsync(
        NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            select rolcanlogin, rolpassword is null
            from pg_authid
            where rolname = @role
            """,
            connection);
        command.Parameters.AddWithValue("role", DatabaseProvisioning.AppRoleName);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (false, false, false);
        }

        return (true, reader.GetBoolean(0), reader.GetBoolean(1));
    }

    /// <summary>
    /// Sends one statement that is expected to succeed. Used only for setup, where the SQL is built
    /// from constants of this class rather than from input.
    /// </summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
