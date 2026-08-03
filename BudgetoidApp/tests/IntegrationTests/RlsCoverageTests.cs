using Infrastructure.Persistence.Provisioning;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the one thing no isolation test can: that <b>every</b> table in the schema is accounted
/// for — policed by a <c>budget_isolation</c> policy, or exempt for a reason someone wrote down.
/// The grant matrix and row-level security fail in opposite directions, and that asymmetry is the
/// whole reason this file exists. A new table nobody grants is simply invisible to the application
/// role — fail-closed, and the first feature that touches it fails loudly with <c>42501</c>. A new
/// table nobody writes a policy for is fully readable and writable by the role across every tenant
/// — fail-open, silent, and indistinguishable from working. So a budget-owned table needs both, and
/// only a test that derives its subject from the live schema can notice the second was forgotten.
/// </summary>
/// <remarks>
/// <para>
/// The subject is therefore <b>discovered</b> and never written down: every ordinary table in
/// <c>public</c>. What <i>is</i> written down is <see cref="Exemptions" /> — the tables that need
/// no policy, each carrying the reason it belongs to no tenant. Everything discovery finds and that
/// list does not name must be policed.
/// </para>
/// <para>
/// <b>A list of exceptions is the opposite of the hardcoded list this file used to warn against,
/// and the difference is which way each one fails.</b> A list of the tables that <i>are</i> policed
/// fails open: add the sixth table and the list still describes the five, so the test keeps passing
/// on the only day it matters. A list of the tables that are <i>exempt</i> fails closed: add the
/// sixth table and no exemption names it, so it is required to have a policy and the suite goes red
/// until someone either writes the policy or writes down why none is needed. Both are lists of five
/// names; only one of them can notice a new table. Do not "simplify" this back into a list of
/// policed tables, and do not put a filter back into discovery — either change reopens the hole.
/// </para>
/// <para>
/// Filtering discovery is exactly how this test lost <c>credentials</c>. Subjects used to be found
/// by looking for a <c>budget_id</c> column, which silently exempted every table without one — no
/// decision, no record, just absence. That is the fail-open half of the asymmetry above, reproduced
/// inside the test written to catch it, and server-side sessions, passkey public keys and wrapped
/// encryption keys would each have landed in the same blind spot.
/// <see cref="Classification_WithNoExemptions_LeavesEveryExemptTableNeedingAPolicy" /> is the guard
/// against it coming back: it classifies the same live schema against an <b>empty</b> exemption set
/// and demands the five otherwise-exempt tables come back needing a policy, which they can only do
/// while discovery still reaches tables with no <c>budget_id</c> column.
/// </para>
/// <para>
/// <c>budget_id</c> still appears below, in the opposite role: as a fact read about a table rather
/// than a filter applied to one. Every exemption's reason amounts to "this table belongs to no
/// tenant", and a <c>budget_id</c> column is what belonging to one looks like in the schema — so an
/// exempt table that grows the column has outlived its reason, and someone has to reconsider it
/// rather than inherit it.
/// </para>
/// <para>
/// Every test here runs on the superuser connection. That is not a privilege question:
/// <c>pg_class</c> and <c>pg_policies</c> describe the schema, and the schema is the same whoever
/// reads it.
/// </para>
/// <para>
/// A discovery query that silently stopped matching anything would make these assertions pass
/// vacuously, so each test asserts the list it is about is non-empty before it asserts anything
/// about that list's contents.
/// </para>
/// </remarks>
public sealed class RlsCoverageTests
{
    /// <summary>
    /// The tables that need no <c>budget_isolation</c> policy, each with the reason it needs none.
    /// The reason travels attached to the name rather than floating in a comment block above the
    /// list: attached, adding an exemption means writing a reason, and the reason reaches the
    /// failure message of whichever assertion the exemption later breaks.
    /// </summary>
    /// <remarks>
    /// The same five names with the same five reasons are written in the row-level-security section
    /// of <c>Infrastructure/Persistence/Provisioning/app-role-grants.sql</c>, beside the policies
    /// they are the complement of. That restatement is deliberate: the script is where someone adds
    /// a policy, and this is where the test decides whether one was owed.
    /// </remarks>
    private static readonly TableExemption[] Exemptions =
    [
        new("budgets", "a budget is the tenant, not a tenant's row"),
        new("users", "belongs to no budget, and provisioning reads it before one is resolved"),
        new(
            "credentials",
            "the same, and it is read to discover who is asking — before any identity exists"),
        new("currencies", "shared reference data belonging to no tenant"),
        new("__EFMigrationsHistory", "EF's own bookkeeping"),
    ];

