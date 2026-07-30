using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// Performs a deploy's database work as one operation: migrate the schema on the admin connection,
/// then provision the application role with its grants and row-level security policies, then verify
/// that the policies actually cover every budget-owned table.
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
    /// Every ordinary table in <c>public</c> carrying a <c>budget_id</c> column, with whether
    /// row-level security is enforced for it and how many policies it has.
    /// </summary>
    /// <remarks>
    /// The <c>budget_id</c> column <i>is</i> the definition of budget-owned, so the subject is
    /// derived from the live schema rather than from a list of the five names that exist today — a
    /// hardcoded list would keep passing on the day someone adds the sixth, which is the only day
    /// this check matters. <c>relrowsecurity</c> and the policy count are read in the same row as the
    /// discovery so the three facts cannot drift into lists that disagree. Policies are counted
    /// through <c>pg_policy.polrelid</c> rather than the <c>pg_policies</c> view's table
    /// <i>name</i>, because the oid cannot match a same-named table in another schema.
    /// </remarks>
    private const string BudgetOwnedTableCoverageSql =
        """
        select c.relname,
               c.relrowsecurity,
               (select count(*) from pg_policy p where p.polrelid = c.oid)
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
        """;

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
    /// Provisioning ran but left at least one budget-owned table unprotected.
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
    /// Asserts that every budget-owned table in the live schema enforces row-level security and
    /// carries exactly one policy, and throws naming the tables that do not.
    /// </summary>
    /// <remarks>
    /// It verifies row-level security and nothing else, which is why it is not named for provisioning
    /// as a whole: a missing grant is fail-closed and reports itself as <c>42501</c> the first time it
    /// matters, so there is nothing silent there to verify. Exactly one policy, not at least one:
    /// these policies are permissive and permissive policies OR together, so a second one can only
    /// widen what the first allows — a table that grew a stray policy has quietly stopped meaning
    /// what its isolation policy says.
    /// </remarks>
    /// <param name="adminConnectionString">
    /// Connection string used to read the catalogs. Any role can be used: <c>pg_class</c> and
    /// <c>pg_policy</c> describe the schema, and the schema reads the same whoever asks.
    /// </param>
    /// <param name="log">Sink for what was inspected; written on the failure path too.</param>
    /// <param name="cancellationToken">Cancels the catalog query.</param>
    /// <exception cref="InvalidOperationException">
    /// The schema contains no budget-owned tables at all, which means the database is not migrated
    /// and there is nothing to have verified.
    /// </exception>
    /// <exception cref="RowLevelSecurityCoverageException">
    /// At least one budget-owned table is unprotected.
    /// </exception>
    public static async Task VerifyRowLevelSecurityCoverageAsync(
        string adminConnectionString,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);

        IReadOnlyList<(string Table, bool RowSecurityEnabled, long Policies)> tables =
            await ReadBudgetOwnedTableCoverageAsync(adminConnectionString, cancellationToken);

        // A discovery query that matched nothing would make every check below pass with nothing in
        // it, on a database with no policies at all. It is not the coverage exception: no table is
        // unprotected, the schema simply is not there.
        if (tables.Count == 0)
        {
            throw new InvalidOperationException(
                "Found no budget-owned tables in schema 'public', so row-level security coverage "
                + "could not be verified. A migrated database always has some; check that the "
                + "migration ran against this database before provisioning did.");
        }

        // Logged before the verdict rather than only on success: called on its own this is a whole
        // deploy step, and an operator reading a refusal needs to see that the check ran against the
        // database they think it did, not only that it failed.
        log?.Invoke(
            $"Verifying row-level security on {tables.Count} budget-owned table(s): "
            + $"{string.Join(", ", tables.Select(entry => entry.Table))}.");

        List<string> unprotected = [];
        List<string> problems = [];
        foreach ((string table, bool rowSecurityEnabled, long policies) in tables)
        {
            // Both halves are needed, and one without the other is a real failure mode: a table can
            // have its policy defined and listed in the catalog while row-level security is switched
            // off for it, in which case the policy is never enforced.
            if (!rowSecurityEnabled)
            {
                unprotected.Add(table);
                problems.Add($"{table} has row-level security disabled.");
            }
            else if (policies != 1)
            {
                unprotected.Add(table);
                problems.Add($"{table} has {policies} policies, wanted exactly 1.");
            }
        }

        if (unprotected.Count > 0)
        {
            throw new RowLevelSecurityCoverageException(unprotected, problems);
        }

        log?.Invoke(
            "Row-level security covers every budget-owned table with exactly one isolation policy.");
    }

    private static async Task<IReadOnlyList<(string Table, bool RowSecurityEnabled, long Policies)>>
        ReadBudgetOwnedTableCoverageAsync(
            string adminConnectionString,
            CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(BudgetOwnedTableCoverageSql, connection);

        List<(string, bool, long)> tables = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2)));
        }

        return tables;
    }
}
