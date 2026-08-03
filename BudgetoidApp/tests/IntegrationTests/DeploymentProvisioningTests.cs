using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

/// <summary>
/// Covers the one operation a deploy cannot be trusted to perform in two steps: migrating the schema
/// and then provisioning the role, its grants, and its row-level security policies. The grant matrix
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
/// This file also pins the split that Entra authentication forces on provisioning. <b>How</b> the role
/// authenticates is now separate from <b>what</b> it may do: <c>ProvisionAsync</c> creates the role
/// credential-free — <c>LOGIN</c>, no password, no Entra label — and grants it its exact write surface,
/// and a credential is attached afterwards by whichever of the two paths applies.
/// <c>AttachAppRolePasswordAsync</c> is the dev and test path; <c>AttachAppRoleIdentityAsync</c> is the
/// production path and binds the role to a managed identity. Credential-free provisioning is only
/// correct if attaching a credential still works, so the tests below never assert the one without the
/// other: a role that exists and cannot be made loginable is a deploy that produces an API which
/// cannot start.
/// </para>
/// <para>
/// Attaching the identity is deliberately <i>not</i> part of <c>ProvisionAsync</c>, and this file does
/// not assert that it is. Forgetting it is the opposite of the RLS hazard above: it fails loudly at the
/// first login attempt rather than silently granting cross-tenant reads, so it does not need the
/// ordering guarantee that the grants and the policies do.
/// </para>
/// <para>
/// Every test here spins a <b>bare</b> <see cref="PostgreSqlContainer" /> rather than using
/// <c>RepositoryTestHost</c>. That is not a style preference: the host's <c>StartAsync</c> already
/// runs <c>MigrateAsync</c>, <c>ApplyGrantsAsync</c> and <c>AttachAppRolePasswordAsync</c>, so a test
/// built on it starts from a database that is already provisioned and could never observe "empty
/// database becomes a provisioned one" — which is the entire subject of this file. Two tests need no
/// container at all, and say so where they are.
/// </para>
/// <para>
/// The container account is a superuser and every schema observation below is made through it. That is
/// deliberate and not a privilege blind spot: <c>pg_class</c>, <c>pg_policy</c> and <c>pg_authid</c>
/// describe the cluster, and the cluster reads the same whoever asks. The exceptions are the
/// application role's own login attempts, which are the one fact only a non-superuser connection can
/// establish.
/// </para>
/// </remarks>
public sealed class DeploymentProvisioningTests
{
    /// <summary>
    /// Password these tests attach to the application role. A constant is fine: the container lives
    /// for one test and is unreachable from outside it. Every character is inside the alphabet
    /// <c>DatabaseProvisioning</c> permits, so a failure here is never about the password.
    /// </summary>
    private const string AppRolePassword = "deploy-test-password";

    /// <summary>
    /// A password carrying a single quote — the character that would close the SQL literal
    /// <c>ALTER ROLE ... WITH PASSWORD</c> splices it into, and the reason the alphabet check exists.
    /// </summary>
    private const string PasswordWithASingleQuote = "deploy'test";

    /// <summary>
    /// A table the baseline migration creates. Any migrated table would do; this one is named
    /// because it is also one of the budget-owned tables the policies below have to cover, so a
    /// database where it is missing fails this file in two places rather than one.
    /// </summary>
    private const string MigratedTable = "transactions";

    /// <summary>
    /// The budget-owned table whose protection the two verification tests sabotage. Any of the five
    /// would do; payees is picked because it is the one with no <c>DELETE</c> grant, so a reader
    /// tempted to conclude the tests only work on fully-granted tables is wrong.
    /// </summary>
    private const string SabotagedTable = "payees";

    /// <summary>
    /// A fixed object id standing in for the deployed container app's managed identity. Fixed rather
    /// than <c>Guid.NewGuid()</c> because the emitted SQL is pinned character for character, and a
    /// value that changed per run would make the expected string unwritable.
    /// </summary>
    private static readonly Guid AppIdentityObjectId =
        new("9f3ae1c4-5d27-4b8e-9a10-6c2f8d4e7b31");

