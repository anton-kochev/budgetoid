using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

/// <summary>
/// Covers the one operation a deploy cannot be trusted to perform in two steps: migrating the schema
/// and then provisioning the role, its grants, and its row-level security policies. Today those are
/// <c>DEPLOYMENT.md</c> Steps 3 and 4 — a migration bundle, then <c>app-role-grants.sql</c> piped
/// through <c>psql</c> — and the second one being skipped is not a visible failure. The grant matrix
/// is fail-closed, so a missing grant stops a feature dead with <c>42501</c>. Row-level security is
/// fail-open: a granted table with no policy is readable and writable by the application role across
/// every tenant, silently and indistinguishably from working. A deploy that migrates and forgets to
/// provision is therefore a tenancy breach nothing reports, which is why the ordering belongs in code
/// and why <c>VerifyRowLevelSecurityCoverageAsync</c> exists at all — and why it is named for row-level
/// security rather than for provisioning as a whole. It does not check the grants, and it should not:
/// a missing grant is fail-closed and announces itself as <c>42501</c> at the first statement that
/// needs it, so there is nothing silent there to verify.
/// </summary>
/// <remarks>
/// <para>
/// Every test here spins a <b>bare</b> <see cref="PostgreSqlContainer" /> rather than using
/// <c>RepositoryTestHost</c>. That is not a style preference: the host's <c>StartAsync</c> already
/// runs <c>MigrateAsync</c> and <c>ApplyGrantsAsync</c>, so a test built on it starts from a database
/// that is already provisioned and could never observe "empty database becomes a provisioned one" —
/// which is the entire subject of this file. The runtime pattern the host uses is what
/// <c>ProvisionAsync</c> is extracting, so the host is the thing under test's ancestor, not its
/// fixture.
/// </para>
/// <para>
/// The container account is a superuser and every observation below except one is made through it.
/// That is deliberate and not a privilege blind spot: <c>pg_class</c> and <c>pg_policies</c> describe
/// the schema, and the schema reads the same whoever asks. The single exception is the application
/// role's login in <see cref="ProvisionAsync_OnEmptyDatabase_MigratesSchemaAndCreatesRole" />, which
/// is the one fact only a non-superuser connection can establish.
/// </para>
/// </remarks>
public sealed class DeploymentProvisioningTests
{
    /// <summary>
    /// Password these tests hand to provisioning for the application role. A constant is fine: the
    /// container lives for one test and is unreachable from outside it. Every character is inside
    /// the alphabet <c>DatabaseProvisioning</c> permits, so a failure here is never about the
    /// password.
    /// </summary>
    private const string AppRolePassword = "deploy-test-password";

    /// <summary>
    /// A password carrying a single quote — the character that would close the SQL literal the
    /// grants script splices it into, and the reason the alphabet check exists.
    /// </summary>
    private const string PasswordWithASingleQuote = "deploy'test";

    /// <summary>
    /// A table the baseline migration creates. Any migrated table would do; this one is named
    /// because it is also one of the budget-owned tables the policies below have to cover, so a
    /// database where it is missing fails this file in two places rather than one.
    /// </summary>
    private const string MigratedTable = "transactions";

    /// <summary>
    /// EF Core's migration-history table, spelled exactly as EF creates it. Its presence is the
    /// earliest trace a migration attempt leaves behind, which is what makes it the right thing to
    /// look for when asserting that no migration was attempted at all.
    /// </summary>
    private const string MigrationHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// The budget-owned table whose protection the two verification tests sabotage. Any of the five
    /// would do; payees is picked because it is the one with no <c>DELETE</c> grant, so a reader
    /// tempted to conclude the tests only work on fully-granted tables is wrong.
    /// </summary>
    private const string SabotagedTable = "payees";

