using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// Performs a deploy's database work as one operation: migrate the schema on the admin connection,
/// then provision the application role with its grants and row-level security policies, then verify
/// that the policies actually cover every tenant-owned table.
/// </summary>
/// <remarks>
/// <para>
/// These were <c>DEPLOYMENT.md</c> Steps 3 and 4 — an EF migration bundle, then
/// <c>app-role-grants.sql</c> piped through <c>psql</c> — and the ordering being manual is the
/// problem this class removes. Skipping the second step is not a visible failure: the grant matrix is
/// fail-closed and announces a missing privilege as <c>42501</c> at the first statement that needs
/// it, but row-level security is fail-open, and a granted table with no enforced policy is readable
/// and writable by the application role across every tenant, silently. A deploy that migrates and
/// forgets to provision is therefore a tenancy breach nothing reports.
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
    /// migrated, application role present with exactly its grant matrix and isolation policies, and
    /// that coverage verified. Idempotent — this runs on every deploy, so the second run is the
    /// common case.
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
    /// a table whose tenancy nobody has decided.
    /// </exception>
    public static async Task ProvisionAsync(
        string adminConnectionString,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);

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
            $"Provisioning role {DatabaseProvisioning.AppRoleName} with its grant matrix and "
            + "row-level security policies.");
        await DatabaseProvisioning.ApplyGrantsAsync(adminConnectionString, cancellationToken);

        await VerifyRowLevelSecurityCoverageAsync(adminConnectionString, log, cancellationToken);
    }

    /// <summary>
    /// Asserts that every table in the live schema is accounted for — policed by the isolation policy
    /// its own ownership requires, or excused by a written-down exemption — and throws naming the
    /// tables that are not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It verifies row-level security and nothing else, which is why it is not named for provisioning
    /// as a whole: a missing grant is fail-closed and reports itself as <c>42501</c> the first time it
    /// matters, so there is nothing silent there to verify.
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
    /// Exactly one policy, not at least one: these policies are permissive and permissive policies OR
    /// together, so a second one can only widen what the first allows — a table that grew a stray
    /// policy has quietly stopped meaning what its isolation policy says. And exactly the <i>named</i>
    /// one, because a count answers "is a rule enforced here" while the deploy has to answer "is the
    /// rule enforced here the rule this table owes". Those come apart silently: with two isolation
    /// rules in the schema, a budget-owned table carrying <c>user_isolation</c> has a real, enforced
    /// policy that is simply wider than its tenancy.
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
    /// nobody has decided.
    /// </exception>
    public static async Task VerifyRowLevelSecurityCoverageAsync(
        string adminConnectionString,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);

        SchemaClassification schema;
        await using (NpgsqlConnection connection = new(adminConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            schema = RowLevelSecurityCoverage.Classify(
                await RowLevelSecurityCoverage.DiscoverAsync(connection, cancellationToken),
                RowLevelSecurityCoverage.Exemptions);
        }

        // Measured on the tables that need a policy rather than on the count discovery returned,
        // because discovery now returns every table in public and a bare count would be satisfied by
        // a database holding nothing but __EFMigrationsHistory — every table exempt, every check
        // below passing with nothing in it. It is still not the coverage exception: no table is
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
        log?.Invoke(
            $"Verifying row-level security on {schema.NeedingAPolicy.Count} tenant-owned table(s) "
            + $"({budgetOwned} budget-owned, {userOwned} user-owned), with {schema.Exempt.Count} "
            + $"exempt: "
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

        foreach (ClassifiedTable classified in schema.NeedingAPolicy)
        {
            DiscoveredTable table = classified.Table;

            // Checked first and on its own, because it dominates everything below: a table with
            // relrowsecurity off is open no matter what its policies say. PostgreSQL keeps the
            // definitions and enforces none of them, so this is fully policed on paper and readable
            // across every tenant in practice — the failure mode a policy-only check calls healthy.
            if (!table.RowSecurityEnabled)
            {
                unprotected.Add(table.Name);
                problems.Add($"{table.Name} has row-level security disabled.");
                continue;
            }

            if (table.Policies is not [TablePolicy policy])
            {
                unprotected.Add(table.Name);
                problems.Add(
                    $"{table.Name} has {table.Policies.Count} policies, wanted exactly 1.");
                continue;
            }

            List<string> wrong = [];
            if (!string.Equals(policy.Name, classified.RequiredPolicyName, StringComparison.Ordinal))
            {
                wrong.Add(
                    $"{table.Name} is policed by '{policy.Name}', wanted "
                    + $"'{classified.RequiredPolicyName}'.");
            }

            // "Binds the application role", not "names it and nothing else". A policy written
            // TO PUBLIC binds every non-owner role, so it covers the app role too and is broader
            // rather than weaker; refusing it would make this check brittle about spelling instead
            // of about protection. Anything else leaves the app role unpoliced.
            if (!policy.Roles.Contains(DatabaseProvisioning.AppRoleName)
                && !policy.Roles.Contains("public"))
            {
                wrong.Add(
                    $"{table.Name} is policed by '{policy.Name}', which binds "
                    + $"[{string.Join(", ", policy.Roles)}] and so does not bind "
                    + $"{DatabaseProvisioning.AppRoleName}.");
            }

            if (wrong.Count > 0)
            {
                unprotected.Add(table.Name);
                problems.AddRange(wrong);
            }
        }

        if (unprotected.Count > 0)
        {
            throw new RowLevelSecurityCoverageException(unprotected, problems);
        }

        log?.Invoke(
            "Row-level security covers every tenant-owned table with exactly the one isolation "
            + "policy its ownership requires, and every other table is exempt for a written reason.");
    }
}