    /// <summary>
    /// Address nothing listens on, used by the two tests whose whole claim is that a refusal happened
    /// <i>before</i> a connection was opened. Port 1 is privileged and unbound, so reaching a server
    /// through this string is not a race that could occasionally succeed.
    /// </summary>
    private const string UnreachableAdminConnectionString =
        "Host=127.0.0.1;Port=1;Username=postgres;Password=postgres;Database=budgetoid;Timeout=2";

    [Test]
    public async Task ProvisionAsync_OnEmptyDatabase_MigratesSchemaAndCreatesCredentialFreeRole()
    {
        // Arrange — an empty database: no schema, no migration history, no application role. This
        // is the state a first production deploy starts from and the only state in which "did
        // provisioning do the work" and "was the work already there" can be told apart.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        List<string> logLines = [];

        // Act
        await DeploymentDatabaseProvisioning.ProvisionAsync(
            container.GetConnectionString(),
            log: logLines.Add);

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        bool migratedTableExists = await TableExistsAsync(admin, MigratedTable);

        await using BudgetoidDbContext db = CreateDbContext(container);
        List<string> pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        // The role's shape, read out of pg_authid rather than inferred. This is the claim the Entra
        // migration adds: provisioning produces a role that is allowed to log in and has no
        // credential with which to do it. Both halves matter and neither implies the other — NOLOGIN
        // with a password set, and LOGIN with a password set, are both wrong here, and only one of
        // them is visible from a failed connection attempt.
        (bool roleExists, bool canLogin, bool hasNoPassword) = await ReadAppRoleAsync(admin);

        // The consequence, not a restatement: a role with no password cannot authenticate, and this
        // is what stops the assertions above from passing on a catalog that happens to say the right
        // thing about a role that is nonetheless reachable. 28P01 is password authentication failure.
        (string? refusedUser, string? refusedSqlState) =
            await TryLoginAsAppRoleAsync(container, AppRolePassword);

        // And the other half of the pairing, which is the point of the whole design: credential-free
        // provisioning is only correct if attaching a credential afterwards still produces a role the
        // application can connect as. Without this, "the role cannot log in" is satisfied by
        // provisioning that produced a permanently unusable role.
        await DatabaseProvisioning.AttachAppRolePasswordAsync(
            container.GetConnectionString(), AppRolePassword);
        (string? connectedAs, string? attachedSqlState) =
            await TryLoginAsAppRoleAsync(container, AppRolePassword);

        // Assert
        await Assert.That(migratedTableExists).IsTrue();
        await Assert.That(pending).IsEmpty();

        await Assert.That(roleExists).IsTrue();
        await Assert.That(canLogin).IsTrue();
        await Assert.That(hasNoPassword).IsTrue();

        await Assert.That(refusedUser).IsNull();
        await Assert.That(refusedSqlState).IsEqualTo(PostgresErrorCodes.InvalidPassword);

        await Assert.That(attachedSqlState).IsNull();
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
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using BudgetoidDbContext db = CreateDbContext(container);
        List<string> pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        await using NpgsqlConnection admin = await OpenAdminAsync(container);

        // Re-reading the role after the second run is what stops this from being a bare "it didn't
        // throw" test, and the credential-free split changes what the danger is. The re-run path
        // ALTERs an existing role instead of creating one, and an ALTER that reintroduced a password
        // clause — or dropped LOGIN — would leave the schema migrated, the call successful, and the
        // credential the deploy actually attached either overwritten or unusable.
        (bool roleExists, bool canLogin, bool hasNoPassword) = await ReadAppRoleAsync(admin);

        // Attaching after a converged re-run, for the same reason as on the first run: the deploy's
        // second step has to still work on the second deploy.
        await DatabaseProvisioning.AttachAppRolePasswordAsync(
            container.GetConnectionString(), AppRolePassword);
        (string? connectedAs, string? sqlState) =
            await TryLoginAsAppRoleAsync(container, AppRolePassword);

        // Assert
        await Assert.That(pending).IsEmpty();
        await Assert.That(roleExists).IsTrue();
        await Assert.That(canLogin).IsTrue();
        await Assert.That(hasNoPassword).IsTrue();
        await Assert.That(sqlState).IsNull();
        await Assert.That(connectedAs).IsEqualTo(DatabaseProvisioning.AppRoleName);
    }

