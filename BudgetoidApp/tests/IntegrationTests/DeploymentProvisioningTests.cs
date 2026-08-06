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
    /// Both carry <c>user_id</c>, so a query that asked only "does this table own rows" would demand
    /// a policy neither may have. <c>credentials</c> is read to answer "who is asking", and a policy
    /// keyed on that answer would refuse the question that produces it. <c>passkey_public_keys</c> is
    /// read to decide whether the signature on an assertion is genuine, which a WebAuthn ceremony has
    /// to settle before it knows whose account it is. Each is read before the request has an identity
    /// a policy could be keyed on; both reasons are written down in
    /// <c>RowLevelSecurityCoverage.Exemptions</c> and argued in docs/decisions/0011 and
    /// docs/decisions/0012.
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
    /// Only tenant-owned exemptions belong in it, which is why there are two names here and five in
    /// the shared list. <c>currencies</c>, <c>webauthn_challenges</c> and <c>__EFMigrationsHistory</c>
    /// carry neither ownership column, so the query's own shape predicate already excludes them;
    /// naming them here would turn an independent statement into a copy of a list and invite somebody
    /// to keep the two mechanically in sync. A future exemption on a table with no ownership column
    /// therefore stays green, because it was never a subject.
    /// </para>
    /// </remarks>
    private static readonly string[] UnpolicedUserOwnedTables = ["credentials", "passkey_public_keys"];

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
    /// <see cref="SabotagedTable" />: the role holds <c>UPDATE (name, type, opening_balance)</c>
    /// <b>and</b> <c>DELETE</c> on accounts, so narrowing its policy to <c>FOR SELECT</c> costs
    /// something nameable. On payees, which has no <c>DELETE</c> grant, the same narrowing would
    /// still be wrong but the demonstration would be thinner.
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
        // UPDATE (name, type, opening_balance) and DELETE on accounts, and a policy that covers only
        // SELECT leaves both of those commands with no permissive policy to satisfy — PostgreSQL
        // denies them outright. So a green gate here would have shipped a deploy where every account
        // rename and every account deletion fails in production, for every tenant, while the check
        // that exists to certify the isolation story reports it as fully covered. The failure is the
        // opposite direction from a leak and is no less a reason to refuse: the gate's claim is that
        // the rule enforced is the rule owed, and FOR SELECT is not the rule owed.
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
        // Both excused names at once, so the second written-down decision cannot read as a table
        // whose policy was forgotten. <> all(...) is false as soon as one element matches, and an
        // empty array excludes nothing rather than everything.
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
    /// Sends one statement that is expected to succeed. Used only for admin-side setup and sabotage,
    /// where the SQL is built from constants of this class rather than from input.
    /// </summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