    [Test]
    public async Task ProvisionAsync_OnEmptyDatabase_MigratesSchemaAndCreatesRole()
    {
        // Arrange — an empty database: no schema, no migration history, no application role. This
        // is the state a first production deploy starts from and the only state in which "did
        // provisioning do the work" and "was the work already there" can be told apart.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        List<string> logLines = [];

        // Act
        await DeploymentDatabaseProvisioning.ProvisionAsync(
            container.GetConnectionString(),
            AppRolePassword,
            log: logLines.Add);

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        bool migratedTableExists = await TableExistsAsync(admin, MigratedTable);

        await using BudgetoidDbContext db = CreateDbContext(container);
        List<string> pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        // The only honest assertion about the role. A row in pg_roles proves nothing a deploy cares
        // about: a role can exist with NOLOGIN, or with a password other than the one the deployed
        // container was handed, and a catalog query would call both of those a success while the API
        // fails to start. Opening a real connection as the role, with the password provisioning was
        // given, is the whole claim.
        await using NpgsqlConnection app = new(AppConnectionString(container, AppRolePassword));
        await app.OpenAsync();
        await using NpgsqlCommand whoami = new("select current_user", app);
        object? connectedAs = await whoami.ExecuteScalarAsync();

        // Assert
        await Assert.That(migratedTableExists).IsTrue();
        await Assert.That(pending).IsEmpty();
        await Assert.That(connectedAs).IsEqualTo(DatabaseProvisioning.AppRoleName);

        // The log is the only visibility a deploy pipeline has into this call, and a run that
        // reported nothing would read identically to a run that did nothing. The assertion is on
        // presence rather than wording on purpose: the message text is not a contract, but the
        // pipeline getting a trace at all is.
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task ProvisionAsync_RunTwice_Converges()
    {
        // Arrange
        await using PostgreSqlContainer container = await StartBareContainerAsync();

        // Act — idempotency is the deploy contract, not a nicety: this runs on every push, so the
        // second call is the common case and the first is the exception. The second call takes
        // different code paths inside the grants script than the first — ALTER ROLE instead of
        // CREATE ROLE, DROP POLICY finding something to drop — and those paths only ever execute on
        // a re-run, so nothing else in this file exercises them.
        await DeploymentDatabaseProvisioning.ProvisionAsync(
            container.GetConnectionString(),
            AppRolePassword);
        await DeploymentDatabaseProvisioning.ProvisionAsync(
            container.GetConnectionString(),
            AppRolePassword);

        await using BudgetoidDbContext db = CreateDbContext(container);
        List<string> pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        // Re-asserting the login after the second run is what stops this from being a bare
        // "it didn't throw" test. The re-run path resets the role's password rather than creating
        // it, and a password reset that landed wrong would leave the role present, the schema
        // migrated, the call successful, and the deployed application unable to connect.
        await using NpgsqlConnection app = new(AppConnectionString(container, AppRolePassword));
        await app.OpenAsync();
        await using NpgsqlCommand whoami = new("select current_user", app);
        object? connectedAs = await whoami.ExecuteScalarAsync();

        // Assert
        await Assert.That(pending).IsEmpty();
        await Assert.That(connectedAs).IsEqualTo(DatabaseProvisioning.AppRoleName);
    }

    [Test]
    public async Task ProvisionAsync_PolicesEveryBudgetOwnedTable()
    {
        // Arrange
        await using PostgreSqlContainer container = await StartBareContainerAsync();

        // Act
        await DeploymentDatabaseProvisioning.ProvisionAsync(
            container.GetConnectionString(),
            AppRolePassword);

        await using NpgsqlConnection admin = await OpenAdminAsync(container);

        // The table list is derived from the live schema, never written down. A hardcoded list of
        // the five known names would keep passing on the day someone adds the sixth, which is the
        // only day this assertion matters.
        IReadOnlyList<(string Table, bool RowSecurityEnabled)> tables =
            await DiscoverBudgetOwnedTablesAsync(admin);
        List<string> unprotected = tables
            .Where(entry => !entry.RowSecurityEnabled)
            .Select(entry => entry.Table)
            .ToList();

        List<string> wronglyPoliced = [];
        foreach ((string table, _) in tables)
        {
            int policies = await CountIsolationPoliciesAsync(admin, table);

            // Exactly one, not at least one. These policies are permissive and permissive policies
            // OR together, so a second one can only widen what the first allows — a table that grew
            // a stray policy has quietly stopped meaning what its isolation policy says.
            if (policies != 1)
            {
                wronglyPoliced.Add($"{table}: {policies} policies, wanted 1");
            }
        }

        // Assert — the non-empty check comes first and is not decoration. If the discovery query
        // silently matched nothing, both lists below would be empty and every remaining assertion
        // would pass with nothing in it, on a database with no policies at all.
        await Assert.That(tables).IsNotEmpty();
        await Assert.That(unprotected).IsEmpty();
        await Assert.That(wronglyPoliced).IsEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_MissingPolicy_ThrowsListingTheTable()
    {
        // Arrange — provision, then take one table's policy away as the admin. Dropping a policy is
        // exactly the shape of the real accident: a table that was granted and never policed looks
        // identical to this from the catalog's point of view.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(
            container.GetConnectionString(),
            AppRolePassword);

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"drop policy budget_isolation on {SabotagedTable}");

        // Act — the verifier directly, never through ProvisionAsync. ProvisionAsync re-applies the
        // grants script before it verifies, so it would heal this damage on the way past and the
        // test would pass with the verifier left as an empty method body. Going straight at the
        // verifier is the only way this test can fail for the reason it names. Calling it this way
        // is also why it takes a log: on this path it is the whole of a deploy step, and its own
        // account of what it inspected is the operator's only context for the refusal.
        List<string> logLines = [];
        InvalidOperationException? caught = null;
        try
        {
            await DeploymentDatabaseProvisioning.VerifyRowLevelSecurityCoverageAsync(
                container.GetConnectionString(),
                log: logLines.Add);
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        List<string> reportedTables =
            (caught as RowLevelSecurityCoverageException)?.Tables.ToList() ?? [];

        // Assert — the type first, and it is caught as the base InvalidOperationException on purpose
        // so that this proves two things at once: the thrown exception is exactly the coverage
        // exception, and it is still catchable as the base type anything already handling
        // provisioning failures expects.
        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();

        // Then the table, read out of the structured list rather than out of the message. This is
        // what lets a pipeline tell "coverage is incomplete, here is where" from "something else
        // broke", and it cannot be satisfied by an exception that merely happens to mention payees.
        await Assert.That(reportedTables).Contains(SabotagedTable);

        // The message is asserted as well, and it is not a weaker restatement of the line above: an
        // uncaught throw out of a deploy step shows the operator its Message and nothing else, so a
        // coverage exception whose table names live only in a property is unreadable in exactly the
        // situation it exists for.
        await Assert.That(caught!.Message).Contains(SabotagedTable);

        // A log delegate that is accepted and then never called is a broken contract nobody would
        // notice. Presence, not wording — the text is not a contract, the trace is.
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_RowSecurityDisabled_ThrowsListingTheTable()
    {
        // Arrange — a distinct failure from the missing-policy case, and the reason both tests
        // exist. Here the policy is still defined and still listed in pg_policies; it is simply not
        // being enforced, because row-level security is switched off for the table. A verifier that
        // only read pg_policies would call this database fully protected while the application role
        // reads every tenant's rows.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(
            container.GetConnectionString(),
            AppRolePassword);

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin, $"alter table {SabotagedTable} disable row level security");

        // Act
        int survivingPolicies = await CountIsolationPoliciesAsync(admin, SabotagedTable);

        List<string> logLines = [];
        InvalidOperationException? caught = null;
        try
        {
            await DeploymentDatabaseProvisioning.VerifyRowLevelSecurityCoverageAsync(
                container.GetConnectionString(),
                log: logLines.Add);
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        List<string> reportedTables =
            (caught as RowLevelSecurityCoverageException)?.Tables.ToList() ?? [];

        // Assert — the surviving policy is asserted first, because without it this test could be
        // the previous one wearing a different name. One policy still present is what pins the
        // sabotage as "defined but unenforced" rather than "gone".
        await Assert.That(survivingPolicies).IsEqualTo(1);

        // Then the same four claims as the missing-policy case, spelled out again rather than shared,
        // because an unenforced table has to be reported the same way a policy-less one is: this
        // failure is no less of a tenancy breach for having a policy on paper, and a verifier that
        // reported it differently would send an operator looking for a missing policy that is right
        // there. The reasoning behind each line is in the missing-policy test above.
        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(SabotagedTable);
        await Assert.That(caught!.Message).Contains(SabotagedTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task ProvisionAsync_InvalidPasswordAlphabet_ThrowsBeforeTouchingTheDatabase()
    {
        // Arrange
        await using PostgreSqlContainer container = await StartBareContainerAsync();

        // Act
        ArgumentException? caught = null;
        try
        {
            await DeploymentDatabaseProvisioning.ProvisionAsync(
                container.GetConnectionString(),
                PasswordWithASingleQuote);
        }
        catch (ArgumentException exception)
        {
            caught = exception;
        }

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        bool migrationHistoryExists = await TableExistsAsync(admin, MigrationHistoryTable);

        // Assert — the refusal is half the test; the untouched database is the half that matters.
        // A typo in a deploy secret must not be able to leave production half-migrated with no role
        // to run it under, so the alphabet check has to happen before the first statement rather
        // than wherever the password is eventually needed. The migration-history table is the
        // earliest trace MigrateAsync leaves, so its absence is the strongest available statement
        // that nothing ran.
        await Assert.That(caught).IsNotNull();
        await Assert.That(migrationHistoryExists).IsFalse();
    }

    /// <summary>
    /// Starts an empty PostgreSQL container. The builder is the same one the test hosts use, so
    /// these tests and the rest of the integration suite run against the same server version; what
    /// is deliberately missing is everything the hosts do afterwards.
    /// </summary>
    private static async Task<PostgreSqlContainer> StartBareContainerAsync()
    {
        PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17")
            .WithDatabase("budgetoid")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await container.StartAsync();
        return container;
    }

    /// <summary>
    /// Opens a connection as the container account, which is a superuser. Every schema observation
    /// in this file goes through it; the application role could not answer most of these questions
    /// and is not being measured by them.
    /// </summary>
    private static async Task<NpgsqlConnection> OpenAdminAsync(PostgreSqlContainer container)
    {
        NpgsqlConnection connection = new(container.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// Builds the connection string for the least-privilege application role against the same
    /// container, so a test can find out whether provisioning produced a role that can actually
    /// log in.
    /// </summary>
    private static string AppConnectionString(PostgreSqlContainer container, string password) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = DatabaseProvisioning.AppRoleName,
            Password = password,
        }.ConnectionString;

    /// <summary>
    /// A context built exactly the way <c>ProvisionAsync</c> builds one — directly, on the admin
    /// connection string, with no <c>IBudgetContext</c>. Migration state is a property of the
    /// database rather than of a tenant, so there is no ambient budget for this context to carry.
    /// </summary>
    private static BudgetoidDbContext CreateDbContext(PostgreSqlContainer container) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .Options);

    /// <summary>
    /// Reports whether an ordinary table of that exact name exists in <c>public</c>. The name is
    /// matched case-sensitively against <c>pg_class</c>, which is what lets
    /// <c>__EFMigrationsHistory</c> be looked up by the mixed-case name EF quotes it with.
    /// </summary>
    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table)
    {
        await using NpgsqlCommand command = new(
            """
            select exists (
                select 1
                from pg_class c
                join pg_namespace n on n.oid = c.relnamespace
                where n.nspname = 'public' and c.relkind = 'r' and c.relname = @table)
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Returns every ordinary table in <c>public</c> carrying a <c>budget_id</c> column, with
    /// whether row-level security is switched on for it.
    /// </summary>
    /// <remarks>
    /// The <c>budget_id</c> column <i>is</i> the definition of budget-owned, which is why it and not
    /// a name list is the filter. <c>budgets</c>, <c>users</c>, <c>currencies</c> and
    /// <c>__EFMigrationsHistory</c> drop out for free: a budget is the tenant rather than a tenant's
    /// row, and none of the other three belongs to one. <c>relrowsecurity</c> is read in the same
    /// row as the discovery so that "is this table budget-owned" and "is it protected" cannot drift
    /// into two lists that disagree. This duplicates the query <c>RlsCoverageTests</c> runs, on
    /// purpose: both files need to observe the fact from outside the code that establishes it, and a
    /// shared helper would make one test's subject the other's fixture.
    /// </remarks>
    private static async Task<IReadOnlyList<(string Table, bool RowSecurityEnabled)>>
        DiscoverBudgetOwnedTablesAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            select c.relname, c.relrowsecurity
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and c.relkind = 'r'
              and exists (
                  select 1
                  from pg_attribute a
                  where a.attrelid = c.oid
                    and a.attname = 'budget_id'
                    and a.attnum > 0
                    and not a.attisdropped)
            order by c.relname
            """,
            connection);

        List<(string, bool)> tables = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add((reader.GetString(0), reader.GetBoolean(1)));
        }

        return tables;
    }

    /// <summary>
    /// Counts the policies defined on one table. Every policy, not only the ones named
    /// <c>budget_isolation</c>: the count is asserted to be exactly one, and narrowing the query to
    /// a name would hide the extra policy that assertion exists to catch.
    /// </summary>
    private static async Task<int> CountIsolationPoliciesAsync(
        NpgsqlConnection connection,
        string table)
    {
        await using NpgsqlCommand command = new(
            """
            select count(*)
            from pg_policies
            where schemaname = 'public' and tablename = @table
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Sends one statement that is expected to succeed. Used only for the admin-side sabotage in the
    /// verification tests, where the table name is a constant of this class rather than input.
    /// </summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