    [Test]
    public async Task ProvisionAsync_PolicesEveryBudgetOwnedTable()
    {
        // Arrange
        await using PostgreSqlContainer container = await StartBareContainerAsync();

        // Act
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

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
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

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
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

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
    public async Task AttachAppRolePasswordAsync_InvalidPasswordAlphabet_ThrowsBeforeConnecting()
    {
        // Arrange — no container, on purpose. The claim is about ordering inside the method, and an
        // address nothing listens on is what makes the ordering observable: if validation runs first
        // the connection string is never used, and if it does not, the attempt to use it fails in a
        // way an ArgumentException cannot be mistaken for.
        //
        // "Before connecting" is the boundary that matters for the whole password path. ALTER ROLE
        // cannot take a bound parameter, so the password is spliced into a SQL literal, and the
        // alphabet check is the only thing standing between a typo'd deploy secret and a statement
        // that means something other than what it says.

        // Act
        ArgumentException? rejected = null;
        try
        {
            await DatabaseProvisioning.AttachAppRolePasswordAsync(
                UnreachableAdminConnectionString, PasswordWithASingleQuote);
        }
        catch (ArgumentException exception)
        {
            rejected = exception;
        }

        // The positive control, and this test is vacuous without it: an ArgumentException proves
        // ordering only if the same call with a legal password demonstrably does get as far as the
        // network and fail there. If both calls threw ArgumentException, the address would be
        // irrelevant and the test would prove nothing about when validation happens.
        Exception? reachedTheNetwork = null;
        try
        {
            await DatabaseProvisioning.AttachAppRolePasswordAsync(
                UnreachableAdminConnectionString, AppRolePassword);
        }
        catch (Exception exception)
        {
            reachedTheNetwork = exception;
        }

        // Assert
        await Assert.That(rejected).IsNotNull();
        await Assert.That(reachedTheNetwork).IsNotNull();
        await Assert.That(reachedTheNetwork is ArgumentException).IsFalse();
    }

    [Test]
    public async Task AttachAppRolePasswordAsync_InvalidPasswordAlphabet_LeavesTheRoleCredentialFree()
    {
        // Arrange — a provisioned database, so the role exists and the rejected call has something it
        // could have damaged. The previous test proves when the refusal happens; this one proves what
        // the refusal costs, which is the fact an operator actually depends on: a bad secret must
        // leave the role exactly as provisioning left it rather than half-credentialed.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        // Act
        ArgumentException? rejected = null;
        try
        {
            await DatabaseProvisioning.AttachAppRolePasswordAsync(
                container.GetConnectionString(), PasswordWithASingleQuote);
        }
        catch (ArgumentException exception)
        {
            rejected = exception;
        }

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        (_, bool canLogin, bool hasNoPassword) = await ReadAppRoleAsync(admin);

        // The positive control: the same method, same database, legal password, and it works. Without
        // it "the role still has no password" is equally satisfied by a method that never attaches
        // anything at all.
        await DatabaseProvisioning.AttachAppRolePasswordAsync(
            container.GetConnectionString(), AppRolePassword);
        (string? connectedAs, string? sqlState) =
            await TryLoginAsAppRoleAsync(container, AppRolePassword);

        // Assert
        await Assert.That(rejected).IsNotNull();
        await Assert.That(canLogin).IsTrue();
        await Assert.That(hasNoPassword).IsTrue();
        await Assert.That(sqlState).IsNull();
        await Assert.That(connectedAs).IsEqualTo(DatabaseProvisioning.AppRoleName);
    }

    [Test]
    public async Task BuildAppRoleIdentitySql_ForAKnownObjectId_LabelsTheRoleThenNullsThePassword()
    {
        // Arrange — no database and no container. Pinning the text is not a convenience here, it is
        // the only verification available anywhere but Azure: vanilla PostgreSQL has no pgaadauth
        // label provider, so the statement below cannot be executed in a container at all (see
        // AttachAppRoleIdentityAsync_RunsAgainstThePostgresDatabase, which proves the routing and
        // nothing more). Character-for-character is therefore the strongest claim obtainable locally,
        // and the emitted SQL is the whole of what Azure will be asked to run.
        string expectedLabel =
            $"""SECURITY LABEL for "pgaadauth" on role {DatabaseProvisioning.AppRoleName} """
            + $"is 'aadauth,oid={AppIdentityObjectId:D},type=service';";
        string expectedPasswordNull =
            $"ALTER ROLE {DatabaseProvisioning.AppRoleName} WITH PASSWORD NULL;";

        // Act
        string sql = DatabaseProvisioning.BuildAppRoleIdentitySql(AppIdentityObjectId);

        // Assert — both statements, and the label first. The order is load-bearing rather than
        // cosmetic: nulling the password before the label is attached would leave a window in which
        // the role has no credential of either kind, and on a re-provision that window is a
        // production API that cannot authenticate.
        await Assert.That(sql).Contains(expectedLabel);
        await Assert.That(sql).Contains(expectedPasswordNull);
        await Assert.That(sql.IndexOf(expectedLabel, StringComparison.Ordinal))
            .IsLessThan(sql.IndexOf(expectedPasswordNull, StringComparison.Ordinal));

        // type=service, spelled out separately from the whole-statement match above, because it is
        // the one token in the label whose value is a decision rather than an input. A managed
        // identity is a service principal; 'user' would make Azure look the object id up in the
        // wrong directory object class and reject a login that has nothing else wrong with it.
        await Assert.That(sql).Contains("type=service");

        // Guid "D" format — lowercase, hyphenated, unbraced — because that is the only rendering
        // pgaadauth accepts in the oid field. Asserting the formatted value rather than
        // AppIdentityObjectId.ToString() would be circular, so the literal is written out.
        await Assert.That(sql).Contains("oid=9f3ae1c4-5d27-4b8e-9a10-6c2f8d4e7b31,");

        // The reason the splice is safe, asserted rather than assumed. The object id is spliced into
        // a single-quoted SQL literal and the label grammar cannot take a bound parameter, so the
        // parameter being a Guid rather than a string is the whole defence: a Guid's D rendering is
        // 32 hex digits and four hyphens and can no more contain a quote than it can contain a
        // semicolon. The exact count of two is what pins that — the label literal's own delimiters
        // and nothing else in the emitted SQL is quoted.
        await Assert.That(AppIdentityObjectId.ToString("D").Contains('\'')).IsFalse();
        await Assert.That(sql.Count(character => character == '\'')).IsEqualTo(2);
    }

    [Test]
    public async Task AttachAppRoleIdentityAsync_RunsAgainstThePostgresDatabase()
    {
        // Arrange — the role, then the application database dropped out from under the connection
        // string. Azure exposes pgaadauth's functions and labels only in the postgres database, so
        // AttachAppRoleIdentityAsync has to rewrite the admin connection string's Database and keep
        // every other option, and dropping the database the string names is what makes the rewrite
        // observable rather than assumed. A method that used the string as given cannot reach a
        // server at all once budgetoid is gone.
        //
        // ProvisionAsync is not called: the label provider check fires before PostgreSQL resolves the
        // role, so the migrated schema would only make this test slower. The role is created anyway,
        // so the statement is as close to the real one as a non-Azure server can get.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await using NpgsqlConnection maintenance = await OpenAdminOnPostgresDatabaseAsync(container);
        await ExecuteAsync(
            maintenance, $"create role {DatabaseProvisioning.AppRoleName} with login");
        await ExecuteAsync(maintenance, "drop database budgetoid with (force)");

        // The positive control, taken first so that the failure below cannot be read charitably: the
        // connection string handed to the method is genuinely unusable as written, and says so with
        // 3D000, invalid_catalog_name.
        PostgresException? applicationDatabaseGone = null;
        try
        {
            await using NpgsqlConnection asWritten = new(container.GetConnectionString());
            await asWritten.OpenAsync();
        }
        catch (PostgresException exception)
        {
            applicationDatabaseGone = exception;
        }

        // Act
        PostgresException? labelFailure = null;
        try
        {
            await DatabaseProvisioning.AttachAppRoleIdentityAsync(
                container.GetConnectionString(), AppIdentityObjectId);
        }
        catch (PostgresException exception)
        {
            labelFailure = exception;
        }

        // Assert — routing only. The label statement itself is exercisable on Azure and nowhere else,
        // so the honest claim here is that the statement reached a live server through a database the
        // rewrite chose, and 22023 is what proves it: invalid_parameter_value carrying
        // 'security label provider "pgaadauth" is not loaded' is a server-side rejection of the
        // statement, which cannot be produced without a completed connection and authentication. It
        // is not confusable with the control's 3D000, and neither is it confusable with a socket
        // failure, which would not be a PostgresException at all.
        await Assert.That(applicationDatabaseGone).IsNotNull();
        await Assert.That(applicationDatabaseGone!.SqlState)
            .IsEqualTo(PostgresErrorCodes.InvalidCatalogName);

        await Assert.That(labelFailure).IsNotNull();
        await Assert.That(labelFailure!.SqlState)
            .IsEqualTo(PostgresErrorCodes.InvalidParameterValue);
        await Assert.That(labelFailure.MessageText).Contains("pgaadauth");
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
    /// Opens the same superuser connection against the cluster's <c>postgres</c> database instead of
    /// the application one, which is where a session has to be in order to drop the application
    /// database from under it.
    /// </summary>
    /// <remarks>
    /// This helper is not a stand-in for the rewrite <c>AttachAppRoleIdentityAsync</c> performs, and
    /// the identity test does not use it for that. The test asks whether the production code rewrites
    /// the database on its own; a helper that did the rewrite for it would make the question
    /// unanswerable.
    /// </remarks>
    private static async Task<NpgsqlConnection> OpenAdminOnPostgresDatabaseAsync(
        PostgreSqlContainer container)
    {
        NpgsqlConnection connection = new(
            new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                Database = "postgres",
            }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// Attempts a real login as the least-privilege application role and reports either the
    /// <c>current_user</c> the server acknowledged or the SQLSTATE it refused with.
    /// </summary>
    /// <remarks>
    /// Pooling is switched off so that every call is a genuine authentication round trip. With the
    /// pool on, a login taken after a credential changed could be answered out of a connection
    /// established under the previous one, and a test asserting that an attach took effect would be
    /// reading a cached success.
    /// </remarks>
    private static async Task<(string? ConnectedAs, string? SqlState)> TryLoginAsAppRoleAsync(
        PostgreSqlContainer container,
        string password)
    {
        string connectionString =
            new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                Username = DatabaseProvisioning.AppRoleName,
                Password = password,
                Pooling = false,
            }.ConnectionString;

        try
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand whoami = new("select current_user", connection);
            return ((string?)await whoami.ExecuteScalarAsync(), null);
        }
        catch (PostgresException exception)
        {
            return (null, exception.SqlState);
        }
    }

    /// <summary>
    /// Reads whether the application role exists, may log in, and has no password, from
    /// <c>pg_authid</c> in one row.
    /// </summary>
    /// <remarks>
    /// <c>pg_authid</c> rather than <c>pg_roles</c> because only the former exposes
    /// <c>rolpassword</c>, and "the role was created without a credential" is the fact the Entra
    /// migration turns into a contract. It is superuser-only, which the container account is. Reading
    /// all three in one row keeps them from drifting into separate observations that disagree about
    /// which role they described.
    /// </remarks>
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
    /// matched case-sensitively against <c>pg_class</c>, which is what lets a mixed-case name like
    /// <c>__EFMigrationsHistory</c> be looked up the way EF quotes it.
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
    /// a name list is the filter. <c>budgets</c>, <c>users</c>, <c>currencies</c>,
    /// <c>credentials</c> and <c>__EFMigrationsHistory</c> drop out for free: a budget is the tenant
    /// rather than a tenant's row, and none of the other four belongs to one. <c>relrowsecurity</c>
    /// is read in the same row as the discovery so that "is this table budget-owned" and "is it
    /// protected" cannot drift into two lists that disagree. This and <c>RlsCoverageTests</c> ask
    /// different questions of the catalog: here it is "which tables are budget-owned, so I can
    /// sabotage one", there it is "is every table in the schema accounted for". Both files need to
    /// observe the fact from outside the code that establishes it, and a shared helper would make
    /// one test's subject the other's fixture.
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
    /// Sends one statement that is expected to succeed. Used only for admin-side setup and sabotage,
    /// where the SQL is built from constants of this class rather than from input.
    /// </summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