    /// <summary>
    /// The one policy name a policed table may carry. Asserting the name and not only the count is
    /// a real strengthening: "this table is isolated by the policy the grants script writes" is a
    /// different statement from "this table has some policy on it", and only the first one can be
    /// read back out of the file that writes it.
    /// </summary>
    private const string IsolationPolicyName = "budget_isolation";

    [Test]
    public async Task Database_EnablesRowLevelSecurityOnEveryTableNeedingAPolicy()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — relrowsecurity is read alongside the discovery, because "which tables are there"
        // and "is each one protected" are one row of pg_class and splitting them would only invite
        // the two lists to drift. This stays a separate assertion from the policy check below
        // rather than folding into it: a policy on a table whose relrowsecurity is off is inert —
        // PostgreSQL keeps the definition and enforces nothing — so a table can be fully policed on
        // paper and completely open in practice.
        SchemaClassification schema = await ClassifySchemaAsync(admin, Exemptions);
        List<string> unprotected = schema.NeedingAPolicy
            .Where(table => !table.RowSecurityEnabled)
            .Select(table => table.Name)
            .ToList();

        // Assert — the non-empty check first: a discovery query that silently stopped matching
        // anything would make the real assertion below pass with nothing in it.
        await Assert.That(schema.NeedingAPolicy).IsNotEmpty();
        await Assert.That(unprotected).IsEmpty();
    }

    [Test]
    public async Task Database_GivesEveryTableNeedingAPolicyExactlyOneBudgetIsolationPolicy()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — exactly one, not at least one. These isolation policies are permissive, and
        // permissive policies OR together, so a second permissive one can only widen what the first
        // allows; anything meant to narrow has to be written AS RESTRICTIVE. A table that grew a
        // stray policy has quietly stopped meaning what the first policy says.
        SchemaClassification schema = await ClassifySchemaAsync(admin, Exemptions);
        List<string> wrongly = [];
        foreach (DiscoveredTable table in schema.NeedingAPolicy)
        {
            IReadOnlyList<(string Name, string[] Roles)> policies =
                await ReadPoliciesAsync(admin, table.Name);

            if (policies is not [(string name, string[] roles)])
            {
                wrongly.Add($"{table.Name}: {policies.Count} policies, wanted 1");
                continue;
            }

            // The name, and not only the count. A single policy called something else passes the
            // count check and can pass the role check while doing anything at all to the rows —
            // the count says a rule exists, the name says which rule, and only the named one has
            // been read by anyone here.
            if (!string.Equals(name, IsolationPolicyName, StringComparison.Ordinal))
            {
                wrongly.Add(
                    $"{table.Name}: policy is named '{name}', wanted '{IsolationPolicyName}'");
            }

            // The role check is deliberately "binds the application role" rather than "names
            // budgetoid_app and nothing else". A policy written TO PUBLIC also binds the app role —
            // it binds every non-owner role — so it is broader, not weaker, and failing it would be
            // the assertion being brittle about spelling rather than about protection. Anything
            // else means the app role is unpoliced, which is the whole failure this test is for.
            if (!roles.Contains(DatabaseProvisioning.AppRoleName) && !roles.Contains("public"))
            {
                wrongly.Add(
                    $"{table.Name}: policy '{name}' binds [{string.Join(", ", roles)}], " +
                    $"which does not include {DatabaseProvisioning.AppRoleName}");
            }
        }

        // Assert
        await Assert.That(schema.NeedingAPolicy).IsNotEmpty();
        await Assert.That(wrongly).IsEmpty();
    }

    [Test]
    public async Task Exemptions_NameOnlyTablesThatExistInTheSchema()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — an exemption whose table is gone is worse than clutter. It is a name lying in wait
        // for whatever is next called that, ready to hand it a reason written about something else
        // entirely, so exemptions have to be pinned to the schema they exempt things from instead
        // of rotting silently as it moves under them.
        SchemaClassification schema = await ClassifySchemaAsync(admin, Exemptions);
        List<string> rotted = schema.ExemptionsNamingNoTable
            .Select(exemption =>
                $"{exemption.Table}: exempt because \"{exemption.Reason}\", but no such table exists")
            .ToList();

        // Assert — this one needs no separate non-empty guard. Nothing discovered means every
        // exemption matched nothing, which is this list rather than an empty one.
        await Assert.That(rotted).IsEmpty();
    }

    [Test]
    public async Task Exemptions_CoverNoTableCarryingABudgetIdColumn()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — this is what stops an exemption outliving its reason. Every reason on the list is a
        // variation of "belongs to no tenant", so the moment an exempt table becomes budget-owned
        // the exemption is a claim nobody has re-checked, and it would otherwise be inherited in
        // silence by exactly the kind of table this file exists to police.
        SchemaClassification schema = await ClassifySchemaAsync(admin, Exemptions);
        List<string> nowBudgetOwned = schema.Exempt
            .Where(exempt => exempt.Table.HasBudgetIdColumn)
            .Select(exempt =>
                $"{exempt.Table.Name}: exempt because \"{exempt.Exemption.Reason}\", " +
                "but it now carries budget_id")
            .ToList();

        // Assert — non-empty first, for the usual reason: with nothing discovered there is no
        // exempt table left to carry the column and the real assertion would pass on an empty list.
        await Assert.That(schema.Exempt).IsNotEmpty();
        await Assert.That(nowBudgetOwned).IsEmpty();
    }

    [Test]
    public async Task Classification_WithNoExemptions_LeavesEveryExemptTableNeedingAPolicy()
    {
        // Arrange — the same live schema, handed an empty exemption set. The set is a parameter of
        // the helper precisely so this test can pass a different one; a helper that reached for
        // Exemptions itself could not be tested at all, only trusted.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        SchemaClassification schema = await ClassifySchemaAsync(admin, []);
        List<string> needingAPolicy = schema.NeedingAPolicy.Select(table => table.Name).ToList();

        // Assert — with nothing exempted, the five tables the real runs exempt must each come back
        // needing a policy. None of them has a budget_id column, so they can only appear here while
        // discovery is still "every ordinary table in public"; narrow it back to a column and this
        // test goes red on its own. It is a standing guard on the mechanism rather than on the
        // schema — against discovery being filtered again, and against a classification that
        // quietly drops what no exemption names instead of demanding a policy for it.
        foreach (TableExemption exemption in Exemptions)
        {
            await Assert.That(needingAPolicy).Contains(exemption.Table);
        }

        // Nothing was exempted, so nothing may be reported as exempt, and no exemption can be
        // reported as naming a missing table. Both would mean the classification invented an entry
        // the caller never supplied.
        await Assert.That(schema.Exempt).IsEmpty();
        await Assert.That(schema.ExemptionsNamingNoTable).IsEmpty();
    }

    /// <summary>
    /// Reads the live schema and sorts every ordinary table in <c>public</c> against
    /// <paramref name="exemptions" />: whatever no exemption names needs a policy, whatever one
    /// names is exempt, and any exemption naming nothing is reported rather than dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exemption set is a parameter and not a reach for <see cref="Exemptions" />, so that
    /// <see cref="Classification_WithNoExemptions_LeavesEveryExemptTableNeedingAPolicy" /> can
    /// classify the same schema against an empty set and prove this mechanism still sees the tables
    /// the real set hides. Hardwiring the real list here would leave the widened discovery with
    /// nothing to check it and only a comment to promise it.
    /// </para>
    /// <para>
    /// Names match ordinally. <c>pg_class</c> stores <c>__EFMigrationsHistory</c> exactly as EF
    /// quotes it, and a loose comparison here would let an exemption claim a table it does not
    /// name — which is the failure this whole file is about, spelled differently.
    /// </para>
    /// </remarks>
    private static async Task<SchemaClassification> ClassifySchemaAsync(
        NpgsqlConnection connection,
        IReadOnlyList<TableExemption> exemptions)
    {
        IReadOnlyList<DiscoveredTable> tables = await DiscoverPublicTablesAsync(connection);
        Dictionary<string, DiscoveredTable> byName =
            tables.ToDictionary(table => table.Name, StringComparer.Ordinal);

        List<ExemptTable> exempt = [];
        List<TableExemption> namingNoTable = [];
        foreach (TableExemption exemption in exemptions)
        {
            if (byName.TryGetValue(exemption.Table, out DiscoveredTable? table))
            {
                exempt.Add(new ExemptTable(exemption, table));
            }
            else
            {
                namingNoTable.Add(exemption);
            }
        }

        HashSet<string> exemptNames = exemptions
            .Select(exemption => exemption.Table)
            .ToHashSet(StringComparer.Ordinal);
        List<DiscoveredTable> needingAPolicy = tables
            .Where(table => !exemptNames.Contains(table.Name))
            .ToList();

        return new SchemaClassification(needingAPolicy, exempt, namingNoTable);
    }

    /// <summary>
    /// Returns every ordinary table in <c>public</c>, with whether row-level security is switched on
    /// for it and whether it carries a <c>budget_id</c> column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>relkind = 'r'</c> is the only condition, and it excludes kinds of object rather than
    /// tables: a view has no row-level security of its own, and neither has a sequence. Every actual
    /// table comes back and is classified afterwards, because a query that decides which tables are
    /// interesting is a query that can quietly stop finding one.
    /// </para>
    /// <para>
    /// <c>budget_id</c> is selected as a fact about the table, never used to filter it. It used to
    /// be the filter, which is how tables without the column left this test's scope with nobody
    /// deciding they should.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<DiscoveredTable>> DiscoverPublicTablesAsync(
        NpgsqlConnection connection)
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
                         and not a.attisdropped)
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and c.relkind = 'r'
            order by c.relname
            """,
            connection);

        List<DiscoveredTable> tables = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(new DiscoveredTable(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2)));
        }

        return tables;
    }

    /// <summary>
    /// Returns the policies defined on one table, with the roles each binds. Every policy, not only
    /// the ones named <c>budget_isolation</c>: the count is asserted to be exactly one, and
    /// narrowing this query to a name would hide the extra policy that assertion exists to catch.
    /// <c>roles</c> is cast to <c>text[]</c> because <c>pg_policies</c> exposes it as <c>name[]</c>,
    /// which is a catalog type rather than a thing a client reads back as strings.
    /// </summary>
    private static async Task<IReadOnlyList<(string Name, string[] Roles)>> ReadPoliciesAsync(
        NpgsqlConnection connection,
        string table)
    {
        await using NpgsqlCommand command = new(
            """
            select policyname, roles::text[]
            from pg_policies
            where schemaname = 'public' and tablename = @table
            order by policyname
            """,
            connection);
        command.Parameters.AddWithValue("table", table);

        List<(string, string[])> policies = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            policies.Add((reader.GetString(0), reader.GetFieldValue<string[]>(1)));
        }

        return policies;
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// One ordinary table as the schema describes it, before anything has decided what it owes.
    /// </summary>
    private sealed record DiscoveredTable(
        string Name,
        bool RowSecurityEnabled,
        bool HasBudgetIdColumn);

    /// <summary>
    /// One table excused from needing a <c>budget_isolation</c> policy, with the reason it needs
    /// none.
    /// </summary>
    private sealed record TableExemption(string Table, string Reason);

    /// <summary>
    /// An exemption joined to the table it actually matched, so an assertion about an exempt table
    /// can say in its failure message which claim about that table has stopped being true.
    /// </summary>
    private sealed record ExemptTable(TableExemption Exemption, DiscoveredTable Table);

    /// <summary>
    /// The schema sorted into the two buckets every table has to land in — needing a policy, or
    /// exempt — plus the exemptions that matched no table at all.
    /// </summary>
    /// <remarks>
    /// There is no third bucket, and that is the point. A table nobody has thought about is not
    /// "unknown", it is in <see cref="NeedingAPolicy" />, which is what makes a new table go red
    /// until someone classifies it.
    /// </remarks>
    private sealed record SchemaClassification(
        IReadOnlyList<DiscoveredTable> NeedingAPolicy,
        IReadOnlyList<ExemptTable> Exempt,
        IReadOnlyList<TableExemption> ExemptionsNamingNoTable);
}
