using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

/// <summary>
/// Covers the one operation a deploy cannot be trusted to perform in two steps: migrating the schema
/// and then provisioning the role, its grants, and its row-level security policies. A missing grant
/// is fail-closed: it stops a feature dead with <c>42501</c>. Row-level security is fail-open: a
/// granted table with no policy is readable and writable by the application role across every tenant,
/// silently and indistinguishably from working. A deploy that migrates and forgets to provision is
/// therefore a tenancy breach nothing reports, which is why the ordering belongs in code and why
/// <c>VerifyRowLevelSecurityCoverageAsync</c> exists at all — and why it is named for row-level
/// security rather than for provisioning as a whole. It does not check the grants. An extra grant is
/// the other fail-open half, silent in the same way, and it is <c>VerifyAppRoleReachAsync</c>'s: its
/// sabotages are in this file too, one rule at a time.
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
    /// The user-owned table whose policy the users sabotage test drops. Unlike
    /// <see cref="SabotagedTable" /> this one is not interchangeable with its neighbours: it is the
    /// only table in the schema that is owned by a tenant without carrying an ownership column, so it
    /// is the one table a discovery query keyed on a column can miss entirely.
    /// </summary>
    private const string UsersTable = "users";

    /// <summary>
    /// The tables that own rows on behalf of a person and are nonetheless deliberately unpoliced,
    /// written down here so the discovery below can excuse them by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All four carry <c>user_id</c>, so a query that asked only "does this table own rows" would
    /// demand a policy none of them may have. <c>credentials</c> is read to answer "who is asking",
    /// and a policy keyed on that answer would refuse the question that produces it.
    /// <c>passkey_public_keys</c> is read to decide whether the signature on an assertion is genuine,
    /// which a WebAuthn ceremony has to settle before it knows whose account it is.
    /// <c>recovery_code_hashes</c> is found by the SHA-256 of the verifier a person typed, on a
    /// request that has said nothing about who they are, and a policy keyed on
    /// <c>app.current_user_id</c> would refuse the very query that establishes the identity — refuse
    /// it loudly, because an unset setting reaches the policy as <c>''::uuid</c> and raises
    /// <c>22P02</c>, so the failure would be every redemption in production rather than a leak.
    /// <c>session_tokens</c> is found by the SHA-256 of the token a cookie presented, on a request that
    /// has said nothing about who it is, and a policy there would refuse the very query that produces
    /// the identity it wants to compare against — on every authenticated request rather than on a
    /// redemption. Each is read before the request has an identity a policy could be keyed on; all four
    /// reasons are argued in docs/decisions/0011 and docs/decisions/0012.
    /// </para>
    /// <para>
    /// The third and fourth names are written out above rather than cited, and the paragraph below is why that
    /// matters more than it looks. <c>RowLevelSecurityCoverage.Exemptions</c> also carries a reason for
    /// <c>recovery_code_hashes</c>; pointing at it — "excused because the shared list excuses it" —
    /// would make this entry an assertion about the code under test, which is the one thing this
    /// literal exists not to be. The reason has to stand on its own here, so that a wrong entry in
    /// that list and a wrong entry here are two mistakes a person has to make separately.
    /// </para>
    /// <para>
    /// Spelled out here rather than read from that list, and this is the same argument the policy
    /// name constants above make. ADR 0011's objection to two lists is an objection about production
    /// code paths, where both lists enforce and the loser of a disagreement fails open. A test is not
    /// a second enforcer; it is an oracle, and an oracle that asks the code under test what to expect
    /// agrees with it by construction and can never fail. Reading the shared list would mean that
    /// appending a name to it silently removes that table from this test's subjects, leaving the
    /// exemption list with no adversarial reader anywhere in the suite — a wrongly-granted exemption
    /// on a <c>user_id</c>-carrying table would go green here while <c>RlsCoverageTests</c>, which
    /// also reads the list, went green too.
    /// </para>
    /// <para>
    /// The duplication fails <b>closed</b>. A name added to the shared list and not added here leaves
    /// the table a subject of this test, so it goes red until somebody writes the name down twice, on
    /// purpose. The failure asks for a decision rather than granting one.
    /// </para>
    /// <para>
    /// Only tenant-owned exemptions belong in it, which is why there are four names here and seven in
    /// the shared list. <c>currencies</c>, <c>webauthn_challenges</c> and <c>__EFMigrationsHistory</c>
    /// carry neither ownership column, so the query's own shape predicate already excludes them;
    /// naming them here would turn an independent statement into a copy of a list and invite somebody
    /// to keep the two mechanically in sync. A future exemption on a table with no ownership column
    /// therefore stays green, because it was never a subject.
    /// </para>
    /// <para>
    /// The counts in the sentence above are a description, not a check. Nothing fails when they drift,
    /// and nothing should — a test that compared the two lengths would be reading the shared list
    /// again by the back door. They are here because a reader arriving at four names and seven
    /// exemptions needs to know the gap is expected.
    /// </para>
    /// </remarks>
    private static readonly string[] UnpolicedUserOwnedTables =
    [
        "credentials",
        "passkey_public_keys",
        "recovery_code_hashes",

        // The fourth name, written out a second time by a person on purpose — the duplication above is
        // the design, not a defect somebody should resolve by reading the shared list. Its own reason,
        // stated here so that a wrong entry there and a wrong entry here stay two separate mistakes: a
        // presented session token is looked up before the request has said who it is, and
        // user_isolation is keyed on app.current_user_id, which is exactly the value that lookup
        // exists to produce. A policy here would refuse the query it wants to compare against, and
        // refuse it loudly — an unset setting reaches the policy as ''::uuid and raises 22P02 — so the
        // failure would be every authenticated request in the product rather than a leak. It carries
        // user_id, so without this name the discovery below would demand a policy of it and go red
        // with nothing wrong.
        "session_tokens",
    ];

    /// <summary>
    /// The policy name a budget-owned table owes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as a literal rather than read from <c>RowLevelSecurityCoverage</c>, and this is the
    /// same argument the discovery helper below already makes for itself. A test that asked the code
    /// under test which policy name it requires would agree with that code by construction: rename
    /// the constant in production and the expectation renames itself, so the assertion could never
    /// fail. Duplicating two short strings is the price of an expectation that is independently
    /// stated, and it is a price this file already pays — the missing-policy test has spelled
    /// <c>budget_isolation</c> into its sabotage SQL since it was written.
    /// </para>
    /// <para>
    /// The duplication is also self-announcing rather than silent: change the name in
    /// <c>app-role-grants.sql</c> without changing it here and these tests go red immediately, which
    /// is exactly the conversation a rename ought to start.
    /// </para>
    /// </remarks>
    private const string BudgetIsolationPolicy = "budget_isolation";

    /// <summary>
    /// The policy name a user-owned table owes. A literal for the reason given on
    /// <see cref="BudgetIsolationPolicy" />.
    /// </summary>
    private const string UserIsolationPolicy = "user_isolation";

    /// <summary>
    /// The name a policy is renamed to by the rename sabotage. Anything not equal to either isolation
    /// policy name would do; it is spelled out so the failure message reads as a rename rather than as
    /// an unrelated policy someone added.
    /// </summary>
    private const string RenamedPolicy = "budget_isolation_v2";

    /// <summary>
    /// A table created by the unclassifiable sabotage: in <c>public</c>, ordinary, and carrying
    /// neither ownership column. The name is one nobody would mistake for a migration's output.
    /// </summary>
    private const string UnclassifiableTable = "sabotage_unclassified";

    /// <summary>
    /// The budget-owned table the command-narrowing sabotage aims at. Not interchangeable with
    /// <see cref="SabotagedTable" />: the role holds
    /// <c>UPDATE (name, name_key, type, opening_balance)</c> <b>and</b> <c>DELETE</c> on accounts,
    /// so narrowing its policy to <c>FOR SELECT</c> costs something nameable. <c>name</c> and
    /// <c>name_key</c> travel as a pair — a grant naming one without the other forbids the rename
    /// both columns exist to serve. On payees, which has no <c>DELETE</c> grant, the same narrowing
    /// would still be wrong but the demonstration would be thinner.
    /// </summary>
    private const string GrantedForWriteTable = "accounts";

    /// <summary>
    /// A view created over a policed table by the view sabotage. Named so that nothing mistakes it
    /// for one of the migration's relations.
    /// </summary>
    private const string SabotageView = "sabotage_transactions_view";

    /// <summary>
    /// The predicate a budget-owned table's policy actually carries, written out so the sabotages
    /// below can recreate a policy that is wrong in exactly one respect and right in every other.
    /// </summary>
    /// <remarks>
    /// A literal here for the reason <see cref="BudgetIsolationPolicy" /> gives: a sabotage that
    /// read its "correct" predicate out of <c>app-role-grants.sql</c> would be correct by
    /// construction, and these tests need to state independently what right looks like so that
    /// "wrong in one respect" is a claim rather than a tautology.
    /// </remarks>
    private const string BudgetOwnershipPredicate =
        "budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid";

    /// <summary>
    /// The same shape keyed on the wrong session setting: a budget-owned column compared against
    /// the user the session authenticated as.
    /// </summary>
    private const string BudgetColumnKeyedOnTheUserSetting =
        "budget_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid";

    /// <summary>
    /// A predicate that reads the correct session setting and names no ownership column at all: it
    /// asserts only that somebody is signed in.
    /// </summary>
    private const string SignedInButOwnershipFreePredicate =
        "COALESCE(current_setting('app.current_user_id', true), '')::uuid IS NOT NULL";

    /// <summary>
    /// Matches <c>id</c> as a whole word. Used by the users sabotage to state, as an assertion
    /// rather than as a claim in a comment, that the sabotaged predicate contains the letters
    /// <c>id</c> and yet names no column called <c>id</c>.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex StandaloneIdWord = new(@"\bid\b");

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

    /// <summary>
    /// The database privileges the application role is meant to hold, and all of them: CONNECT, and
    /// nothing else. It arrives through PUBLIC rather than through a grant, and stays there by
    /// decision — revoking CONNECT from PUBLIC would also shut out every principal that relies on it.
    /// </summary>
    /// <remarks>
    /// A literal for the reason <see cref="BudgetIsolationPolicy" /> gives: an expectation read out of
    /// <c>app-role-grants.sql</c> would agree with the script by construction.
    /// </remarks>
    private static readonly string[] DeclaredDatabasePrivileges = ["CONNECT"];

    /// <summary>
    /// Every privilege a database has, in the spelling <c>has_database_privilege</c> takes, plus each
    /// one's grant option. The grant options are asked about separately because holding one is a
    /// second privilege — the power to hand the first to somebody else.
    /// </summary>
    private static readonly string[] DatabasePrivileges =
    [
        "CONNECT",
        "CREATE",
        "TEMPORARY",
        "CONNECT WITH GRANT OPTION",
        "CREATE WITH GRANT OPTION",
        "TEMPORARY WITH GRANT OPTION",
    ];

    /// <summary>
    /// A database name no line of the grant script could spell, so a script that hard-coded the
    /// container's <c>budgetoid</c> revokes on the wrong database.
    /// </summary>
    private const string RenamedDatabase = "deploy_target";

    /// <summary>What the missing-role sabotage renames the application role to.</summary>
    private const string RenamedAppRole = "budgetoid_app_renamed_away";

    /// <summary>
    /// A predefined role whose membership reaches every table at once, used by the membership
    /// sabotage.
    /// </summary>
    private const string PredefinedWriteRole = "pg_write_all_data";

    /// <summary>A table the ownership sabotage creates and hands to the application role.</summary>
    private const string AppOwnedTable = "sabotage_owned";

    /// <summary>
    /// The table the PUBLIC-grant sabotages aim at. budgets, because its UPDATE grant is one column
    /// wide, so any wider UPDATE on it is a widening by definition.
    /// </summary>
    private const string PublicGrantTable = "budgets";

    /// <summary>
    /// A budgets column immutable by omission from the script's column list, used by the
    /// column-level sabotages. Distinctive enough that a problem naming it cannot do so by accident.
    /// </summary>
    private const string ImmutableColumn = "base_currency_code";

    /// <summary>A superuser-only parameter the parameter-grant sabotage grants SET on.</summary>
    private const string GrantedParameter = "session_replication_role";

    /// <summary>A function created with no grant, so its ACL is NULL.</summary>
    private const string DefaultCallableFunction = "sabotage_callable";

    /// <summary>A function whose EXECUTE is taken from PUBLIC and granted to the role by name.</summary>
    private const string GrantedCallableFunction = "sabotage_granted_callable";

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
    public async Task ProvisionAsync_PolicesEveryTenantOwnedTable()
    {
        // Arrange
        await using PostgreSqlContainer container = await StartBareContainerAsync();

        // Act
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);

        // The table list is derived from the live schema, never written down. A hardcoded list of
        // the known names would keep passing on the day someone adds the next one, which is the
        // only day this assertion matters. "Tenant-owned" rather than "budget-owned" because a
        // budget is not the only tenant any more: a user is one too, and a table owned by a person
        // is no less of a breach for being reachable through the wrong kind of key.
        IReadOnlyList<(string Table, bool RowSecurityEnabled, string RequiredPolicy)> tables =
            await DiscoverTenantOwnedTablesAsync(admin);
        List<string> unprotected = tables
            .Where(entry => !entry.RowSecurityEnabled)
            .Select(entry => entry.Table)
            .ToList();

        List<string> wronglyPoliced = [];
        foreach ((string table, _, string requiredPolicy) in tables)
        {
            (int total, int matching) = await CountIsolationPoliciesAsync(admin, table, requiredPolicy);

            // Exactly one, not at least one. These policies are permissive and permissive policies
            // OR together, so a second one can only widen what the first allows — a table that grew
            // a stray policy has quietly stopped meaning what its isolation policy says.
            if (total != 1)
            {
                wronglyPoliced.Add($"{table}: {total} policies, wanted 1");
            }

            // And the one policy has to be the rule this table's ownership calls for, not merely a
            // rule. A count answers "is something enforced here"; only the name answers "is the
            // thing enforced here the thing this table owes". A user-keyed policy on a budget-owned
            // table is a real, enforced policy — and wider than the tenancy the table is supposed
            // to have, because a budget belongs to exactly one user but a user owns many budgets.
            if (matching != 1)
            {
                wronglyPoliced.Add($"{table}: {matching} policies named {requiredPolicy}, wanted 1");
            }
        }

        // Assert — the non-empty check comes first and is not decoration. If the discovery query
        // silently matched nothing, both lists below would be empty and every remaining assertion
        // would pass with nothing in it, on a database with no policies at all.
        await Assert.That(tables).IsNotEmpty();
        await Assert.That(unprotected).IsEmpty();
        await Assert.That(wronglyPoliced).IsEmpty();

        // Both ownership shapes are actually present in what was discovered, which is what stops
        // this test from silently narrowing back to the budget-keyed half it grew out of. Without
        // it, a discovery query that lost its user_id branch would still pass every line above.
        await Assert.That(tables.Select(entry => entry.RequiredPolicy).Distinct().Order().ToList())
            .IsEquivalentTo(new[] { BudgetIsolationPolicy, UserIsolationPolicy });
    }

    [Test]
    public async Task ProvisionAsync_LeavesTheAppRoleExactlyTheDeclaredDatabasePrivileges()
    {
        // Arrange — a database created here under a name no line of the grant script could spell,
        // and provisioned instead of the container's own. The container's database is called
        // budgetoid, so on it a script that hard-coded REVOKE ... ON DATABASE budgetoid would pass
        // for one that names current_database(); on this one it revokes on the wrong database and
        // leaves TEMPORARY standing here.
        //
        // The new database's ACL is NULL, which is the state that hands PUBLIC both CONNECT and
        // TEMPORARY. It is made from template1 and is not a copy of template1's ACL — measured on
        // postgres:17: template1 carries {=c/postgres,postgres=CTc/postgres}, a database created
        // from it carries NULL, because CREATE DATABASE does not copy the template's ACL. The
        // precondition is read rather than trusted.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await using (NpgsqlConnection maintenance = await OpenAdminOnPostgresDatabaseAsync(container))
        {
            await ExecuteAsync(maintenance, $"create database {RenamedDatabase}");
        }

        string targetConnectionString = BuildConnectionStringFor(container, RenamedDatabase);
        await using NpgsqlConnection admin = new(targetConnectionString);
        await admin.OpenAsync();
        bool aclWasDefault = await DatabaseAclIsDefaultAsync(admin);

        // Act
        await DeploymentDatabaseProvisioning.ProvisionAsync(targetConnectionString);

        bool provisionedHere = await TableExistsAsync(admin, MigratedTable);

        // Effective privileges, not grants. has_database_privilege folds in what PUBLIC holds, which
        // is the whole subject: the role holds TEMPORARY through PUBLIC unless the script takes it
        // from PUBLIC, and a read of the role's own grants would call that clean.
        IReadOnlyList<string> held =
            await ReadDatabasePrivilegesAsync(admin, DatabaseProvisioning.AppRoleName);
        List<string> unexpected = held.Except(DeclaredDatabasePrivileges).ToList();
        List<string> missing = DeclaredDatabasePrivileges.Except(held).ToList();

        // Where CONNECT comes from, which the effective set cannot tell. REVOKE ALL FROM PUBLIC plus
        // GRANT CONNECT TO budgetoid_app leaves the role holding exactly CONNECT, and it is not the
        // decision: CONNECT stays with PUBLIC so every principal that connects by the default keeps
        // connecting, and the role holds no grant of its own on the database. So PUBLIC is read the
        // same way the role is, and the role's own ACL entries are read raw.
        IReadOnlyList<string> heldByPublic = await ReadDatabasePrivilegesAsync(admin, "public");
        List<string> publicUnexpected = heldByPublic.Except(DeclaredDatabasePrivileges).ToList();
        List<string> publicMissing = DeclaredDatabasePrivileges.Except(heldByPublic).ToList();
        IReadOnlyList<string> directGrants = await ReadAppRoleDirectDatabaseGrantsAsync(admin);

        // The consequence, read on the connection the application actually uses. It is the same
        // server-side check as the catalog read above, so it is not a second witness to the ACL; it
        // is here because it pins that the database this test reads is the database the role
        // connects to, and because a temporary table is the thing NFR-006 is actually about — a role
        // that can make one has a place to put rows no grant in this repository describes.
        await DatabaseProvisioning.AttachAppRolePasswordAsync(
            targetConnectionString, AppRolePassword);
        string? tempTableSqlState = await TryCreateTempTableAsAppRoleAsync(targetConnectionString);

        // Assert — the preconditions first, so a red below cannot be a database that started out
        // already locked down, or a provisioning run that went to the container's own database.
        // Then each direction as its own named list, so a failure says which privilege is extra and
        // which is gone rather than that two sets differ.
        await Assert.That(aclWasDefault).IsTrue();
        await Assert.That(provisionedHere).IsTrue();
        await Assert.That(unexpected).IsEmpty();
        await Assert.That(missing).IsEmpty();
        await Assert.That(publicUnexpected).IsEmpty();
        await Assert.That(publicMissing).IsEmpty();
        await Assert.That(directGrants).IsEmpty();

        // 42501, insufficient_privilege. Measured: "permission denied to create temporary tables in
        // database".
        await Assert.That(tempTableSqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_OnAFreshlyProvisionedDatabase_DoesNotThrow()
    {
        // Arrange — provisioning and nothing else. This is the control for every refusal below, and
        // it is green on a verifier that checks nothing; that is expected. What it stops is the
        // opposite cheat: a verifier that refuses everything would turn every sabotage test green,
        // and only a clean database that it must accept separates "found the widening" from
        // "refuses whatever it is shown". It also pins that the rules below are not tripped by
        // something provisioning itself leaves behind.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — nothing thrown at all, rather than no AppRoleReachException in particular: any
        // refusal of a clean database is the failure this test exists to catch.
        await Assert.That(caught).IsNull();
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    [Arguments("BYPASSRLS", "rolbypassrls")]
    [Arguments("CREATEDB", "rolcreatedb")]
    [Arguments("CREATEROLE", "rolcreaterole")]
    [Arguments("REPLICATION", "rolreplication")]
    [Arguments("SUPERUSER", "rolsuper")]
    public async Task VerifyAppRoleReachAsync_AppRoleGivenAnElevatedAttribute_ThrowsNamingTheAttribute(
        string attribute,
        string catalogColumn)
    {
        // Arrange — one role attribute switched on. None of these is a grant, so nothing the grant
        // script REVOKEs touches it. Nor could the script count on switching one off: measured on
        // postgres:17.10, a CREATEROLE admin can take CREATEROLE off a role it administers but
        // CREATEDB, REPLICATION or BYPASSRLS only when it holds that attribute itself, and no role
        // but a superuser can alter a role that has SUPERUSER. BYPASSRLS is the sharp one: every
        // isolation policy stays present, enabled and correct in the catalog, and none of them
        // applies to the role any more.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"alter role {DatabaseProvisioning.AppRoleName} {attribute}");
        bool attributeSet = await ReadAppRoleFlagAsync(admin, catalogColumn);

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the sabotage took, then the refusal names the attribute by its SQL keyword.
        await Assert.That(attributeSet).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, attribute)).IsNotEmpty();
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_AppRoleUnableToLogIn_ThrowsNamingLogin()
    {
        // Arrange — NOLOGIN. The script converges this one on a re-run (ALTER ROLE ... WITH LOGIN),
        // so it is the verifier called directly that has to see it: a role that cannot log in is not
        // the role the API connects as, and a snapshot describing it describes the wrong thing.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"alter role {DatabaseProvisioning.AppRoleName} nologin");
        bool canLogin = await ReadAppRoleFlagAsync(admin, "rolcanlogin");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — "LOGIN" covers both spellings an implementer would reach for, NOLOGIN and LOGIN.
        await Assert.That(canLogin).IsFalse();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, "LOGIN")).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_AppRoleMissing_ThrowsNamingTheRole()
    {
        // Arrange — provisioned, then the role renamed away, so no role called budgetoid_app exists.
        // Discovery then reads null attributes and empty lists everywhere, and an implementation that
        // reads "nothing found" as "nothing wrong" certifies a role that is not there.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin, $"alter role {DatabaseProvisioning.AppRoleName} rename to {RenamedAppRole}");
        (bool roleExists, _, _) = await ReadAppRoleAsync(admin);

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the missing-role sentence itself, and nothing beside it. The role's name alone
        // would be satisfied by any other rule's sentence, since every one names the role; and a
        // second problem would mean the verifier went on to judge empty lists as if a role were
        // there, which is the "nothing found" reading this test exists to refuse.
        await Assert.That(roleExists).IsFalse();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsNaming(caught, $"No role named {DatabaseProvisioning.AppRoleName} exists"))
            .IsNotEmpty();
        await Assert.That(((AppRoleReachException)caught!).Problems.Count).IsEqualTo(1);
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_AppRoleMadeAMemberOfAPredefinedRole_ThrowsNamingTheRole()
    {
        // Arrange — membership in pg_write_all_data, which carries INSERT, UPDATE and DELETE on every
        // table in the database. Membership is not a privilege on any table, so the script's
        // per-table REVOKE ALL leaves it standing and every column-list grant in the file becomes
        // decoration. The rule reads pg_auth_members.member = the role; the reverse direction is the
        // creator's automatic membership on PostgreSQL 16+ and is pinned harmless in
        // NonSuperuserDeploymentProvisioningTests.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin, $"grant {PredefinedWriteRole} to {DatabaseProvisioning.AppRoleName}");
        bool isMember = await ScalarBoolAsync(
            admin,
            $"select pg_has_role('{DatabaseProvisioning.AppRoleName}', '{PredefinedWriteRole}', "
            + "'MEMBER')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(isMember).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, PredefinedWriteRole)).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_CreateOnSchemaPublic_ThrowsNamingTheSchemaAndCreate()
    {
        // Arrange — CREATE on the schema. The script only ever GRANTs USAGE there and never REVOKEs,
        // so this survives every re-run, and it hands the role a place to make tables it owns — and
        // an owner is not subject to row-level security.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin, $"grant create on schema public to {DatabaseProvisioning.AppRoleName}");
        bool holdsCreate = await ScalarBoolAsync(
            admin,
            $"select has_schema_privilege('{DatabaseProvisioning.AppRoleName}', 'public', 'CREATE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(holdsCreate).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, "public", "CREATE")).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_CreateOnAnotherSchema_ThrowsNamingTheSchema()
    {
        // Arrange — CREATE on a schema the grant script never names. The schema rule reads every row
        // of pg_namespace rather than public alone, and a rule narrowed to the one schema the script
        // mentions passes this: the role can make tables it owns here just as well, and an owner is
        // not subject to row-level security wherever the table lives.
        const string schema = "sabotage_other_schema";
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"create schema {schema}");
        await ExecuteAsync(
            admin, $"grant create on schema {schema} to {DatabaseProvisioning.AppRoleName}");
        bool holdsCreate = await ScalarBoolAsync(
            admin,
            $"select has_schema_privilege('{DatabaseProvisioning.AppRoleName}', '{schema}', "
            + "'CREATE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — "Schema <name>" rather than the bare name, so the sentence has to be the schema
        // rule's and not some other rule that happens to mention it.
        await Assert.That(holdsCreate).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, $"Schema {schema}", "CREATE")).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_UsageOnSchemaPublicWithGrantOption_ThrowsNamingTheGrantOption()
    {
        // Arrange — the USAGE the script grants, re-granted WITH GRANT OPTION. The privilege itself
        // is declared, so a rule reading only "which privileges" calls this clean; the grant option
        // is the widening — the role may hand the schema to anybody. The script's plain GRANT USAGE
        // does not take the option back.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin,
            $"grant usage on schema public to {DatabaseProvisioning.AppRoleName} with grant option");
        bool holdsGrantOption = await ScalarBoolAsync(
            admin,
            $"select has_schema_privilege('{DatabaseProvisioning.AppRoleName}', 'public', "
            + "'USAGE WITH GRANT OPTION')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the schema, the privilege, and the words "grant option" in any case.
        await Assert.That(holdsGrantOption).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsWhere(
                    caught,
                    problem => problem.Contains("public", StringComparison.Ordinal)
                        && problem.Contains("USAGE", StringComparison.Ordinal)
                        && problem.Contains("grant option", StringComparison.OrdinalIgnoreCase)))
            .IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_DefaultPrivilegesGrantingTheAppRole_ThrowsNamingTheDefault()
    {
        // Arrange — a default privilege: it grants nothing that exists today and everything the
        // schema owner creates next. The next migration's table then arrives with ALL for the role,
        // column lists and all, before anybody writes a line of the grant script for it.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin,
            "alter default privileges in schema public grant all on tables to "
            + DatabaseProvisioning.AppRoleName);
        int defaultAclRows = await CountDefaultAclRowsAsync(admin);

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — "default" in any case, the grantee, and the schema the entry is limited to, in
        // the clause the remedy has to carry: an ALTER DEFAULT PRIVILEGES … REVOKE without
        // IN SCHEMA public names a different pg_default_acl row and leaves this one standing
        // (measured on postgres:17.10).
        await Assert.That(defaultAclRows).IsEqualTo(1);
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsWhere(
                    caught,
                    problem => problem.Contains("default", StringComparison.OrdinalIgnoreCase)
                        && problem.Contains(
                            DatabaseProvisioning.AppRoleName, StringComparison.Ordinal)
                        && problem.Contains("IN SCHEMA public", StringComparison.Ordinal)))
            .IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_GlobalDefaultPrivilege_ThrowsNamingTheDefault()
    {
        // Arrange — a default privilege with no IN SCHEMA, which applies to tables the creator makes
        // in any schema. pg_default_acl stores it with defaclnamespace 0, so a rule that joined
        // pg_namespace with an inner join would drop the row, and a remedy that named a schema would
        // leave it standing (measured on postgres:17.10: the IN SCHEMA public REVOKE ran, the row
        // stayed, and the next table created answered SELECT for the role).
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin,
            $"alter default privileges grant select on tables to {DatabaseProvisioning.AppRoleName}");
        bool entryIsGlobal = await ScalarBoolAsync(
            admin, "select bool_and(defaclnamespace = 0) and count(*) = 1 from pg_default_acl");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the default and the role are named, and no schema is, in either case.
        await Assert.That(entryIsGlobal).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsWhere(
                    caught,
                    problem => problem.Contains("default", StringComparison.OrdinalIgnoreCase)
                        && problem.Contains(
                            DatabaseProvisioning.AppRoleName, StringComparison.Ordinal)
                        && !problem.Contains("IN SCHEMA", StringComparison.OrdinalIgnoreCase)))
            .IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_DefaultPrivilegesGrantingPublic_ThrowsNamingTheDefault()
    {
        // Arrange — the same default privilege aimed at PUBLIC, which the role inherits. A rule that
        // filters pg_default_acl on the role's own name misses it.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin, "alter default privileges in schema public grant select on tables to public");
        int defaultAclRows = await CountDefaultAclRowsAsync(admin);

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — PUBLIC in capitals, the grantee's spelling, so the schema name "public" cannot
        // satisfy it.
        await Assert.That(defaultAclRows).IsEqualTo(1);
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsWhere(
                    caught,
                    problem => problem.Contains("default", StringComparison.OrdinalIgnoreCase)
                        && problem.Contains("PUBLIC", StringComparison.Ordinal)))
            .IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_TableOwnedByTheAppRole_ThrowsNamingTheTable()
    {
        // Arrange — a table handed to the role. An owner is not subject to row-level security and
        // may grant itself anything on what it owns, and REVOKE ALL ... FROM budgetoid_app does not
        // take ownership away. The table is outside the grant script, so the script never sees it.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"create table public.{AppOwnedTable} (id uuid primary key)");
        await ExecuteAsync(
            admin,
            $"alter table public.{AppOwnedTable} owner to {DatabaseProvisioning.AppRoleName}");
        bool ownedByTheRole = await ScalarBoolAsync(
            admin,
            $"select pg_get_userbyid(relowner) = '{DatabaseProvisioning.AppRoleName}' "
            + $"from pg_class where oid = 'public.{AppOwnedTable}'::regclass");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(ownedByTheRole).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, AppOwnedTable)).IsNotEmpty();
    }

    [Test]
    [Arguments("UPDATE on a table")]
    [Arguments("TRUNCATE on a table")]
    [Arguments("DELETE on a table")]
    [Arguments("INSERT on a table")]
    [Arguments("TRIGGER on a table")]
    [Arguments("REFERENCES on a table")]
    [Arguments("MAINTAIN on a table")]
    [Arguments("SELECT on a view")]
    [Arguments("USAGE on a sequence")]
    public async Task VerifyAppRoleReachAsync_PrivilegeOnARelationGrantedToPublic_ThrowsNamingTheRelation(
        string sabotage)
    {
        // Arrange — a privilege on a relation in public, to PUBLIC. The script's REVOKE ALL … FROM
        // budgetoid_app does not reach PUBLIC, and the role inherits PUBLIC. One row per privilege a
        // table has (MAINTAIN is PostgreSQL 17's, and these containers are 17), so a rule that
        // filtered the privilege type — on the reading that SELECT is harmless, or that only writes
        // matter — goes red on the row it dropped. The view and sequence rows hold the relation kinds
        // the rule reads beside tables; each is created here, because the migration makes neither.
        const string view = "public.sabotage_public_view";
        const string sequence = "public.sabotage_public_sequence";
        (string[] SabotageSql, string Probe, string[] ExpectedTokens) arranged = sabotage switch
        {
            "SELECT on a view" => (
                [$"create view {view} as select 1 as x", $"grant select on {view} to public"],
                $"select has_table_privilege('public', '{view}', 'SELECT')",
                [view, "SELECT", "PUBLIC"]),
            "USAGE on a sequence" => (
                [$"create sequence {sequence}", $"grant usage on sequence {sequence} to public"],
                $"select has_sequence_privilege('public', '{sequence}', 'USAGE')",
                [sequence, "USAGE", "PUBLIC"]),
            _ when sabotage.EndsWith(" on a table", StringComparison.Ordinal) => TablePrivilege(
                sabotage[..sabotage.IndexOf(' ', StringComparison.Ordinal)]),
            _ => throw new ArgumentOutOfRangeException(nameof(sabotage), sabotage, null),
        };
        (string[] sabotageSql, string probe, string[] expectedTokens) = arranged;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        foreach (string statement in sabotageSql)
        {
            await ExecuteAsync(admin, statement);
        }

        bool publicHoldsIt = await ScalarBoolAsync(admin, probe);

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(publicHoldsIt).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, expectedTokens)).IsNotEmpty();

        static (string[], string, string[]) TablePrivilege(string privilege) => (
            [$"grant {privilege.ToLowerInvariant()} on {PublicGrantTable} to public"],
            $"select has_table_privilege('public', '{PublicGrantTable}', '{privilege}')",
            [$"public.{PublicGrantTable}", privilege, "PUBLIC"]);
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_UpdateOnAColumnGrantedToPublic_ThrowsNamingTheColumn()
    {
        // Arrange — the same widening one level down: UPDATE on one immutable column, to PUBLIC. It
        // lives in pg_attribute.attacl, not in pg_class.relacl, so a rule reading relation ACLs
        // alone reads budgets as clean while the role can rewrite base_currency_code — a column
        // immutable by omission from the script's column list.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin, $"grant update ({ImmutableColumn}) on {PublicGrantTable} to public");
        bool publicHoldsTableUpdate = await ScalarBoolAsync(
            admin, $"select has_table_privilege('public', '{PublicGrantTable}', 'UPDATE')");
        bool publicHoldsColumnUpdate = await ScalarBoolAsync(
            admin,
            $"select has_column_privilege('public', '{PublicGrantTable}', '{ImmutableColumn}', "
            + "'UPDATE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the table-level read is asserted false first: that is what makes the column ACL
        // the only place this grant can be seen.
        await Assert.That(publicHoldsTableUpdate).IsFalse();
        await Assert.That(publicHoldsColumnUpdate).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, PublicGrantTable, ImmutableColumn)).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_SetOnAParameterGrantedToTheAppRole_ThrowsNamingTheParameter()
    {
        // Arrange — SET on session_replication_role, a superuser-only parameter. Set to replica it
        // stops ordinary triggers firing for the session, and foreign keys are enforced by triggers.
        // A parameter grant lives in pg_parameter_acl and nothing in the script names it.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin,
            $"grant set on parameter {GrantedParameter} to {DatabaseProvisioning.AppRoleName}");
        bool holdsSet = await ScalarBoolAsync(
            admin,
            $"select has_parameter_privilege('{DatabaseProvisioning.AppRoleName}', "
            + $"'{GrantedParameter}', 'SET')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(holdsSet).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, GrantedParameter)).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_FunctionCreatedInPublic_ThrowsNamingTheFunction()
    {
        // Arrange — a function created with no grant at all. Its proacl is NULL, and a NULL ACL on a
        // function is not "nobody" but the built-in default, which gives PUBLIC EXECUTE. So
        // aclexplode(proacl) returns no rows here; only acldefault('f', proowner) shows the grant.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin,
            $"create function public.{DefaultCallableFunction}() returns int "
            + "language sql as 'select 1'");
        bool aclIsNull = await ScalarBoolAsync(
            admin,
            $"select proacl is null from pg_proc where proname = '{DefaultCallableFunction}'");
        bool roleCanExecute = await ScalarBoolAsync(
            admin,
            $"select has_function_privilege('{DatabaseProvisioning.AppRoleName}', "
            + $"'public.{DefaultCallableFunction}()', 'EXECUTE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the NULL ACL first: it is the whole trap, and a sabotage that set one would test
        // an easier case under this name.
        await Assert.That(aclIsNull).IsTrue();
        await Assert.That(roleCanExecute).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, DefaultCallableFunction)).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_FunctionGrantedToTheAppRole_ThrowsNamingTheFunction()
    {
        // Arrange — the other half of the routine rule: EXECUTE taken from PUBLIC and granted to the
        // role by name, so the ACL is explicit and PUBLIC holds nothing. A rule that only looks for
        // PUBLIC's default EXECUTE misses it.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin,
            $"create function public.{GrantedCallableFunction}() returns int "
            + "language sql as 'select 1'");
        await ExecuteAsync(
            admin, $"revoke execute on function public.{GrantedCallableFunction}() from public");
        await ExecuteAsync(
            admin,
            $"grant execute on function public.{GrantedCallableFunction}() "
            + $"to {DatabaseProvisioning.AppRoleName}");
        bool publicCanExecute = await ScalarBoolAsync(
            admin,
            $"select has_function_privilege('public', 'public.{GrantedCallableFunction}()', "
            + "'EXECUTE')");
        bool roleCanExecute = await ScalarBoolAsync(
            admin,
            $"select has_function_privilege('{DatabaseProvisioning.AppRoleName}', "
            + $"'public.{GrantedCallableFunction}()', 'EXECUTE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(publicCanExecute).IsFalse();
        await Assert.That(roleCanExecute).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, GrantedCallableFunction)).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_CreateOnTheDatabaseGrantedToTheAppRole_ThrowsNamingTheDatabase()
    {
        // Arrange — CREATE on the current database, which lets the role make schemas it owns. Named
        // through current_database() so the expectation is the database the verifier was pointed at.
        // Called directly: the script's REVOKE ALL ... FROM budgetoid_app would converge this on a
        // re-run by an owner, and the gate is what reports it when the REVOKE cannot run.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        string database = await ScalarStringAsync(admin, "select current_database()");
        await ExecuteAsync(
            admin, $"grant create on database {database} to {DatabaseProvisioning.AppRoleName}");
        bool holdsCreate = await ScalarBoolAsync(
            admin,
            $"select has_database_privilege('{DatabaseProvisioning.AppRoleName}', "
            + "current_database(), 'CREATE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the database name as a whole word, because "budgetoid" is also a prefix of
        // "budgetoid_app" and a substring match would be satisfied by the role's name alone.
        await Assert.That(holdsCreate).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsWhere(
                    caught,
                    problem => problem.Contains("CREATE", StringComparison.Ordinal)
                        && NamesWholeWord(problem, database)))
            .IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_TemporaryOnTheDatabaseRegrantedToPublic_ThrowsNamingTemporary()
    {
        // Arrange — TEMPORARY handed back to PUBLIC after provisioning took it away. The role
        // inherits PUBLIC, so it can make temporary tables again: rows no grant, no policy and no
        // census in this repository describes.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        string database = await ScalarStringAsync(admin, "select current_database()");
        await ExecuteAsync(admin, $"grant temporary on database {database} to public");
        bool roleHoldsTemporary = await ScalarBoolAsync(
            admin,
            $"select has_database_privilege('{DatabaseProvisioning.AppRoleName}', "
            + "current_database(), 'TEMPORARY')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(roleHoldsTemporary).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsWhere(
                    caught,
                    problem => problem.Contains("TEMPORARY", StringComparison.Ordinal)
                        && problem.Contains("PUBLIC", StringComparison.Ordinal)
                        && NamesWholeWord(problem, database)))
            .IsNotEmpty();
    }

    [Test]
    public async Task ProvisionAsync_WhenAWideningSurvivedTheLastDeploy_RefusesTheDeploy()
    {
        // Arrange — a deployed database with CREATE on schema public hand-granted to the role. The
        // script only ever GRANTs USAGE on the schema and never REVOKEs there, so the next deploy
        // leaves the CREATE in place; this is the deploy that has to refuse.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin, $"grant create on schema public to {DatabaseProvisioning.AppRoleName}");

        // Act
        InvalidOperationException? caught = null;
        try
        {
            await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        bool createSurvived = await ScalarBoolAsync(
            admin,
            $"select has_schema_privilege('{DatabaseProvisioning.AppRoleName}', 'public', 'CREATE')");

        // Assert — the widening survived the re-run first, so the refusal is about something the
        // script genuinely left behind rather than about a script that stopped converging.
        await Assert.That(createSurvived).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, "public", "CREATE")).IsNotEmpty();
    }

    [Test]
    public async Task ProvisionAsync_WhenTheScriptConvergesAHandIssuedGrant_Succeeds()
    {
        // Arrange — a hand-issued CREATE on the current database, to the role. The verifier refuses
        // exactly this when it is still there (VerifyAppRoleReachAsync_CreateOnTheDatabaseGranted…),
        // and the script's REVOKE ALL ON DATABASE … FROM budgetoid_app takes it back — so the deploy
        // passes only if the script runs before the verifier looks. A widening the verifier would
        // not refuse anyway could not tell those two orders apart; this one can. The relation-level
        // version of the same convergence is ApplyGrantsAsync_TakesBackAHandIssuedGrantOnAnyRelation…
        //
        // This is the stated limit, pinned: a widening the script converges away is not reported.
        // The deploy that removed it is the report, and there is nothing left for the gate to name.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        string database = await ScalarStringAsync(admin, "select current_database()");
        await ExecuteAsync(
            admin, $"grant create on database {database} to {DatabaseProvisioning.AppRoleName}");
        bool grantedBeforeTheDeploy = await ScalarBoolAsync(
            admin,
            $"select has_database_privilege('{DatabaseProvisioning.AppRoleName}', "
            + "current_database(), 'CREATE')");

        // Act
        InvalidOperationException? caught = null;
        try
        {
            await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        bool grantedAfterTheDeploy = await ScalarBoolAsync(
            admin,
            $"select has_database_privilege('{DatabaseProvisioning.AppRoleName}', "
            + "current_database(), 'CREATE')");

        // Assert — held before, the deploy went through, and gone after. The exception is asserted
        // before the grant's absence so that a script which stopped converging reports the
        // verifier's sentence naming the database rather than a bare "expected false".
        await Assert.That(grantedBeforeTheDeploy).IsTrue();
        await Assert.That(caught).IsNull();
        await Assert.That(grantedAfterTheDeploy).IsFalse();
    }

    [Test]
    [Arguments("sequence")]
    [Arguments("unscripted table")]
    [Arguments("unscripted view")]
    [Arguments("unscripted materialized view")]
    [Arguments("budgets column")]
    public async Task ApplyGrantsAsync_TakesBackAHandIssuedGrantOnAnyRelationInPublic(string relation)
    {
        // Arrange — a hand-issued grant to the role on a relation in public. The per-table blocks
        // REVOKE only the tables they name, so a relation with no block — or a relation that is not a
        // table at all — keeps its grant through every re-run unless the script revokes the whole
        // schema. The grant script is called on its own rather than through ProvisionAsync: an
        // unscripted table carries no ownership column and would stop RLS coverage first, which is a
        // different refusal from the one this test is about.
        //
        // The budgets row is the scripted case, and passes already; it rides here so the matrix states
        // "any relation in public" rather than "any relation the script forgot".
        //
        // Each probe asks the has_*_privilege functions for a comma-separated list, which answers true
        // when ANY listed privilege is held — so "false after" means every privilege granted is gone.
        const string role = DatabaseProvisioning.AppRoleName;
        (string create, string grant, string probe) = relation switch
        {
            "sequence" => (
                "create sequence public.sabotage_unscripted_sequence",
                $"grant usage, update on sequence public.sabotage_unscripted_sequence to {role}",
                $"select has_sequence_privilege('{role}', 'public.sabotage_unscripted_sequence', "
                + "'USAGE, UPDATE')"),
            "unscripted table" => (
                "create table public.sabotage_unscripted_table (id int)",
                $"grant all on public.sabotage_unscripted_table to {role}",
                $"select has_table_privilege('{role}', 'public.sabotage_unscripted_table', "
                + "'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN')"),
            "unscripted view" => (
                "create view public.sabotage_unscripted_view as select 1 as x",
                $"grant select on public.sabotage_unscripted_view to {role}",
                $"select has_table_privilege('{role}', 'public.sabotage_unscripted_view', 'SELECT')"),
            "unscripted materialized view" => (
                "create materialized view public.sabotage_unscripted_matview as select 1 as x",
                $"grant select on public.sabotage_unscripted_matview to {role}",
                $"select has_table_privilege('{role}', 'public.sabotage_unscripted_matview', "
                + "'SELECT')"),
            "budgets column" => (
                string.Empty,
                $"grant update ({ImmutableColumn}) on {PublicGrantTable} to {role}",
                $"select has_column_privilege('{role}', '{PublicGrantTable}', '{ImmutableColumn}', "
                + "'UPDATE')"),
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
        };

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        if (create.Length > 0)
        {
            await ExecuteAsync(admin, create);
        }

        await ExecuteAsync(admin, grant);
        bool heldBeforeTheScript = await ScalarBoolAsync(admin, probe);

        // Act
        await DatabaseProvisioning.ApplyGrantsAsync(container.GetConnectionString());

        bool heldAfterTheScript = await ScalarBoolAsync(admin, probe);

        // Assert — the grant took first, so "gone after" is about the script and not a sabotage that
        // never landed.
        await Assert.That(heldBeforeTheScript).IsTrue();
        await Assert.That(heldAfterTheScript).IsFalse();
    }

    [Test]
    [Arguments("table to the role")]
    [Arguments("column to the role")]
    [Arguments("sequence to the role")]
    [Arguments("table to PUBLIC")]
    [Arguments("table to the role without schema usage")]
    [Arguments("view to the role")]
    [Arguments("materialized view to the role")]
    public async Task VerifyAppRoleReachAsync_GrantOnARelationOutsidePublic_ThrowsNamingTheRelation(
        string sabotage)
    {
        // Arrange — a grant on a relation in a schema the grant script never names. The script's
        // REVOKEs are all in public, so nothing converges this away; the verifier is the only thing
        // that can see it. The last row withholds USAGE on the schema: the refusal does not depend on
        // the role being able to reach the relation today, because USAGE is one GRANT away and the
        // relation grant is already waiting behind it.
        const string role = DatabaseProvisioning.AppRoleName;
        const string schema = "sabotage_elsewhere";
        const string table = $"{schema}.sabotage_elsewhere_table";
        const string sequence = $"{schema}.sabotage_elsewhere_sequence";
        const string column = "sabotage_elsewhere_column";
        const string view = $"{schema}.sabotage_elsewhere_view";
        const string materializedView = $"{schema}.sabotage_elsewhere_matview";

        (string[] SabotageSql, string Probe, bool SchemaUsage, string[] ExpectedTokens) arranged =
            sabotage switch
            {
                "table to the role" => (
                    [$"grant usage on schema {schema} to {role}", $"grant all on {table} to {role}"],
                    $"select has_table_privilege('{role}', '{table}', 'SELECT')",
                    true,
                    [table]),
                "column to the role" => (
                    [
                        $"grant usage on schema {schema} to {role}",
                        $"grant update ({column}) on {table} to {role}",
                    ],
                    $"select has_column_privilege('{role}', '{table}', '{column}', 'UPDATE')",
                    true,
                    [table, column]),
                "sequence to the role" => (
                    [
                        $"grant usage on schema {schema} to {role}",
                        $"grant usage on sequence {sequence} to {role}",
                    ],
                    $"select has_sequence_privilege('{role}', '{sequence}', 'USAGE')",
                    true,
                    [sequence]),
                "table to PUBLIC" => (
                    [$"grant usage on schema {schema} to {role}", $"grant select on {table} to public"],
                    $"select has_table_privilege('public', '{table}', 'SELECT')",
                    true,
                    [table]),
                "table to the role without schema usage" => (
                    [$"grant select on {table} to {role}"],
                    $"select has_table_privilege('{role}', '{table}', 'SELECT')",
                    false,
                    [table]),
                "view to the role" => (
                    [
                        $"create view {view} as select 1 as x",
                        $"grant usage on schema {schema} to {role}",
                        $"grant select on {view} to {role}",
                    ],
                    $"select has_table_privilege('{role}', '{view}', 'SELECT')",
                    true,
                    [view]),
                "materialized view to the role" => (
                    [
                        $"create materialized view {materializedView} as select 1 as x",
                        $"grant usage on schema {schema} to {role}",
                        $"grant select on {materializedView} to {role}",
                    ],
                    $"select has_table_privilege('{role}', '{materializedView}', 'SELECT')",
                    true,
                    [materializedView]),
                _ => throw new ArgumentOutOfRangeException(nameof(sabotage), sabotage, null),
            };
        (string[] sabotageSql, string probe, bool schemaUsage, string[] expectedTokens) = arranged;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"create schema {schema}");
        await ExecuteAsync(admin, $"create table {table} (id int, {column} text)");
        await ExecuteAsync(admin, $"create sequence {sequence}");
        foreach (string statement in sabotageSql)
        {
            await ExecuteAsync(admin, statement);
        }

        bool grantHeld = await ScalarBoolAsync(admin, probe);
        bool roleHasSchemaUsage = await ScalarBoolAsync(
            admin, $"select has_schema_privilege('{role}', '{schema}', 'USAGE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the sabotage landed as described first, including whether the schema is open to
        // the role, so the no-usage row cannot pass as a copy of the first.
        await Assert.That(grantHeld).IsTrue();
        await Assert.That(roleHasSchemaUsage).IsEqualTo(schemaUsage);
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, expectedTokens)).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_RelationOutsidePublicWithNoGrantToTheRole_DoesNotThrow()
    {
        // Arrange — the control for the outside-public rule. A schema the script never names, holding
        // one relation of each kind that rule reads, all granted to a role that is neither the
        // application role nor PUBLIC. The application role reaches none of it, so a verifier that
        // refuses whatever sits outside public — rather than what the role can reach there — goes red
        // here and nowhere else.
        const string role = DatabaseProvisioning.AppRoleName;
        const string bystander = "sabotage_bystander";
        const string schema = "sabotage_elsewhere";
        const string table = $"{schema}.sabotage_elsewhere_table";
        const string view = $"{schema}.sabotage_elsewhere_view";
        const string sequence = $"{schema}.sabotage_elsewhere_sequence";

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"create role {bystander}");
        await ExecuteAsync(admin, $"create schema {schema}");
        await ExecuteAsync(admin, $"create table {table} (id int)");
        await ExecuteAsync(admin, $"create view {view} as select id from {table}");
        await ExecuteAsync(admin, $"create sequence {sequence}");
        await ExecuteAsync(admin, $"grant usage on schema {schema} to {bystander}");
        await ExecuteAsync(admin, $"grant all on {table} to {bystander}");
        await ExecuteAsync(admin, $"grant select on {view} to {bystander}");
        await ExecuteAsync(admin, $"grant usage, update on sequence {sequence} to {bystander}");

        // The grants landed on the bystander, and neither the role nor PUBLIC reaches anything here.
        // Each probe is a comma-separated list, true when ANY listed privilege is held.
        bool bystanderHoldsAll = await ScalarBoolAsync(
            admin,
            $"select has_table_privilege('{bystander}', '{table}', 'SELECT') "
            + $"and has_table_privilege('{bystander}', '{view}', 'SELECT') "
            + $"and has_sequence_privilege('{bystander}', '{sequence}', 'USAGE')");
        bool roleOrPublicHoldsAny = await ScalarBoolAsync(
            admin,
            $"select has_table_privilege('{role}', '{table}', "
            + "'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN') "
            + $"or has_table_privilege('{role}', '{view}', 'SELECT, INSERT, UPDATE, DELETE') "
            + $"or has_sequence_privilege('{role}', '{sequence}', 'USAGE, SELECT, UPDATE') "
            + $"or has_schema_privilege('{role}', '{schema}', 'USAGE, CREATE') "
            + $"or has_table_privilege('public', '{table}', "
            + "'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN') "
            + $"or has_table_privilege('public', '{view}', 'SELECT, INSERT, UPDATE, DELETE') "
            + $"or has_sequence_privilege('public', '{sequence}', 'USAGE, SELECT, UPDATE') "
            + $"or has_schema_privilege('public', '{schema}', 'USAGE, CREATE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — nothing thrown at all, for the reason the fresh-database control gives.
        await Assert.That(bystanderHoldsAll).IsTrue();
        await Assert.That(roleOrPublicHoldsAny).IsFalse();
        await Assert.That(caught).IsNull();
    }

    [Test]
    [Arguments("select on a scripted table")]
    [Arguments("update on an unlisted budgets column")]
    [Arguments("usage on schema public")]
    [Arguments("connect on the current database")]
    [Arguments("execute on a function in public")]
    public async Task VerifyAppRoleReachAsync_GrantMadeByAThirdRole_ThrowsNamingTheGrantor(
        string sabotage)
    {
        // Arrange — a grant to the role made by a third role holding a grant option, rather than by
        // the object's owner. PostgreSQL records the grantor in the ACL entry (budgetoid_app=r/middle),
        // and the script's REVOKE ALL ... FROM budgetoid_app leaves an entry with another grantor
        // standing — the second precondition below reads it after the script. Each row is there to
        // hold one arm of the non-owner grantor rule, and bar the function row no other rule refuses
        // any of them.
        //
        // The currencies row adds a second entry for a privilege the script already grants — the
        // effective set is unchanged, and only the grantor tells the two apart. The budgets row is the
        // dangerous one: user_id is immutable by its absence from the one-column UPDATE list, and this
        // makes it writable.
        //
        // The schema, database and routine rows are the arms the table rows cannot hold. USAGE on
        // public and CONNECT on the database are the two privileges the verifier otherwise accepts,
        // so a grantor rule switched off there leaves every other rule quiet. The function in public
        // is not quiet: the routine rule refuses it too, whoever granted it, and that sentence names
        // the function, EXECUTE and the role. A grantor's bare name is not enough to tell the two
        // apart — a fixture named after it, or any other sentence that happens to spell it, supplies
        // it — so every row asks for the non-owner sentence's own clause, "with middle as the grantor
        // rather than its owner", which no other rule writes. The function's name deliberately
        // carries no grantor either.
        const string role = DatabaseProvisioning.AppRoleName;
        const string middle = "middle";
        const string function = "public.sabotage_third_role_callable()";
        const string grantorClause = $"with {middle} as the grantor rather than its owner";

        (string OwnerGrant, string MiddleGrant, string GrantorProbe, string[] ExpectedTokens) arranged =
            sabotage switch
            {
                "usage on schema public" => (
                    $"grant usage on schema public to {middle} with grant option",
                    $"grant usage on schema public to {role}",
                    "select exists (select 1 from pg_namespace n, aclexplode(n.nspacl) a "
                    + "where n.nspname = 'public' "
                    + $"and a.grantee = '{role}'::regrole and a.grantor = '{middle}'::regrole "
                    + "and a.privilege_type = 'USAGE')",
                    ["Schema public", "USAGE", grantorClause]),
                "connect on the current database" => (
                    $"grant connect on database {{database}} to {middle} with grant option",
                    $"grant connect on database {{database}} to {role}",
                    "select exists (select 1 from pg_database d, aclexplode(d.datacl) a "
                    + "where d.datname = current_database() "
                    + $"and a.grantee = '{role}'::regrole and a.grantor = '{middle}'::regrole "
                    + "and a.privilege_type = 'CONNECT')",
                    ["Database {database}", "CONNECT", grantorClause]),
                "execute on a function in public" => (
                    $"create function {function} returns int language sql as 'select 1'; "
                    + $"revoke execute on function {function} from public; "
                    + $"grant execute on function {function} to {middle} with grant option",
                    $"grant execute on function {function} to {role}",
                    "select exists (select 1 from pg_proc p, aclexplode(p.proacl) a "
                    + $"where p.oid = '{function}'::regprocedure "
                    + $"and a.grantee = '{role}'::regrole and a.grantor = '{middle}'::regrole "
                    + "and a.privilege_type = 'EXECUTE')",
                    [function, "EXECUTE", grantorClause]),
                "select on a scripted table" => (
                    $"grant select on public.currencies to {middle} with grant option",
                    $"grant select on public.currencies to {role}",
                    "select exists (select 1 from pg_class c, aclexplode(c.relacl) a "
                    + "where c.oid = 'public.currencies'::regclass "
                    + $"and a.grantee = '{role}'::regrole and a.grantor = '{middle}'::regrole "
                    + "and a.privilege_type = 'SELECT')",
                    ["currencies", grantorClause]),
                "update on an unlisted budgets column" => (
                    $"grant update (user_id) on public.budgets to {middle} with grant option",
                    $"grant update (user_id) on public.budgets to {role}",
                    "select exists (select 1 from pg_attribute t, aclexplode(t.attacl) a "
                    + "where t.attrelid = 'public.budgets'::regclass and t.attname = 'user_id' "
                    + $"and a.grantee = '{role}'::regrole and a.grantor = '{middle}'::regrole "
                    + "and a.privilege_type = 'UPDATE')",
                    ["budgets", "user_id", grantorClause]),
                _ => throw new ArgumentOutOfRangeException(nameof(sabotage), sabotage, null),
            };
        (string ownerGrant, string middleGrant, string grantorProbe, string[] expectedTokens) =
            arranged;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);

        // GRANT … ON DATABASE takes a name and no expression, so the database row names it here.
        string database = await ScalarStringAsync(admin, "select current_database()");
        ownerGrant = ownerGrant.Replace("{database}", database, StringComparison.Ordinal);
        middleGrant = middleGrant.Replace("{database}", database, StringComparison.Ordinal);
        expectedTokens = [.. expectedTokens.Select(
            token => token.Replace("{database}", database, StringComparison.Ordinal))];

        await ExecuteAsync(admin, $"create role {middle}");
        await ExecuteAsync(admin, ownerGrant);
        await ExecuteAsync(admin, $"set role {middle}; {middleGrant}; reset role");

        bool heldFromMiddleBeforeTheScript = await ScalarBoolAsync(admin, grantorProbe);

        // Act — the script re-run first, then the verifier: the script is what a deploy runs before
        // the gate, and a grant it took back would leave the gate nothing to find.
        await DatabaseProvisioning.ApplyGrantsAsync(container.GetConnectionString());
        bool heldFromMiddleAfterTheScript = await ScalarBoolAsync(admin, grantorProbe);

        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the grant is held with middle as its grantor, and still is after the script ran,
        // so the refusal below is the verifier's and not the script's.
        await Assert.That(heldFromMiddleBeforeTheScript).IsTrue();
        await Assert.That(heldFromMiddleAfterTheScript).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsNaming(caught, expectedTokens)).IsNotEmpty();
    }

    [Test]
    [Arguments("select on pg_statistic")]
    [Arguments("select on the rolpassword column of pg_authid")]
    [Arguments("execute on pg_read_file")]
    public async Task VerifyAppRoleReachAsync_GrantOnASystemSchemaObject_ThrowsNamingTheObject(
        string sabotage)
    {
        // Arrange — a grant to the role on an object in pg_catalog. pg_statistic holds sampled
        // column values of every table, policed ones included; rolpassword is every role's password
        // hash; pg_read_file reads the server's files. None of them is in a schema the grant script
        // names, so nothing takes the grant back. The fresh-database control is what keeps this
        // rule honest: out of the box the only grantees in the system schemas are PUBLIC, the
        // bootstrap superuser, pg_monitor and pg_read_all_stats, so the rule has to be about who
        // holds the grant and not about where the object lives.
        const string role = DatabaseProvisioning.AppRoleName;
        (string Grant, string Probe, string[] ExpectedTokens) arranged = sabotage switch
        {
            "select on pg_statistic" => (
                $"grant select on pg_catalog.pg_statistic to {role}",
                $"select has_table_privilege('{role}', 'pg_catalog.pg_statistic', 'SELECT')",
                ["pg_statistic", "SELECT"]),
            "select on the rolpassword column of pg_authid" => (
                $"grant select (rolpassword) on pg_catalog.pg_authid to {role}",
                $"select has_column_privilege('{role}', 'pg_catalog.pg_authid', 'rolpassword', "
                + "'SELECT')",
                ["pg_authid", "rolpassword", "SELECT"]),
            "execute on pg_read_file" => (
                $"grant execute on function pg_catalog.pg_read_file(text) to {role}",
                $"select has_function_privilege('{role}', 'pg_catalog.pg_read_file(text)', "
                + "'EXECUTE')",
                ["pg_read_file", "EXECUTE"]),
            _ => throw new ArgumentOutOfRangeException(nameof(sabotage), sabotage, null),
        };
        (string grant, string probe, string[] expectedTokens) = arranged;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        bool heldBefore = await ScalarBoolAsync(admin, probe);
        await ExecuteAsync(admin, grant);
        bool heldAfter = await ScalarBoolAsync(admin, probe);

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the grant is what gave the role the privilege, then the system-schema sentence
        // names the object.
        await Assert.That(heldBefore).IsFalse();
        await Assert.That(heldAfter).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsCarrying(caught, "system schema", expectedTokens)).IsNotEmpty();
    }

    [Test]
    [Arguments("for the role")]
    [Arguments("for the role in the current database")]
    [Arguments("for the role in database postgres")]
    [Arguments("for every role in the current database")]
    public async Task VerifyAppRoleReachAsync_SessionDefaultStoredForTheAppRole_ThrowsNamingTheParameter(
        string sabotage)
    {
        // Arrange — a setting the server applies to the role's every session before the API sends a
        // statement. session_replication_role = replica stops ordinary triggers — the foreign keys'
        // among them — for the session; a stored default is how the role gets it with no grant on
        // the parameter at all. pg_db_role_setting holds all four shapes, empty on a fresh cluster:
        // for the role everywhere (setdatabase 0, which rolconfig also shows), for the role in one
        // database — this one, or postgres, which the role can connect to as well — and for every
        // role in this database (setrole 0). The value is not the finding, the parameter is, so the
        // sentence must name the parameter and not echo what it was set to.
        const string role = DatabaseProvisioning.AppRoleName;
        (string Sql, string Parameter, string Value, string? Database, string Probe) arranged =
            sabotage switch
            {
                "for the role" => (
                    $"alter role {role} set session_replication_role = replica",
                    "session_replication_role",
                    "replica",
                    null,
                    "select count(*) = 1 from pg_db_role_setting "
                    + $"where setrole = '{role}'::regrole and setdatabase = 0"),
                "for the role in the current database" => (
                    $"alter role {role} in database {{database}} set work_mem = '8MB'",
                    "work_mem",
                    "8MB",
                    "{database}",
                    "select count(*) = 1 from pg_db_role_setting "
                    + $"where setrole = '{role}'::regrole and setdatabase = "
                    + "(select oid from pg_database where datname = current_database())"),
                "for the role in database postgres" => (
                    $"alter role {role} in database postgres set work_mem = '8MB'",
                    "work_mem",
                    "8MB",
                    "postgres",
                    "select count(*) = 1 from pg_db_role_setting "
                    + $"where setrole = '{role}'::regrole and setdatabase = "
                    + "(select oid from pg_database where datname = 'postgres')"),
                "for every role in the current database" => (
                    "alter database {database} set session_replication_role = replica",
                    "session_replication_role",
                    "replica",
                    "{database}",
                    "select count(*) = 1 from pg_db_role_setting where setrole = 0 and setdatabase = "
                    + "(select oid from pg_database where datname = current_database())"),
                _ => throw new ArgumentOutOfRangeException(nameof(sabotage), sabotage, null),
            };
        (string sql, string parameter, string value, string? database, string probe) = arranged;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);

        // ALTER … IN DATABASE and ALTER DATABASE take a name and no expression.
        string currentDatabase = await ScalarStringAsync(admin, "select current_database()");
        sql = sql.Replace("{database}", currentDatabase, StringComparison.Ordinal);
        database = database?.Replace("{database}", currentDatabase, StringComparison.Ordinal);

        bool clusterStartedEmpty =
            await ScalarBoolAsync(admin, "select count(*) = 0 from pg_db_role_setting");
        await ExecuteAsync(admin, sql);
        bool storedWhereExpected = await ScalarBoolAsync(admin, probe);

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the setting landed in the one row this sabotage aims at; the session-default
        // sentence names the parameter, and the database where the row is scoped to one, as a whole
        // word so budgetoid_app cannot stand in for budgetoid; and no such sentence carries the
        // value.
        await Assert.That(clusterStartedEmpty).IsTrue();
        await Assert.That(storedWhereExpected).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsWhere(
                    caught,
                    problem => problem.Contains("session default", StringComparison.OrdinalIgnoreCase)
                        && problem.Contains(parameter, StringComparison.Ordinal)
                        && (database is null || NamesWholeWord(problem, database))))
            .IsNotEmpty();
        await Assert.That(
                ProblemsWhere(
                    caught,
                    problem => problem.Contains("session default", StringComparison.OrdinalIgnoreCase)
                        && NamesWholeWord(problem, value)))
            .IsEmpty();
    }

    [Test]
    [Arguments("the app role")]
    [Arguments("PUBLIC")]
    public async Task VerifyAppRoleReachAsync_CreateOnATablespace_ThrowsNamingTheTablespace(
        string grantee)
    {
        // Arrange — CREATE on pg_default, the tablespace every table lands in. With it the role
        // could put a relation it owns there, and CREATE on a tablespace is a grant on a cluster
        // object no line of the grant script names. The PUBLIC row is the same grant reached
        // through inheritance. Neither tablespace grants anybody anything out of the box: both ACLs
        // are NULL, which is the owner alone.
        const string role = DatabaseProvisioning.AppRoleName;
        string granteeSql = grantee == "PUBLIC" ? "public" : role;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        string probe = $"select has_tablespace_privilege('{role}', 'pg_default', 'CREATE')";
        bool heldBefore = await ScalarBoolAsync(admin, probe);
        await ExecuteAsync(admin, $"grant create on tablespace pg_default to {granteeSql}");
        bool heldAfter = await ScalarBoolAsync(admin, probe);

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — "tablespace pg_default" is the rule's own clause; CREATE is the privilege; the
        // PUBLIC row also has to say who holds it, in the grantee's capitals.
        string[] tokens = grantee == "PUBLIC" ? ["CREATE", "PUBLIC"] : ["CREATE"];
        await Assert.That(heldBefore).IsFalse();
        await Assert.That(heldAfter).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsCarrying(caught, "tablespace pg_default", tokens)).IsNotEmpty();
    }

    [Test]
    [Arguments("before update on budgets")]
    [Arguments("disabled")]
    [Arguments("constraint")]
    [Arguments("in another schema")]
    public async Task VerifyAppRoleReachAsync_UserDefinedTrigger_ThrowsNamingTheTrigger(string sabotage)
    {
        // Arrange — a trigger somebody created. A column privilege is checked against the statement
        // the role sends; a BEFORE UPDATE trigger assigning NEW.<column> writes a column the
        // statement never named, and the column probe in AppRoleGrantMatrixTests still reports it
        // refused. A disabled trigger counts — ENABLE TRIGGER is one statement away — and so does a
        // constraint trigger, which pg_trigger records with tgisinternal false. The foreign keys'
        // own triggers are internal and are the referential-action rule's to judge, which is what
        // the fresh-database control holds. A table in another schema writes the same way.
        //
        // The trigger function's EXECUTE is taken from PUBLIC so the routine rule stays quiet and
        // the refusal can only be the trigger's.
        const string function = "public.sabotage_rewrite_fn()";
        (string[] Sql, string Name, string Table, string Probe) arranged = sabotage switch
        {
            "before update on budgets" => (
                [
                    "create trigger sabotage_before_update before update on public.budgets "
                    + $"for each row execute function {function}",
                ],
                "sabotage_before_update",
                "public.budgets",
                "select tgenabled = 'O' from pg_trigger where tgname = 'sabotage_before_update'"),
            "disabled" => (
                [
                    "create trigger sabotage_switched_off before update on public.accounts "
                    + $"for each row execute function {function}",
                    "alter table public.accounts disable trigger sabotage_switched_off",
                ],
                "sabotage_switched_off",
                "public.accounts",
                "select tgenabled = 'D' from pg_trigger where tgname = 'sabotage_switched_off'"),
            "constraint" => (
                [
                    "create constraint trigger sabotage_deferred after update on public.payees "
                    + "deferrable initially deferred "
                    + $"for each row execute function {function}",
                ],
                "sabotage_deferred",
                "public.payees",
                "select tgconstraint <> 0 and not tgisinternal from pg_trigger "
                + "where tgname = 'sabotage_deferred'"),
            "in another schema" => (
                [
                    "create schema sabotage_far",
                    "create table sabotage_far.far_table (id int)",
                    "create trigger sabotage_far_rewrite before update on sabotage_far.far_table "
                    + $"for each row execute function {function}",
                ],
                "sabotage_far_rewrite",
                "sabotage_far.far_table",
                "select exists (select 1 from pg_trigger "
                + "where tgname = 'sabotage_far_rewrite' "
                + "and tgrelid = 'sabotage_far.far_table'::regclass)"),
            _ => throw new ArgumentOutOfRangeException(nameof(sabotage), sabotage, null),
        };
        (string[] sql, string name, string table, string probe) = arranged;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin,
            $"create function {function} returns trigger language plpgsql "
            + "as $$ begin return new; end $$");
        await ExecuteAsync(admin, $"revoke execute on function {function} from public");
        foreach (string statement in sql)
        {
            await ExecuteAsync(admin, statement);
        }

        bool landedAsDescribed = await ScalarBoolAsync(admin, probe);

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — "trigger <name> on <table>" is the rule's clause: no other sentence writes the
        // word before a trigger's name.
        await Assert.That(landedAsDescribed).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsCarrying(caught, $"trigger {name} on {table}")).IsNotEmpty();
    }

    [Test]
    [Arguments("do also on budgets")]
    [Arguments("in another schema")]
    public async Task VerifyAppRoleReachAsync_RewriteRuleBeyondAViewsOwn_ThrowsNamingTheRule(
        string sabotage)
    {
        // Arrange — a rewrite rule. Its actions run with the rule owner's privileges, so a DO ALSO
        // INSERT writes a table the role holds nothing on, on the role's own UPDATE. The one rule
        // that is not a finding is a view's _RETURN, which is the view's definition; the view
        // control below holds that exclusion, and the fresh-database control holds the pg_catalog
        // one, where pg_settings carries two rules of its own.
        (string[] Sql, string Name, string Table) arranged = sabotage switch
        {
            "do also on budgets" => (
                [
                    "create rule sabotage_also as on update to public.budgets "
                    + "do also notify sabotage_channel",
                ],
                "sabotage_also",
                "public.budgets"),
            "in another schema" => (
                [
                    "create schema sabotage_far",
                    "create table sabotage_far.far_table (id int)",
                    "create table sabotage_far.far_log (id int)",
                    "create rule sabotage_far_copy as on insert to sabotage_far.far_table "
                    + "do also insert into sabotage_far.far_log values (new.id)",
                ],
                "sabotage_far_copy",
                "sabotage_far.far_table"),
            _ => throw new ArgumentOutOfRangeException(nameof(sabotage), sabotage, null),
        };
        (string[] sql, string name, string table) = arranged;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        foreach (string statement in sql)
        {
            await ExecuteAsync(admin, statement);
        }

        bool ruleExists = await ScalarBoolAsync(
            admin,
            "select exists (select 1 from pg_rewrite "
            + $"where rulename = '{name}' and ev_class = '{table}'::regclass)");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(ruleExists).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(ProblemsCarrying(caught, $"rule {name} on {table}")).IsNotEmpty();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_ViewInPublic_DoesNotThrow()
    {
        // Arrange — the control for the rewrite-rule refusal. A view is a relation whose definition is a
        // rewrite rule named _RETURN, and the migration creates no view, so without this one the
        // _RETURN exclusion is held by nothing. No grant on it to the role or to PUBLIC.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, "create view public.sabotage_plain_view as select 1 as x");
        bool viewCarriesItsOwnRule = await ScalarBoolAsync(
            admin,
            "select exists (select 1 from pg_rewrite where rulename = '_RETURN' "
            + "and ev_class = 'public.sabotage_plain_view'::regclass)");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — nothing thrown at all, for the reason the fresh-database control gives.
        await Assert.That(viewCarriesItsOwnRule).IsTrue();
        await Assert.That(caught).IsNull();
    }

    [Test]
    [Arguments("on update cascade from users email")]
    [Arguments("on delete set null behind an on delete cascade")]
    [Arguments("on update cascade behind a granted column")]
    [Arguments("on update cascade behind a refused column")]
    [Arguments("on update cascade behind an on delete set null")]
    public async Task VerifyAppRoleReachAsync_ReferentialActionWritesAColumnTheRoleCannotUpdate_ThrowsNamingTheColumn(
        string sabotage)
    {
        // Arrange — a foreign key whose action rewrites a referencing column the role cannot UPDATE,
        // set off by something the role can do. The action runs as the table owner, so the column
        // privilege never checks it. Each row was run on postgres:17.10 as the role and wrote the
        // column below:
        //
        // - UPDATE (email) on users is granted, and an ON UPDATE CASCADE from another table's column
        //   carries a new email into it. email has a unique index, which is all a foreign key needs.
        // - DELETE on users is granted, and cascades a delete into a table the role cannot DELETE
        //   from, whose ON DELETE SET NULL child is then rewritten: a rule that asked only "can the
        //   role DELETE the referenced table" never gets past the first hop.
        // - Two ON UPDATE CASCADE hops, the middle column granted: the middle is written and is no
        //   finding, because the role could write it itself, and the leaf is. Exactly one sentence,
        //   so a rule that reports every written column whatever the role holds goes red here.
        // - The same two hops, the middle column refused: the leaf is reached only through a column
        //   the role cannot write, so only a rule that follows the chain names it.
        // - ON DELETE SET NULL on a referenced key, which the referencing table's ON UPDATE CASCADE
        //   then carries on: a delete becomes an update one hop down.
        const string role = DatabaseProvisioning.AppRoleName;
        (string[] Sql, string ArmedProbe, string WrittenTable, string WrittenColumn, bool OnlyOne)
            arranged = sabotage switch
            {
                "on update cascade from users email" => (
                    [
                        "create table public.sabotage_email_holder (id int primary key, "
                        + "email_copy varchar(254) collate case_insensitive "
                        + "references public.users (email) on update cascade)",
                    ],
                    $"select has_column_privilege('{role}', 'public.users', 'email', 'UPDATE')",
                    "public.sabotage_email_holder",
                    "email_copy",
                    false),
                "on delete set null behind an on delete cascade" => (
                    [
                        "create table public.sabotage_parent (id uuid primary key, "
                        + "user_ref uuid references public.users (id) on delete cascade)",
                        "create table public.sabotage_kid (id int primary key, "
                        + "parent_ref uuid references public.sabotage_parent (id) on delete set null)",
                    ],
                    $"select has_table_privilege('{role}', 'public.users', 'DELETE') "
                    + $"and not has_table_privilege('{role}', 'public.sabotage_parent', 'DELETE')",
                    "public.sabotage_kid",
                    "parent_ref",
                    false),
                "on update cascade behind a granted column" => (
                    [
                        "create table public.sabotage_relay (id int primary key, "
                        + "relay_code varchar(254) collate case_insensitive unique "
                        + "references public.users (email) on update cascade)",
                        $"grant update (relay_code) on public.sabotage_relay to {role}",
                        "create table public.sabotage_leaf (id int primary key, "
                        + "leaf_code varchar(254) collate case_insensitive "
                        + "references public.sabotage_relay (relay_code) on update cascade)",
                    ],
                    $"select has_column_privilege('{role}', 'public.users', 'email', 'UPDATE') "
                    + $"and has_column_privilege('{role}', 'public.sabotage_relay', 'relay_code', "
                    + "'UPDATE')",
                    "public.sabotage_leaf",
                    "leaf_code",
                    true),
                "on update cascade behind a refused column" => (
                    [
                        "create table public.sabotage_relay (id int primary key, "
                        + "relay_code varchar(254) collate case_insensitive unique "
                        + "references public.users (email) on update cascade)",
                        "create table public.sabotage_leaf (id int primary key, "
                        + "leaf_code varchar(254) collate case_insensitive "
                        + "references public.sabotage_relay (relay_code) on update cascade)",
                    ],
                    $"select has_column_privilege('{role}', 'public.users', 'email', 'UPDATE') "
                    + $"and not has_column_privilege('{role}', 'public.sabotage_relay', "
                    + "'relay_code', 'UPDATE')",
                    "public.sabotage_leaf",
                    "leaf_code",
                    false),
                "on update cascade behind an on delete set null" => (
                    [
                        "create table public.sabotage_owner_relay (id int primary key, "
                        + "owner_ref uuid unique references public.users (id) on delete set null)",
                        "create table public.sabotage_tail (id int primary key, "
                        + "tail_ref uuid references public.sabotage_owner_relay (owner_ref) "
                        + "on update cascade)",
                    ],
                    $"select has_table_privilege('{role}', 'public.users', 'DELETE')",
                    "public.sabotage_tail",
                    "tail_ref",
                    false),
                _ => throw new ArgumentOutOfRangeException(nameof(sabotage), sabotage, null),
            };
        (string[] sql, string armedProbe, string writtenTable, string writtenColumn, bool onlyOne) =
            arranged;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        foreach (string statement in sql)
        {
            await ExecuteAsync(admin, statement);
        }

        bool armed = await ScalarBoolAsync(admin, armedProbe);
        bool roleCanWriteItItself = await ScalarBoolAsync(
            admin,
            $"select has_column_privilege('{role}', '{writtenTable}', '{writtenColumn}', 'UPDATE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert — the role can set the chain off and cannot write the column itself; then the
        // referential-action sentence names the written column.
        await Assert.That(armed).IsTrue();
        await Assert.That(roleCanWriteItItself).IsFalse();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsCarrying(
                    caught, "referential action", $"column {writtenColumn} of table {writtenTable}"))
            .IsNotEmpty();
        if (onlyOne)
        {
            await Assert.That(ProblemsCarrying(caught, "referential action").Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_ReferentialActionTheRoleCannotSetOff_DoesNotThrow()
    {
        // Arrange — the precise negative for the rule above: an ON UPDATE CASCADE onto users.id,
        // which the role cannot UPDATE. The action writes a column the role cannot write, and
        // nothing the role can do fires it, so it is no reach of the role's. A rule that refused
        // every writing action regardless of who can set it off goes red here.
        const string role = DatabaseProvisioning.AppRoleName;
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin,
            "create table public.sabotage_quiet (id int primary key, "
            + "user_copy uuid references public.users (id) on update cascade)");
        bool actionIsCascade = await ScalarBoolAsync(
            admin,
            "select confupdtype = 'c' from pg_constraint "
            + "where conrelid = 'public.sabotage_quiet'::regclass and contype = 'f'");
        bool roleCanSetItOff = await ScalarBoolAsync(
            admin, $"select has_column_privilege('{role}', 'public.users', 'id', 'UPDATE')");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(actionIsCascade).IsTrue();
        await Assert.That(roleCanSetItOff).IsFalse();
        await Assert.That(caught).IsNull();
    }

    [Test]
    [Arguments("base column granted to the role")]
    [Arguments("base column nulled behind an on delete cascade")]
    public async Task VerifyAppRoleReachAsync_StoredGeneratedColumnRewrittenForTheRole_ThrowsNamingTheColumn(
        string sabotage)
    {
        // Arrange — a stored generated column is recomputed whenever a column it reads changes, and
        // nobody's UPDATE privilege is asked about it. Both rows were run on postgres:17.10 as the
        // role: an UPDATE of the granted base column rewrote the generated one, and a DELETE on users
        // cascaded, set the base column NULL one hop down, and rewrote the generated column beside
        // it. The second row is only reachable through the referential-action walk: the role holds
        // no privilege on that table at all.
        const string role = DatabaseProvisioning.AppRoleName;
        (string[] Sql, string ArmedProbe, string Table, string Column) arranged = sabotage switch
        {
            "base column granted to the role" => (
                [
                    "create table public.sabotage_derive (id int primary key, base_text text, "
                    + "derived_upper text generated always as (upper(base_text)) stored)",
                    $"grant update (base_text) on public.sabotage_derive to {role}",
                ],
                $"select has_column_privilege('{role}', 'public.sabotage_derive', 'base_text', "
                + "'UPDATE')",
                "public.sabotage_derive",
                "derived_upper"),
            "base column nulled behind an on delete cascade" => (
                [
                    "create table public.sabotage_parent (id uuid primary key, "
                    + "user_ref uuid references public.users (id) on delete cascade)",
                    "create table public.sabotage_kid (id int primary key, "
                    + "parent_ref uuid references public.sabotage_parent (id) on delete set null, "
                    + "parent_gone boolean generated always as (parent_ref is null) stored)",
                ],
                $"select has_table_privilege('{role}', 'public.users', 'DELETE') "
                + $"and not has_column_privilege('{role}', 'public.sabotage_kid', 'parent_ref', "
                + "'UPDATE')",
                "public.sabotage_kid",
                "parent_gone"),
            _ => throw new ArgumentOutOfRangeException(nameof(sabotage), sabotage, null),
        };
        (string[] sql, string armedProbe, string table, string column) = arranged;

        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        foreach (string statement in sql)
        {
            await ExecuteAsync(admin, statement);
        }

        bool armed = await ScalarBoolAsync(admin, armedProbe);
        bool isStoredGenerated = await ScalarBoolAsync(
            admin,
            "select attgenerated = 's' from pg_attribute "
            + $"where attrelid = '{table}'::regclass and attname = '{column}'");

        // Act
        List<string> logLines = [];
        InvalidOperationException? caught =
            await TryVerifyAppRoleReachAsync(container.GetConnectionString(), logLines);

        // Assert
        await Assert.That(armed).IsTrue();
        await Assert.That(isStoredGenerated).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsCarrying(caught, "generated column", $"column {column} of table {table}"))
            .IsNotEmpty();
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
        (int survivingPolicies, _) =
            await CountIsolationPoliciesAsync(admin, SabotagedTable, BudgetIsolationPolicy);

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
    public async Task VerifyRowLevelSecurityCoverageAsync_MissingPolicyOnUsers_ThrowsListingTheTable()
    {
        // Arrange — the same sabotage as the missing-policy test, aimed at the one table that owns
        // rows without carrying an ownership column. That difference is the whole test: a verifier
        // that finds its subjects by looking for a budget_id column cannot see users at all, so
        // dropping the policy that stands between the application role and every person's row leaves
        // the deploy reporting full coverage. The check is not weaker here, it is absent, and absence
        // is invisible from the outside — which is the exact fail-open shape this verifier was
        // written to prevent, now sitting inside the verifier.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"drop policy {UserIsolationPolicy} on {UsersTable}");

        // Act — the verifier directly, never through ProvisionAsync, for the reason spelled out in
        // the missing-policy test: ProvisionAsync re-applies the grants script before verifying and
        // would heal this damage on the way past.
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

        // Assert — the same four claims as the budget-owned sabotage, and deliberately not a weaker
        // set. A user-owned table left unpoliced is not a lesser breach that deserves a softer
        // report: it is every registered person's row readable by a session that named somebody
        // else. The reasoning behind each line is in the missing-policy test above.
        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(UsersTable);
        await Assert.That(caught!.Message).Contains(UsersTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_RenamedPolicy_ThrowsListingTheTable()
    {
        // Arrange — rename rather than drop, so the table still has exactly one policy and still has
        // row-level security switched on. Everything a count can see is unchanged; only the name
        // moved.
        //
        // That is why counting was never enough. A count answers "does a rule exist here", and the
        // question a deploy has to answer is "is the rule that exists here the rule this table
        // owes". Those come apart in two directions and both ship silently: the policy body can be
        // rewritten under a name nobody reads, and — once there are two isolation rules in the
        // schema — a table can end up carrying the wrong one of them, which is a real, enforced
        // policy that is simply wider than its tenancy. A rename is the cheapest way to make that
        // gap visible, because it changes nothing else at all.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin,
            $"alter policy {BudgetIsolationPolicy} on {SabotagedTable} rename to {RenamedPolicy}");

        // Act
        (int survivingPolicies, int surviving) =
            await CountIsolationPoliciesAsync(admin, SabotagedTable, BudgetIsolationPolicy);

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

        // Assert — the state of the sabotaged table first, because without it this test could be the
        // missing-policy one wearing a different name. One policy present and none of it named
        // budget_isolation is what pins the sabotage as "renamed" rather than "gone", and it is the
        // precise state every count-based check calls healthy.
        await Assert.That(survivingPolicies).IsEqualTo(1);
        await Assert.That(surviving).IsEqualTo(0);

        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(SabotagedTable);
        await Assert.That(caught!.Message).Contains(SabotagedTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_UnclassifiableTable_Throws()
    {
        // Arrange — a new ordinary table in public carrying neither ownership column. Today the
        // verifier finds its subjects by looking for budget_id, so this table is exempted by a query
        // rather than by anybody's decision, and the deploy passes.
        //
        // Refusing it is not pedantry about a table that may well need nothing. It is that "we
        // forgot to police this" and "this genuinely needs no policy" produce the identical catalog,
        // and the difference between them is a judgement only a person can make. Left to a query,
        // every future table gets the benefit of the doubt silently and permanently — and the deploy
        // is the last moment anyone is looking. A red build asking someone to decide costs a minute;
        // the alternative costs whatever the table turns out to hold.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin, $"create table public.{UnclassifiableTable} (id uuid primary key, note text)");

        // Act — again straight at the verifier. Here that matters for a second reason on top of the
        // healing one: this table is outside the grants script entirely, so ProvisionAsync would not
        // touch it and the sabotage would survive — but the call would still be the whole pipeline
        // rather than the one step whose behaviour is in question.
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

        // Assert — the coverage exception rather than a bare InvalidOperationException, because an
        // operator reading this needs to be sent to the same place the other coverage failures send
        // them: a table in the schema is not accounted for, and here it is. The table name in both
        // the structured list and the message, for the reasons the missing-policy test gives.
        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(UnclassifiableTable);
        await Assert.That(caught!.Message).Contains(UnclassifiableTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_PolicyNarrowedToSelect_ThrowsListingTheTable()
    {
        // Arrange — the policy is dropped and recreated with the real predicate, the required name,
        // the right role, and one word changed: FOR ALL becomes FOR SELECT. Everything a name-and-
        // roles check reads is untouched, which is why this ships.
        //
        // accounts rather than payees because the cost has to be nameable. The role holds
        // UPDATE (name, name_key, type, opening_balance) and DELETE on accounts, and a policy that
        // covers only SELECT leaves both of those commands with no permissive policy to satisfy —
        // PostgreSQL denies them outright. So a green gate here would have shipped a deploy where
        // every account rename and every account deletion fails in production, for every tenant,
        // while the check that exists to certify the isolation story reports it as fully covered.
        // The failure is the opposite direction from a leak and is no less a reason to refuse: the
        // gate's claim is that the rule enforced is the rule owed, and FOR SELECT is not the rule
        // owed.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"drop policy {BudgetIsolationPolicy} on {GrantedForWriteTable}");
        await ExecuteAsync(
            admin,
            $"create policy {BudgetIsolationPolicy} on {GrantedForWriteTable} "
            + $"for select to {DatabaseProvisioning.AppRoleName} "
            + $"using ({BudgetOwnershipPredicate})");

        // Act
        (string? sabotagedUsing, _, string? command, _) =
            await ReadPolicyAsync(admin, GrantedForWriteTable, BudgetIsolationPolicy);
        (int survivingPolicies, int surviving) = await CountIsolationPoliciesAsync(
            admin, GrantedForWriteTable, BudgetIsolationPolicy);

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

        // Assert — the sabotage first, because a create policy that errored would leave the table
        // with no policy at all and this test would go red as a duplicate of the missing-policy one.
        // One policy, carrying the required name, with the real predicate, restricted to SELECT: that
        // combination is the whole subject, and every count- and name-based check calls it healthy.
        await Assert.That(survivingPolicies).IsEqualTo(1);
        await Assert.That(surviving).IsEqualTo(1);
        await Assert.That(sabotagedUsing).IsNotNull();
        await Assert.That(command).IsEqualTo("r");

        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(GrantedForWriteTable);
        await Assert.That(caught!.Message).Contains(GrantedForWriteTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_PolicyWithATrivialUsingExpression_ThrowsListingTheTable()
    {
        // Arrange — the name is right, the roles are right, the command is right, and the rule says
        // yes to every row. USING (true) is not a degenerate case invented for a test: it is what a
        // policy left behind by someone debugging a 0-row query looks like, and it is the shortest
        // possible way to have a real, enforced, correctly-named isolation policy that isolates
        // nothing.
        //
        // A green gate here would have shipped every budget's payees to every session, with the
        // deploy log reporting the table as policed by budget_isolation — which is true, and which is
        // exactly why reading only the name is not enough. Only the policy's content separates
        // "budget_isolation exists here" from "budgets are isolated here".
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"drop policy {BudgetIsolationPolicy} on {SabotagedTable}");
        await ExecuteAsync(
            admin,
            $"create policy {BudgetIsolationPolicy} on {SabotagedTable} "
            + $"for all to {DatabaseProvisioning.AppRoleName} "
            + "using (true) with check (true)");

        // Act
        (string? sabotagedUsing, string? sabotagedWithCheck, string? command, _) =
            await ReadPolicyAsync(admin, SabotagedTable, BudgetIsolationPolicy);

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

        // Assert — the sabotage first, so that a create policy which failed to apply cannot be
        // mistaken for the behaviour under test. Both halves read back as the constant true, and the
        // command is still FOR ALL, which is what leaves the content as the only thing that differs.
        await Assert.That(sabotagedUsing).IsEqualTo("true");
        await Assert.That(sabotagedWithCheck).IsEqualTo("true");
        await Assert.That(command).IsEqualTo("*");

        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(SabotagedTable);
        await Assert.That(caught!.Message).Contains(SabotagedTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_PolicyKeyedOnTheWrongSessionSetting_ThrowsListingTheTable()
    {
        // Arrange — the real shape in every respect except which session setting the ownership column
        // is compared against: budget_id = app.current_user_id, in both halves. It reads a setting,
        // it names the table's own ownership column, it is permissive, it is FOR ALL, it is named
        // budget_isolation, and it binds the application role.
        //
        // This test exists to forbid the cheap implementation. A check that satisfied itself with
        // "the predicate mentions current_setting somewhere" would pass this, and so would one that
        // asked only "does the predicate mention the ownership column". Both are wrong here: a
        // budget id compared to a user id never matches — the two are drawn from different id spaces
        // — so a green gate would have shipped a table that reads as empty for every tenant, with the
        // deploy certifying it as isolated. Which setting keys which column is the rule, and nothing
        // less than the pair of them is the rule.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"drop policy {BudgetIsolationPolicy} on {SabotagedTable}");
        await ExecuteAsync(
            admin,
            $"create policy {BudgetIsolationPolicy} on {SabotagedTable} "
            + $"for all to {DatabaseProvisioning.AppRoleName} "
            + $"using ({BudgetColumnKeyedOnTheUserSetting}) "
            + $"with check ({BudgetColumnKeyedOnTheUserSetting})");

        // Act
        (string? sabotagedUsing, string? sabotagedWithCheck, string? command, _) =
            await ReadPolicyAsync(admin, SabotagedTable, BudgetIsolationPolicy);

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

        // Assert — the sabotage first, and here it is asserted in a shape that pins the trap rather
        // than merely pinning that something applied: the predicate does read a session setting, and
        // it does name budget_id, and it is still wrong. Anything that concludes "policed" from
        // either of those two facts alone has to fail this test.
        await Assert.That(sabotagedUsing).IsNotNull();
        await Assert.That(sabotagedUsing!).Contains("current_setting");
        await Assert.That(sabotagedUsing).Contains("app.current_user_id");
        await Assert.That(sabotagedUsing).Contains("budget_id");
        await Assert.That(sabotagedUsing).DoesNotContain("app.current_budget_id");
        await Assert.That(sabotagedWithCheck).IsEqualTo(sabotagedUsing);
        await Assert.That(command).IsEqualTo("*");

        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(SabotagedTable);
        await Assert.That(caught!.Message).Contains(SabotagedTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_PolicyOnUsersReferencingNoOwnershipColumn_ThrowsListingTheTable()
    {
        // Arrange — user_isolation on users, replaced by a predicate that reads the correct setting
        // and asks only whether anybody is signed in. Every signed-in session then reads every
        // person's row, so a green gate would have shipped the account table of the whole service to
        // any authenticated caller, under a policy named exactly what it should be named.
        //
        // This sabotage is aimed at one specific wrong implementation, and users is the only table in
        // the schema on which it can be aimed. The ownership column of users is id — users is
        // user-owned by BEING the person rather than by referencing one — so a content check written
        // as "the predicate must mention the ownership column" degrades on this table into
        // Contains("id"). And "id" is a substring of app.current_user_id, and of budget_id, and of
        // user_id: the sabotaged predicate below contains the letters id twice over while naming no
        // column called id at all. The assertions state that rather than trusting it. Whoever
        // implements the content check needs to know that a substring test is not merely weak here,
        // it is vacuous — it cannot fail on this table, which is the one table where failing matters
        // most.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"drop policy {UserIsolationPolicy} on {UsersTable}");
        await ExecuteAsync(
            admin,
            $"create policy {UserIsolationPolicy} on {UsersTable} "
            + $"for all to {DatabaseProvisioning.AppRoleName} "
            + $"using ({SignedInButOwnershipFreePredicate})");

        // Act
        (string? sabotagedUsing, _, string? command, _) =
            await ReadPolicyAsync(admin, UsersTable, UserIsolationPolicy);

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

        // Assert — the trap, spelled out as two assertions that are not restatements of each other.
        // The first says a substring search for "id" succeeds on this predicate; the second says no
        // whole word "id" appears in it. Together they are the reason this test exists: an
        // implementation that reaches for Contains passes the first and never consults the second.
        await Assert.That(sabotagedUsing).IsNotNull();
        await Assert.That(sabotagedUsing!).Contains("id");
        await Assert.That(StandaloneIdWord.IsMatch(sabotagedUsing)).IsFalse();
        await Assert.That(sabotagedUsing).Contains("app.current_user_id");
        await Assert.That(command).IsEqualTo("*");

        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(UsersTable);
        await Assert.That(caught!.Message).Contains(UsersTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_PolicyWithAWiderWithCheckThanUsing_ThrowsListingTheTable()
    {
        // Arrange — the correct USING, so every read is isolated exactly as it should be, and
        // WITH CHECK (true), so every write is not. Reads are the half anybody testing by hand would
        // look at, which is what makes this the sabotage most likely to survive review.
        //
        // A green gate would have shipped a database in which the application role can INSERT a payee
        // into any budget it names and UPDATE a row out of the current budget into somebody else's —
        // and then never see it again, because USING still hides it. Writes that vanish into another
        // tenant are worse than reads that leak: the leak is at least visible to the person who
        // suffers it. USING and WITH CHECK are two rules and the table owes both.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"drop policy {BudgetIsolationPolicy} on {SabotagedTable}");
        await ExecuteAsync(
            admin,
            $"create policy {BudgetIsolationPolicy} on {SabotagedTable} "
            + $"for all to {DatabaseProvisioning.AppRoleName} "
            + $"using ({BudgetOwnershipPredicate}) with check (true)");

        // Act
        (string? sabotagedUsing, string? sabotagedWithCheck, string? command, _) =
            await ReadPolicyAsync(admin, SabotagedTable, BudgetIsolationPolicy);

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

        // Assert — the two halves read back differently, which is the sabotage and also the only
        // thing that separates this test from the accepted case below. The USING half is asserted to
        // be the real predicate rather than merely non-null, because a create policy that had
        // silently applied true to both halves would turn this into the trivial-predicate test.
        await Assert.That(sabotagedUsing).IsNotNull();
        await Assert.That(sabotagedUsing!).Contains("app.current_budget_id");
        await Assert.That(sabotagedWithCheck).IsEqualTo("true");
        await Assert.That(command).IsEqualTo("*");

        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(SabotagedTable);
        await Assert.That(caught!.Message).Contains(SabotagedTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_PolicyWithNoWithCheck_IsAccepted()
    {
        // Arrange — the real USING and no WITH CHECK clause at all, which PostgreSQL records as a
        // NULL polwithcheck. This is the one test in this group that asserts a policy is ACCEPTED,
        // and that is its whole point.
        //
        // The rule the previous test asks for is "the check half may not be wider than the read
        // half". The obvious way to implement that is to compare two expressions, and the obvious way
        // to handle a NULL is to call it missing and refuse. That would be wrong: when WITH CHECK is
        // omitted, PostgreSQL reuses USING for the check, so a NULL polwithcheck is not an absent
        // rule, it is the same rule stated once. Refusing it would turn a verifier into a style
        // checker that fails a deploy over a clause whose presence changes nothing — and the pressure
        // to do so is real, because "require WITH CHECK to be present" is a shorter sentence than the
        // rule actually owed.
        //
        // app-role-grants.sql writes both halves explicitly and should keep doing so: it is written
        // for people, and a reader should not have to know this PostgreSQL rule to see that writes
        // are constrained. That is a rule about the script's prose, not a rule the gate may enforce
        // against the catalog.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"drop policy {BudgetIsolationPolicy} on {SabotagedTable}");
        await ExecuteAsync(
            admin,
            $"create policy {BudgetIsolationPolicy} on {SabotagedTable} "
            + $"for all to {DatabaseProvisioning.AppRoleName} "
            + $"using ({BudgetOwnershipPredicate})");

        // Act
        (string? policyUsing, string? policyWithCheck, string? command, _) =
            await ReadPolicyAsync(admin, SabotagedTable, BudgetIsolationPolicy);

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

        // Assert — the omission is real before anything is concluded from it. Without this the test
        // would keep passing against a policy that quietly carried a WITH CHECK, and would then
        // defend nothing at all.
        await Assert.That(policyUsing).IsNotNull();
        await Assert.That(policyUsing!).Contains("app.current_budget_id");
        await Assert.That(policyWithCheck).IsNull();
        await Assert.That(command).IsEqualTo("*");

        // And the verdict: accepted. Asserted as "nothing was thrown" rather than as an absence of a
        // particular exception type, because any refusal at all is the failure this test is here to
        // catch.
        await Assert.That(caught).IsNull();
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_RestrictivePolicy_ThrowsListingTheTable()
    {
        // Arrange — AS RESTRICTIVE, FOR ALL, the required name, the application role, and the real
        // predicate in both halves. Name, roles, command and content all pass; polpermissive is the
        // only column in pg_policy that has changed, and it is the difference between a rule that
        // grants access and a rule that cannot.
        //
        // Restrictive is wrong here rather than stricter. Permissive policies OR together to say what
        // a role MAY reach; restrictive policies AND onto that result to narrow it. A restrictive
        // policy therefore grants nothing on its own, and a table whose only policy is restrictive
        // has no rule granting anything — so with row-level security enabled the application role
        // reads zero rows and writes none, in every tenant. A green gate would have certified as
        // isolated a table the application cannot use at all, and the operator debugging the empty
        // result would have the deploy log telling them the policy is present and correct. The
        // enforced rule is not the rule owed, which is the same sentence the renamed-policy test
        // ends on, reached by a different route.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(admin, $"drop policy {BudgetIsolationPolicy} on {SabotagedTable}");
        await ExecuteAsync(
            admin,
            $"create policy {BudgetIsolationPolicy} on {SabotagedTable} as restrictive "
            + $"for all to {DatabaseProvisioning.AppRoleName} "
            + $"using ({BudgetOwnershipPredicate}) with check ({BudgetOwnershipPredicate})");

        // Act
        (string? policyUsing, string? policyWithCheck, string? command, bool permissive) =
            await ReadPolicyAsync(admin, SabotagedTable, BudgetIsolationPolicy);
        (int survivingPolicies, int surviving) =
            await CountIsolationPoliciesAsync(admin, SabotagedTable, BudgetIsolationPolicy);

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

        // Assert — everything except polpermissive is asserted to be right, which is what makes the
        // single false below the entire subject of the test. If any of these drifted, the red would
        // be about the wrong thing.
        await Assert.That(survivingPolicies).IsEqualTo(1);
        await Assert.That(surviving).IsEqualTo(1);
        await Assert.That(policyUsing).IsNotNull();
        await Assert.That(policyUsing!).Contains("app.current_budget_id");
        await Assert.That(policyWithCheck).IsEqualTo(policyUsing);
        await Assert.That(command).IsEqualTo("*");
        await Assert.That(permissive).IsFalse();

        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(SabotagedTable);
        await Assert.That(caught!.Message).Contains(SabotagedTable);
        await Assert.That(logLines).IsNotEmpty();
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_ViewOverAPolicedTable_Throws()
    {
        // Arrange — no policy is touched. Every table keeps the rule it owes, and a view is added
        // over the most sensitive of them and granted to the application role.
        //
        // Discovery is keyed on relkind = 'r', so the view is not a subject and not an exemption
        // either — it is simply invisible, which is the same silent exemption-by-query shape the
        // unclassifiable-table test already refuses for tables. It is worse here than there, because
        // this one is not a hypothetical about a table that may need nothing: a view runs with the
        // privileges of its OWNER unless it is declared WITH (security_invoker = true), the owner
        // here is the schema owner, and an owner is not subject to row-level security. So the role
        // selecting through this view reads every tenant's transactions, unfiltered, while the gate
        // reports full coverage on a schema in which every policy is present, enabled and correct.
        // The leak does not even require a mistake in the policies — it routes around them.
        //
        // Which is also why refusing an unknown view is the right shape rather than pedantry: a view
        // over tenant data is either security_invoker, or it is a bypass, and nothing in the catalog
        // distinguishes "we meant this" from "we forgot" except somebody writing it down.
        await using PostgreSqlContainer container = await StartBareContainerAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(container.GetConnectionString());

        await using NpgsqlConnection admin = await OpenAdminAsync(container);
        await ExecuteAsync(
            admin, $"create view public.{SabotageView} as select * from {MigratedTable}");
        await ExecuteAsync(
            admin,
            $"grant select on public.{SabotageView} to {DatabaseProvisioning.AppRoleName}");

        // Act — straight at the verifier, as everywhere else in this group. Here ProvisionAsync would
        // not heal the damage, since the view is outside the grants script entirely; going through it
        // would still be running the whole pipeline to ask about one step.
        (int policiesOnTheSourceTable, int correctlyNamed) =
            await CountIsolationPoliciesAsync(admin, MigratedTable, BudgetIsolationPolicy);

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

        // Assert — the source table's policy is asserted intact first, because that is what makes the
        // claim interesting: the refusal being demanded here is not "a policy is wrong somewhere",
        // it is "every policy is right and the data is reachable anyway".
        await Assert.That(policiesOnTheSourceTable).IsEqualTo(1);
        await Assert.That(correctlyNamed).IsEqualTo(1);

        await Assert.That(caught).IsTypeOf<RowLevelSecurityCoverageException>();
        await Assert.That(reportedTables).Contains(SabotageView);
        await Assert.That(caught!.Message).Contains(SabotageView);
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
    /// <remarks>
    /// <para>
    /// The start goes through <see cref="StartGuard" />, which is a leak guard rather than tidiness:
    /// every call site has the shape
    /// <c>await using PostgreSqlContainer container = await StartBareContainerAsync();</c>, so the
    /// variable is bound only <b>after</b> this method returns, and a throw here would otherwise
    /// abandon a container Docker has already started. The reasoning, the Testcontainers 4.12.0
    /// observation it rests on, and the decision about a disposal that fails too all live on
    /// <see cref="StartGuard" />, at the code that implements them; <c>AssemblyInfo.cs</c> carries the
    /// suite-level history. <c>SharedPostgresCluster.StartClusterAsync</c> guards a wider region of its
    /// own and deliberately does not share this helper.
    /// </para>
    /// <para>
    /// The guard was written by matching that shape, not by capturing a failure. One test in this
    /// class was lost once under load and never reproduced over three full suite runs — which is what
    /// a one-in-six flake looks like when it does not fire. Read it as a closed leak path, not as a
    /// diagnosed and cured flake. What has changed is that the path is now executed by something:
    /// <see cref="StartGuardTests" /> drives it over a fake, because a <c>catch</c> reachable only by
    /// a broken Docker daemon is a <c>catch</c> no test in this class can ever enter.
    /// </para>
    /// </remarks>
    private static Task<PostgreSqlContainer> StartBareContainerAsync() =>
        StartGuard.StartAsync(
            new PostgreSqlBuilder("postgres:17")
                .WithDatabase("budgetoid")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build(),
            container => container.StartAsync());

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
    /// Returns every ordinary table in <c>public</c> whose rows belong to a tenant, with whether
    /// row-level security is switched on for it and the policy name its ownership requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership is read from the table's own columns, because the column <i>is</i> the definition:
    /// <c>budget_id</c> makes a table budget-owned, <c>user_id</c> makes it user-owned. Budget first
    /// where both could apply — a budget belongs to exactly one user, so the budget-keyed rule is
    /// the narrower of the two and a table carrying both must be protected by it. <c>users</c> is
    /// named outright, and that is the one place a literal is safe in this direction: the row that
    /// <i>is</i> the person has no <c>user_id</c> to be recognised by, so without the name it would
    /// look like it owned nothing. A hardcoded name here can only <b>add</b> a subject, never remove
    /// one.
    /// </para>
    /// <para>
    /// The excused tables are <see cref="UnpolicedUserOwnedTables" />, stated there rather than read
    /// from <c>RowLevelSecurityCoverage.Exemptions</c>. Reading the shared list would not remove a
    /// second executed list, it would remove this test's oracle: the deploy gate and
    /// <c>RlsCoverageTests</c> both read that classifier to <i>enforce</i>, and ADR 0011's argument
    /// that two executed lists have no adjudicator is an argument about production code paths. Here
    /// the list would be the expectation, and an expectation taken from the code under test agrees
    /// with it by construction — appending a name to <c>Exemptions</c> would silently drop that table
    /// from this test's subjects, and the exemption list would have no adversarial reader left
    /// anywhere in the suite. <c>currencies</c>, <c>webauthn_challenges</c> and
    /// <c>__EFMigrationsHistory</c> need no naming here: they carry neither ownership column, so the
    /// shape predicate already excludes them. <c>passkey_signature_counters</c> is named by no
    /// exemption and carries <c>user_id</c>, so it stays a subject here and owes <c>user_isolation</c>
    /// like any other user-owned table.
    /// </para>
    /// <para>
    /// Every input to this query is therefore <b>independent</b> of the code under test: which tables
    /// own rows, and which policy each owes, are derived here from the live schema's columns, and
    /// which tables are excused is stated here as a literal. Asking the classifier any of the three
    /// would make the expectation a restatement of the implementation — the two would agree by
    /// construction and the assertion could never fail. The same argument keeps the policy names
    /// above as literals. <c>relrowsecurity</c> is read in the same row as the discovery so "is this
    /// table owned" and "is it protected" cannot drift into two lists that disagree.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<(string Table, bool RowSecurityEnabled, string RequiredPolicy)>>
        DiscoverTenantOwnedTablesAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            select c.relname,
                   c.relrowsecurity,
                   exists (
                       select 1
                       from pg_attribute a
                       where a.attrelid = c.oid
                         and a.attname = 'budget_id'
                         and a.attnum > 0
                         and not a.attisdropped) as budget_owned
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and c.relkind = 'r'
              and c.relname <> all(@exempt)
              and (
                  c.relname = @usersTable
                  or exists (
                      select 1
                      from pg_attribute a
                      where a.attrelid = c.oid
                        and a.attname in ('budget_id', 'user_id')
                        and a.attnum > 0
                        and not a.attisdropped))
            order by c.relname
            """,
            connection);
        // Every excused name at once, so a written-down decision cannot read as a table whose policy
        // was forgotten. <> all(...) is false as soon as one element matches, and an empty array
        // excludes nothing rather than everything.
        command.Parameters.AddWithValue("exempt", UnpolicedUserOwnedTables);
        command.Parameters.AddWithValue("usersTable", UsersTable);

        List<(string, bool, string)> tables = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add((
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2) ? BudgetIsolationPolicy : UserIsolationPolicy));
        }

        return tables;
    }

    /// <summary>
    /// Counts the policies on one table twice over: all of them, and the ones carrying the name that
    /// table's ownership requires.
    /// </summary>
    /// <remarks>
    /// Both numbers are needed and neither implies the other. The total is what makes "exactly one"
    /// meaningful — these policies are permissive and permissive policies OR together, so a second
    /// one can only widen what the first allows, and a query narrowed to a name would hide the stray.
    /// The named count is what makes "one policy" mean the right policy: with two isolation rules in
    /// the schema, a table carrying the other one has a rule, enforced, and wider than its tenancy.
    /// </remarks>
    private static async Task<(int Total, int WithRequiredName)> CountIsolationPoliciesAsync(
        NpgsqlConnection connection,
        string table,
        string requiredPolicyName)
    {
        await using NpgsqlCommand command = new(
            """
            select count(*), count(*) filter (where policyname = @policy)
            from pg_policies
            where schemaname = 'public' and tablename = @table
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("policy", requiredPolicyName);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    /// <summary>
    /// Reads one policy's two predicates, the command it applies to, and whether it is permissive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own query and its own catalog columns, for the reason the discovery helper above gives at
    /// length: a test that asked <c>RowLevelSecurityCoverage</c> what a policy contains would be
    /// checking the code under test against itself. These four values are what
    /// <c>pg_policy</c> holds beyond the name and the roles, and each of the sabotages above is
    /// exactly one of them changed.
    /// </para>
    /// <para>
    /// <c>polqual</c> and <c>polwithcheck</c> come back through <c>pg_get_expr</c>, which normalizes
    /// the expression rather than echoing what was typed — so an assertion on this text is about the
    /// rule the server ended up with, not about spelling. <c>polwithcheck</c> is NULL when the clause
    /// is omitted, which is a fact one test above depends on. <c>polcmd</c> is cast to <c>text</c>
    /// because it is PostgreSQL's internal <c>"char"</c> type: <c>*</c> for <c>FOR ALL</c>,
    /// <c>r</c> for <c>FOR SELECT</c>.
    /// </para>
    /// </remarks>
    private static async Task<(string? Using, string? WithCheck, string? Command, bool Permissive)>
        ReadPolicyAsync(NpgsqlConnection connection, string table, string policy)
    {
        await using NpgsqlCommand command = new(
            """
            select pg_get_expr(p.polqual, p.polrelid),
                   pg_get_expr(p.polwithcheck, p.polrelid),
                   p.polcmd::text,
                   p.polpermissive
            from pg_policy p
            join pg_class c on c.oid = p.polrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public' and c.relname = @table and p.polname = @policy
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("policy", policy);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            // No policy of that name. Returned rather than thrown so the caller's assertions report
            // it: a sabotage whose CREATE POLICY failed leaves exactly this, and the test that says
            // "the predicate is not null" is the one that should be naming the problem.
            return (null, null, null, false);
        }

        return (
            await reader.IsDBNullAsync(0) ? null : reader.GetString(0),
            await reader.IsDBNullAsync(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3));
    }

    /// <summary>
    /// Reports whether the connection's own database still carries a NULL ACL — PostgreSQL's
    /// default, under which PUBLIC holds CONNECT and TEMPORARY.
    /// </summary>
    private static async Task<bool> DatabaseAclIsDefaultAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            "select datacl is null from pg_database where datname = current_database()",
            connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Returns which of <see cref="DatabasePrivileges" /> <paramref name="role" /> effectively holds
    /// on the connection's own database — through its own grants, through PUBLIC, or through any
    /// role it is a member of. <c>public</c> names PUBLIC itself.
    /// </summary>
    /// <remarks>
    /// <c>current_database()</c> rather than a name, so the question is asked about the database
    /// provisioning ran against and nothing else.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ReadDatabasePrivilegesAsync(
        NpgsqlConnection connection,
        string role)
    {
        await using NpgsqlCommand command = new(
            """
            select privilege
            from unnest(@privileges) as privilege
            where has_database_privilege(@role, current_database(), privilege)
            order by privilege
            """,
            connection);
        command.Parameters.AddWithValue("privileges", DatabasePrivileges);
        command.Parameters.AddWithValue("role", role);

        List<string> held = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            held.Add(reader.GetString(0));
        }

        return held;
    }

    /// <summary>
    /// Returns the application role's own entries in the connection's database ACL, each as the
    /// privilege keyword with <c>WITH GRANT OPTION</c> appended where held. Raw ACL rather than
    /// effective privileges: this is the question "does the role hold a grant of its own", which
    /// <c>has_database_privilege</c> folds together with PUBLIC and cannot answer.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadAppRoleDirectDatabaseGrantsAsync(
        NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            select a.privilege_type
                   || case when a.is_grantable then ' WITH GRANT OPTION' else '' end
            from pg_database d
            cross join lateral aclexplode(d.datacl) a
            where d.datname = current_database()
              and a.grantee = (select oid from pg_roles where rolname = @role)
            order by 1
            """,
            connection);
        command.Parameters.AddWithValue("role", DatabaseProvisioning.AppRoleName);

        List<string> grants = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            grants.Add(reader.GetString(0));
        }

        return grants;
    }

    /// <summary>The container's connection string re-pointed at another database on it.</summary>
    private static string BuildConnectionStringFor(PostgreSqlContainer container, string database) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = database,
        }.ConnectionString;

    /// <summary>
    /// Calls the reach verifier and returns what it threw, or <see langword="null" /> if it
    /// accepted. Caught as the base <see cref="InvalidOperationException" /> so the caller's
    /// <c>IsTypeOf</c> proves the exact type rather than a catch clause filtering for it.
    /// </summary>
    private static async Task<InvalidOperationException?> TryVerifyAppRoleReachAsync(
        string adminConnectionString,
        List<string> logLines)
    {
        try
        {
            await DeploymentDatabaseProvisioning.VerifyAppRoleReachAsync(
                adminConnectionString,
                log: logLines.Add);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// The problems in a caught <see cref="AppRoleReachException" /> that contain every one of
    /// <paramref name="tokens" />, matched ordinally. Empty when nothing was caught or the exception
    /// is of another type. A collection rather than a joined string, because TUnit truncates string
    /// assertions and would hide which problem was which.
    /// </summary>
    private static List<string> ProblemsNaming(
        InvalidOperationException? caught,
        params string[] tokens) =>
        ProblemsWhere(
            caught,
            problem => tokens.All(token => problem.Contains(token, StringComparison.Ordinal)));

    /// <summary>
    /// The problems in a caught <see cref="AppRoleReachException" /> that carry
    /// <paramref name="ruleClause" /> — the clause only one rule's sentence writes, matched in any
    /// case so a sentence may open with it — and every one of <paramref name="tokens" />, matched
    /// ordinally.
    /// </summary>
    private static List<string> ProblemsCarrying(
        InvalidOperationException? caught,
        string ruleClause,
        params string[] tokens) =>
        ProblemsWhere(
            caught,
            problem => problem.Contains(ruleClause, StringComparison.OrdinalIgnoreCase)
                && tokens.All(token => problem.Contains(token, StringComparison.Ordinal)));

    /// <summary>
    /// The problems in a caught <see cref="AppRoleReachException" /> that satisfy
    /// <paramref name="predicate" />; empty when nothing was caught or the exception is of another
    /// type.
    /// </summary>
    private static List<string> ProblemsWhere(
        InvalidOperationException? caught,
        Func<string, bool> predicate) =>
        (caught as AppRoleReachException)?.Problems.Where(predicate).ToList() ?? [];

    /// <summary>
    /// Whether <paramref name="text" /> contains <paramref name="word" /> bounded by non-word
    /// characters, so a database called <c>budgetoid</c> is not found inside <c>budgetoid_app</c>.
    /// </summary>
    private static bool NamesWholeWord(string text, string word) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            text, $@"\b{System.Text.RegularExpressions.Regex.Escape(word)}\b");

    /// <summary>
    /// Reads one boolean column of <c>pg_roles</c> for the application role. The column name is a
    /// constant of this class, never input.
    /// </summary>
    private static Task<bool> ReadAppRoleFlagAsync(NpgsqlConnection connection, string column) =>
        ScalarBoolAsync(
            connection,
            $"select {column} from pg_roles where rolname = '{DatabaseProvisioning.AppRoleName}'");

    /// <summary>Counts <c>pg_default_acl</c> rows, which a freshly provisioned database has none of.</summary>
    private static async Task<int> CountDefaultAclRowsAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new("select count(*) from pg_default_acl", connection);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Runs a query returning one boolean, used for the sabotage preconditions. The SQL is built from
    /// constants of this class rather than from input.
    /// </summary>
    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Runs a query returning one string.</summary>
    private static async Task<string> ScalarStringAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Logs in as the application role and tries to create a temporary table, reporting the SQLSTATE
    /// it was refused with, or <see langword="null" /> if it was allowed. Pooling off for the reason
    /// <see cref="TryLoginAsAppRoleAsync" /> gives.
    /// </summary>
    private static async Task<string?> TryCreateTempTableAsAppRoleAsync(string adminConnectionString)
    {
        string connectionString =
            new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Username = DatabaseProvisioning.AppRoleName,
                Password = AppRolePassword,
                Pooling = false,
            }.ConnectionString;

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        try
        {
            await using NpgsqlCommand command = new(
                "create temp table provisioning_probe (x int)", connection);
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
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
