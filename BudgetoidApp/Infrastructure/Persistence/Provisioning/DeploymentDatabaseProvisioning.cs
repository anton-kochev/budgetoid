using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// Performs a deploy's database work as one operation: migrate the schema on the admin connection,
/// then provision the application role with its grants and row-level security policies, then verify
/// that the policies actually cover every tenant-owned table and that, over the catalogs
/// <see cref="AppRoleReach"/> reads, the role reaches nothing the grant script leaves behind.
/// </summary>
/// <remarks>
/// <para>
/// These were <c>DEPLOYMENT.md</c> Steps 3 and 4 — an EF migration bundle, then
/// <c>app-role-grants.sql</c> piped through <c>psql</c> — and the ordering being manual is the
/// problem this class removes. Skipping the second step is not a visible failure: a <i>missing</i>
/// grant is fail-closed and announces itself as <c>42501</c> at the first statement that needs it,
/// but row-level security is fail-open, and a granted table with no enforced policy is readable and
/// writable by the application role across every tenant, silently. A deploy that migrates and
/// forgets to provision is therefore a tenancy breach nothing reports.
/// </para>
/// <para>
/// An <i>extra</i> grant is fail-open and silent too, which is why grants are verified as well —
/// but only the part a re-run cannot converge. The table and column matrix is re-converged by every
/// run of the script, which <c>REVOKE</c>s all on every table and sequence in <c>public</c> from the
/// role before re-granting, and <c>AppRoleGrantMatrixTests</c> holds it in CI. That convergence
/// takes back only the entries recorded against the script's own grantor. What a re-run leaves
/// standing, as far as <see cref="AppRoleReach"/> reads it, is <see cref="VerifyAppRoleReachAsync"/>;
/// its remarks list the categories and what is not read.
/// </para>
/// <para>
/// It does <b>not</b> attach a credential to the role, and that omission is the same argument read the
/// other way round. Provisioning leaves the role loginable with no credential; binding it to the API's
/// managed identity is <see cref="DatabaseProvisioning.AttachAppRoleIdentityAsync"/>, and forgetting
/// <i>that</i> fails loudly at the first login with <c>28P01</c> rather than silently granting
/// cross-tenant access. Only the fail-open half needs the ordering guarantee this class provides,
/// which is why one step is in here and the other is not.
/// </para>
/// <para>
/// This is the deploy-time entry point; <see cref="DatabaseProvisioning"/> remains the piece that
/// owns the role, its grants and its policies, and is what the test hosts and the Development startup
/// block call directly.
/// </para>
/// </remarks>
public static class DeploymentDatabaseProvisioning
{
    /// <summary>
    /// Brings an empty or already-deployed database to the state the application expects: schema
    /// migrated, application role present with its grant matrix and isolation policies, and both
    /// that coverage and the role's reach verified. Idempotent — this runs on every deploy, so the
    /// second run is the common case.
    /// </summary>
    /// <remarks>
    /// The role is left <b>credential-free</b>, and a deploy is not finished until it has attached one
    /// with <see cref="DatabaseProvisioning.AttachAppRoleIdentityAsync"/>. That step is outside this
    /// method on purpose, not by oversight: omitting it is discovered at the first login attempt as
    /// <c>28P01</c>, whereas omitting the provisioning inside this method leaves a granted table with
    /// no enforced policy — readable across every tenant, silently. Fail-loud work needs no ordering
    /// guarantee; the fail-open work is the only reason a single method sequences anything at all.
    /// </remarks>
    /// <param name="adminConnectionString">
    /// Connection string for a role that owns the schema and may create roles. The migration cannot
    /// run on the least-privilege application role at all; see the <c>__EFMigrationsHistory</c> note
    /// in <c>app-role-grants.sql</c>.
    /// </param>
    /// <param name="log">
    /// Sink for a running account of the work. The deploy pipeline's only window into this call: a
    /// run that reported nothing reads identically to a run that did nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the provisioning run.</param>
    /// <exception cref="RowLevelSecurityCoverageException">
    /// Provisioning ran but left at least one tenant-owned table unprotected, or the schema contains
    /// a table whose tenancy nobody has decided, or a relation no policy can cover.
    /// </exception>
    /// <exception cref="AppRoleReachException">
    /// Provisioning ran, but the application role is missing, cannot log in, or reaches something
    /// the grant script did not take back.
    /// </exception>
    public static Task ProvisionAsync(
        string adminConnectionString,
        Action<string>? log = null,
        CancellationToken cancellationToken = default) =>
        ProvisionAsync(adminConnectionString, AppRole.Production, log, cancellationToken);

    /// <summary>
    /// <see cref="ProvisionAsync(string, Action{string}, CancellationToken)" /> for <paramref name="role" />. Internal so only the integration suite can name a role other than
    /// <see cref="AppRole.Production" />; see <see cref="AppRole" />.
    /// </summary>
    internal static async Task ProvisionAsync(
        string adminConnectionString,
        AppRole role,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);
        ArgumentNullException.ThrowIfNull(role);

        // Built directly over the admin connection string rather than resolved from DI, exactly as
        // the test hosts do. The context's IBudgetContext is optional precisely so a migration path
        // can build one: migration state is a property of the database, not of a tenant.
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(adminConnectionString)
                .Options);

        // Reported before it happens, because on the first production run this is the evidence that
        // MigrateAsync no-ops against the schema that was applied by hand.
        List<string> pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken))
            .ToList();
        log?.Invoke(pending.Count == 0
            ? "No pending migrations; the schema is already at the latest migration."
            : $"Applying {pending.Count} pending migration(s): {string.Join(", ", pending)}.");

        await db.Database.MigrateAsync(cancellationToken);

        // Strictly after the migration: the grants and the policies name individual tables, so the
        // schema has to exist first.
        log?.Invoke(
            $"Provisioning role {role.Name} with its grant matrix and "
            + "row-level security policies.");
        await DatabaseProvisioning.ApplyGrantsAsync(adminConnectionString, role, cancellationToken);

        await VerifyRowLevelSecurityCoverageAsync(adminConnectionString, role, log, cancellationToken);

        // After the script, never before it: a widening the script converges away is no longer
        // there to report, so only what a re-run left standing refuses the deploy.
        await VerifyAppRoleReachAsync(adminConnectionString, role, log, cancellationToken);
    }

    /// <summary>
    /// Asserts that every relation in the live schema is accounted for — policed by the isolation
    /// policy its own ownership requires, excused by a written-down exemption, or reported as
    /// something no policy can cover — and throws naming the ones that are not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It verifies row-level security and nothing else, which is why it is not named for provisioning
    /// as a whole. Grants are the other half: a missing grant is fail-closed and reports itself as
    /// <c>42501</c> the first time it matters, but an extra one is fail-open and silent, and that
    /// half is <see cref="VerifyAppRoleReachAsync" />.
    /// </para>
    /// <para>
    /// The subject and the rule come from <see cref="RowLevelSecurityCoverage" /> rather than from a
    /// query written here, and that sharing is the one deviation from this repository's general
    /// preference for deliberate restatement. The preference holds for restatements read by people —
    /// the prose in <c>app-role-grants.sql</c> is one, and it is worth keeping. This would be a second
    /// <b>executed</b> list of which tables must be policed, and two executed lists that disagree have
    /// no adjudicator: whichever one loses simply stops noticing a table, which is the fail-open
    /// failure this verifier exists to catch, reproduced inside it. That is not hypothetical here —
    /// the query this method used to own found its subjects by looking for a <c>budget_id</c> column,
    /// so it could not see <c>users</c> at all and reported full coverage on a schema where every
    /// person's row was readable by a session that named somebody else. Do not "restore" the local
    /// copy.
    /// </para>
    /// <para>
    /// The per-table rules are <see cref="RowLevelSecurityCoverage.FindProblems" /> and are no longer
    /// spelled out here, for that same reason one layer down: "what a protected table looks like" was
    /// executed both here and in the coverage suite, and the copy the tests do not read is the copy
    /// that rots.
    /// </para>
    /// <para>
    /// Those rules read the policy's <b>content</b> and not only its identity, which is a second axis
    /// rather than a stricter version of the first. Exactly one policy, carrying exactly the required
    /// name, bound to the application role, answers "is a rule enforced here"; a policy satisfying
    /// all three while reading <c>USING (true)</c> answers it and isolates nothing. So the session
    /// setting it is keyed on and the ownership column it decides tenancy by are demanded too, and
    /// its <c>WITH CHECK</c> half is required to match its <c>USING</c> half — a write that can land
    /// where a read cannot reach is worse than a leak, because the row vanishes into another tenant
    /// and nobody is left able to see it.
    /// </para>
    /// <para>
    /// <b>The limit is stated rather than left implied.</b> Those checks catch drift, accident and a
    /// hand-edit that weakened one clause. They are not proof against a deliberately crafted
    /// wider-but-plausible predicate — <c>… OR true</c> reads the setting, names the column, and
    /// passes. That is outside the threat model: anyone able to rewrite <c>pg_policy</c> can
    /// <c>DISABLE ROW LEVEL SECURITY</c> instead, and this gate would have nothing to say about that
    /// either.
    /// </para>
    /// </remarks>
    /// <param name="adminConnectionString">
    /// Connection string used to read the catalogs. Any role can be used: <c>pg_class</c>,
    /// <c>pg_policy</c> and <c>pg_roles</c> describe the schema, and the schema reads the same whoever
    /// asks.
    /// </param>
    /// <param name="log">Sink for what was inspected; written on the failure path too.</param>
    /// <param name="cancellationToken">Cancels the catalog query.</param>
    /// <exception cref="InvalidOperationException">
    /// The schema contains no table that needs a policy at all, which means the database is not
    /// migrated and there is nothing to have verified.
    /// </exception>
    /// <exception cref="RowLevelSecurityCoverageException">
    /// At least one tenant-owned table is unprotected, or the schema contains a table whose tenancy
    /// nobody has decided, or a relation no policy can cover.
    /// </exception>
    public static Task VerifyRowLevelSecurityCoverageAsync(
        string adminConnectionString,
        Action<string>? log = null,
        CancellationToken cancellationToken = default) =>
        VerifyRowLevelSecurityCoverageAsync(adminConnectionString, AppRole.Production, log, cancellationToken);

    /// <summary>
    /// <see cref="VerifyRowLevelSecurityCoverageAsync(string, Action{string}, CancellationToken)" />, judging whether each policy binds <paramref name="role" />. Internal so only the integration suite can name a role other than
    /// <see cref="AppRole.Production" />; see <see cref="AppRole" />.
    /// </summary>
    internal static async Task VerifyRowLevelSecurityCoverageAsync(
        string adminConnectionString,
        AppRole role,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);
        ArgumentNullException.ThrowIfNull(role);

        SchemaClassification schema;
        await using (NpgsqlConnection connection = new(adminConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            schema = RowLevelSecurityCoverage.Classify(
                await RowLevelSecurityCoverage.DiscoverAsync(connection, cancellationToken),
                RowLevelSecurityCoverage.Exemptions);
        }

        // Measured on the tables that need a policy rather than on the count discovery returned,
        // because discovery now returns every relation in public — views and materialized views
        // included — and a bare count would be satisfied by a database holding nothing but
        // __EFMigrationsHistory and a stray view. It is still not the coverage exception: no table is
        // unprotected, the schema simply is not there.
        if (schema.NeedingAPolicy.Count == 0)
        {
            throw new InvalidOperationException(
                "Found no tenant-owned tables in schema 'public', so row-level security coverage "
                + "could not be verified. A migrated database always has some; check that the "
                + "migration ran against this database before provisioning did.");
        }

        // Logged before the verdict rather than only on success: called on its own this is a whole
        // deploy step, and an operator reading a refusal needs to see that the check ran against the
        // database they think it did, not only that it failed. The counts are broken out by ownership
        // because "policed" is no longer one thing — a run that had quietly lost the user-owned half
        // would otherwise report a plausible total.
        int budgetOwned = schema.NeedingAPolicy.Count(
            classified => classified.Table.Ownership == TableOwnership.BudgetOwned);
        int userOwned = schema.NeedingAPolicy.Count - budgetOwned;

        // The unpoliceable count is in the line rather than only in the refusal, because a bucket
        // that is only ever mentioned when it is non-empty reads, on every green deploy, exactly like
        // a bucket nobody inspected.
        log?.Invoke(
            $"Verifying row-level security on {schema.NeedingAPolicy.Count} tenant-owned table(s) "
            + $"({budgetOwned} budget-owned, {userOwned} user-owned), with {schema.Exempt.Count} "
            + $"exempt and {schema.Unpoliceable.Count} unpoliceable: "
            + $"{string.Join(", ", schema.NeedingAPolicy.Select(entry => entry.Table.Name))}.");

        List<string> unprotected = [];
        List<string> problems = [];

        // Refused rather than waved through, because "we forgot to police this" and "this genuinely
        // needs no policy" produce the identical catalog and only a person can tell them apart. The
        // message has to carry both ways forward: without them the next contributor clears a red
        // deploy by exempting whatever the verifier named, which is the one resolution that is wrong
        // exactly when it matters.
        foreach (DiscoveredTable table in schema.Unclassifiable)
        {
            unprotected.Add(table.Name);
            problems.Add(
                $"{table.Name} carries neither budget_id nor user_id, so nobody can say whether it "
                + $"owes {RowLevelSecurityCoverage.BudgetIsolationPolicyName} or "
                + $"{RowLevelSecurityCoverage.UserIsolationPolicyName}; give it an ownership column, "
                + "or add it to RowLevelSecurityCoverage.Exemptions with a written reason.");
        }

        // The same refusal, for the relations a policy cannot be attached to at all. Beside the loop
        // above rather than folded into it, because the decision being asked for is a different one:
        // unclassifiable asks which tenant owns these rows, unpoliceable asks why the relation exists
        // and what stops the application role reading every tenant through it. A view is the sharp
        // case — it runs as its owner, and an owner is not subject to row-level security, so it
        // routes around policies that are all present and all correct.
        foreach (DiscoveredTable relation in schema.Unpoliceable)
        {
            unprotected.Add(relation.Name);
            problems.Add(RowLevelSecurityCoverage.DescribeUnpoliceable(relation));
        }

        // Delegated rather than inlined: the coverage suite reads the same function, and two executed
        // copies of "what a protected table looks like" have no adjudicator when they disagree.
        foreach (ClassifiedTable classified in schema.NeedingAPolicy)
        {
            IReadOnlyList<string> found = RowLevelSecurityCoverage.FindProblems(classified, role);
            if (found.Count > 0)
            {
                unprotected.Add(classified.Table.Name);
                problems.AddRange(found);
            }
        }

        if (unprotected.Count > 0)
        {
            throw new RowLevelSecurityCoverageException(unprotected, problems);
        }

        log?.Invoke(
            "Row-level security covers every tenant-owned table with exactly the one isolation "
            + "policy its ownership requires, keyed on the session setting and the ownership column "
            + "that tenancy calls for, and every other relation is exempt for a written reason.");
    }

    /// <summary>
    /// Asserts that the application role exists, can log in, and — over the catalogs
    /// <see cref="AppRoleReach"/> reads — reaches nothing beyond its grant matrix that a re-run of
    /// <c>app-role-grants.sql</c> would leave in place, and throws naming every finding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grant script converges what it grants, not what it never mentions. Role attributes are no
    /// grant, and <c>PUBLIC</c> grants, memberships, default privileges, ownership and stored session
    /// defaults all survive its <c>REVOKE ALL … FROM budgetoid_app</c>. So do grants in any schema but
    /// <c>public</c>, which the script never names, and grants to the role recorded against another
    /// grantor, because a <c>REVOKE</c> takes back only the entries recorded against its own. So do
    /// the paths that write a column with no <c>UPDATE</c> on it — triggers, rewrite rules,
    /// referential actions and generated columns — which are read here on every deploy, against the
    /// live database. A missing grant announces itself as <c>42501</c>; an extra one is fail-open and
    /// silent, which is why it is verified here. The rules, and what they do not read, are
    /// <see cref="AppRoleReach" />'s.
    /// </para>
    /// <para>
    /// This does not collide with the "restate, don't share" argument <c>AppRoleGrantMatrixTests</c>
    /// makes against a second executed copy of the grant matrix, because it is not one: its expected
    /// answer is a fixed "nothing" rather than a list of tables and columns, and the matrix itself is
    /// left to the script's convergence and that test.
    /// </para>
    /// <para>
    /// It is called by <see cref="ProvisionAsync" /> and again by the deploy tool after the managed
    /// identity is attached. That step hands the role to Azure's <c>pgaadauth</c> label provider,
    /// which no test here can run, so whether it changes the role's reach is found out by the deploy
    /// rather than assumed.
    /// </para>
    /// <para>
    /// It has to connect as the principal that ran the grant script: on a schema, a grant recorded
    /// against the one grant-option holder that principal inherits is its own, and only a connection
    /// as that principal can tell. Both call sites pass the connection string they provisioned with.
    /// </para>
    /// </remarks>
    /// <param name="adminConnectionString">
    /// Connection string for the principal that ran <c>app-role-grants.sql</c>, used to read the
    /// catalogs.
    /// </param>
    /// <param name="log">Sink for what was inspected; written on the failure path too.</param>
    /// <param name="cancellationToken">Cancels the catalog reads.</param>
    /// <exception cref="AppRoleReachException">
    /// The application role is missing, cannot log in, or reaches at least one thing its grant
    /// matrix does not give it.
    /// </exception>
    public static Task VerifyAppRoleReachAsync(
        string adminConnectionString,
        Action<string>? log = null,
        CancellationToken cancellationToken = default) =>
        VerifyAppRoleReachAsync(adminConnectionString, AppRole.Production, log, cancellationToken);

    /// <summary>
    /// <see cref="VerifyAppRoleReachAsync(string, Action{string}, CancellationToken)" /> for <paramref name="role" />. Internal so only the integration suite can name a role other than
    /// <see cref="AppRole.Production" />; see <see cref="AppRole" />.
    /// </summary>
    internal static async Task VerifyAppRoleReachAsync(
        string adminConnectionString,
        AppRole role,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);
        ArgumentNullException.ThrowIfNull(role);

        AppRoleReachSnapshot snapshot;
        await using (NpgsqlConnection connection = new(adminConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            snapshot = await AppRoleReach.DiscoverAsync(
                connection,
                role.Name,
                cancellationToken);
        }

        // Counts before the verdict, for the reason VerifyRowLevelSecurityCoverageAsync gives: a
        // refusal or a pass is only worth reading once the operator can see what was inspected, and a
        // category that read zero rows looks, on a green deploy, exactly like one nobody read.
        log?.Invoke(snapshot.Attributes is null
            ? $"Verifying the reach of role {snapshot.RoleName}: no role of that name exists."
            : $"Verifying the reach of role {snapshot.RoleName}: read {snapshot.MemberOf.Count} "
              + $"membership(s), {snapshot.SchemaGrants.Count} schema privilege(s), "
              + $"{snapshot.DefaultPrivileges.Count} default privilege(s), "
              + $"{snapshot.OwnedObjects.Count} owned object(s), "
              + $"{snapshot.PublicRelationGrants.Count} PUBLIC relation privilege(s), "
              + $"{snapshot.OutsidePublicRelationGrants.Count} relation privilege(s) outside "
              + "schema public, "
              + $"{snapshot.ParameterGrants.Count} parameter privilege(s), "
              + $"{snapshot.ExecutableRoutines.Count} executable routine privilege(s), "
              + $"{snapshot.DatabaseGrants.Count} database privilege(s), "
              + $"{snapshot.NonOwnerGrants.Count} privilege(s) granted by a non-owner, "
              + $"{snapshot.SystemSchemaGrants.Count} system-schema privilege(s) granted to it by "
              + "name, "
              + $"{snapshot.TablespaceGrants.Count} tablespace privilege(s), "
              + $"{snapshot.SessionDefaults.Count} stored session default(s), "
              + $"{snapshot.Triggers.Count} user-defined trigger(s), "
              + $"{snapshot.Rules.Count} rewrite rule(s) beyond a view's own, "
              + $"{snapshot.ReferentialActionWrites.Count} column(s) a referential action writes "
              + "past its UPDATE and "
              + $"{snapshot.GeneratedColumnWrites.Count} generated column(s) rewritten past its "
              + "UPDATE.");

        IReadOnlyList<string> problems = AppRoleReach.FindProblems(snapshot);
        if (problems.Count > 0)
        {
            throw new AppRoleReachException(problems);
        }

        log?.Invoke(
            $"Role {snapshot.RoleName}, over the catalogs this check reads, holds no attribute, "
            + "membership, ownership, default privilege, PUBLIC relation grant, relation grant "
            + "outside schema public, system-schema grant by name, grant made by anyone but the "
            + "object's owner or this principal's own grant-option holder, parameter, routine or "
            + "tablespace privilege, schema or database privilege beyond its grant matrix, or stored "
            + "session default; no user-defined trigger or added rewrite rule exists; and no "
            + "referential action or generated column writes a column it cannot UPDATE.");
    }
}
