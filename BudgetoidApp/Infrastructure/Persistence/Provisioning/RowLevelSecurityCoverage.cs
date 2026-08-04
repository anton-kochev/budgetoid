using Npgsql;

namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// Which kind of tenant a table's rows belong to, and therefore which isolation policy it owes.
/// </summary>
/// <remarks>
/// <see cref="None" /> is not "safe" and not a default. It means the table carries no ownership
/// column at all, which is a table whose tenancy nobody has decided yet — see
/// <see cref="SchemaClassification.Unclassifiable" /> for why that is a refusal rather than a
/// verdict.
/// </remarks>
public enum TableOwnership
{
    /// <summary>The table carries no ownership column, so its tenancy is undecided.</summary>
    None,

    /// <summary>
    /// The rows belong to one person: the table carries <c>user_id</c>, or it <i>is</i> the person.
    /// </summary>
    UserOwned,

    /// <summary>The rows belong to one budget: the table carries <c>budget_id</c>.</summary>
    BudgetOwned,
}

/// <summary>
/// A table that needs no isolation policy, with the reason and the ownership it is exempt despite.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="ExemptDespite" /> is what stops an exemption outliving its reason. Every reason
/// on the list amounts to "this table is not policed the way its shape suggests", so the shape has
/// to be recorded alongside the decision: an exempt table whose ownership has changed under it has
/// outlived the sentence attached to it and needs reconsidering rather than inheriting. Recording
/// only "exempt" would make that drift invisible; recording "exempt, and it owned nothing at the
/// time" turns it into a failing check.
/// </para>
/// </remarks>
/// <param name="Table">The table name exactly as <c>pg_class</c> stores it.</param>
/// <param name="Reason">Why this table belongs to no tenant, or cannot be policed as if it did.</param>
/// <param name="ExemptDespite">The ownership the schema is expected to still report for it.</param>
public sealed record TableExemption(string Table, string Reason, TableOwnership ExemptDespite);

/// <summary>A policy attached to a table, with the roles it binds.</summary>
/// <param name="Name">The policy name — which rule, not merely that a rule exists.</param>
/// <param name="Roles">
/// The role names the policy is written <c>TO</c>, or the single entry <c>public</c> when it binds
/// every non-owner role.
/// </param>
public sealed record TablePolicy(string Name, IReadOnlyList<string> Roles);

/// <summary>A table as the live catalog describes it, before anyone decides what it owes.</summary>
/// <param name="Name">The table name as <c>pg_class</c> stores it.</param>
/// <param name="RowSecurityEnabled">
/// <c>relrowsecurity</c>. A policy on a table with this off is inert: PostgreSQL keeps the
/// definition and enforces nothing, so a table can be fully policed on paper and open in practice.
/// </param>
/// <param name="Ownership">Read from the table's columns as a fact about it, never as a filter.</param>
/// <param name="Policies">
/// <b>All</b> of the table's policies, not the ones a caller was looking for. Callers need the count
/// as much as the contents: these policies are permissive and permissive policies OR together, so a
/// second one can only widen what the first allows.
/// </param>
public sealed record DiscoveredTable(
    string Name,
    bool RowSecurityEnabled,
    TableOwnership Ownership,
    IReadOnlyList<TablePolicy> Policies);

/// <summary>A table that must be policed, paired with the policy name its ownership requires.</summary>
/// <param name="Table">The discovered table.</param>
/// <param name="RequiredPolicyName">
/// <see cref="RowLevelSecurityCoverage.BudgetIsolationPolicyName" /> or
/// <see cref="RowLevelSecurityCoverage.UserIsolationPolicyName" />. Naming the wrong one is a real
/// policy, enforced, and wider than the tenancy the table is supposed to have.
/// </param>
public sealed record ClassifiedTable(DiscoveredTable Table, string RequiredPolicyName);

/// <summary>An exemption paired with the table it currently names.</summary>
/// <param name="Exemption">The written-down decision.</param>
/// <param name="Table">The table as the schema describes it today.</param>
public sealed record ExemptTable(TableExemption Exemption, DiscoveredTable Table);

/// <summary>
/// Every discovered table sorted into exactly one of four buckets, plus the exemptions that matched
/// nothing.
/// </summary>
/// <param name="NeedingAPolicy">Owned tables, each with the policy name its own ownership requires.</param>
/// <param name="Unclassifiable">
/// Tables carrying neither ownership column and named by no exemption. This bucket is red, not
/// empty-by-convenience: with two policy names in play there is nothing to derive for a table that
/// owns nothing, and choosing one for it would be guessing in a type whose whole job is to refuse
/// to guess.
/// </param>
/// <param name="Exempt">Tables an exemption names, so a caller can check the schema still agrees.</param>
/// <param name="ExemptionsNamingNoTable">
/// Exemptions matching no table. Reported rather than dropped: such a name lies in wait for whatever
/// is next called that, ready to hand it a reason written about something else entirely.
/// </param>
public sealed record SchemaClassification(
    IReadOnlyList<ClassifiedTable> NeedingAPolicy,
    IReadOnlyList<DiscoveredTable> Unclassifiable,
    IReadOnlyList<ExemptTable> Exempt,
    IReadOnlyList<TableExemption> ExemptionsNamingNoTable);

/// <summary>
/// Reads the live schema and decides, for every table in it, whether it must be policed by
/// row-level security and by which policy.
/// </summary>
/// <remarks>
/// <para>
/// The grant matrix and row-level security fail in <b>opposite</b> directions, and that asymmetry is
/// the entire reason this type exists. A table nobody grants is invisible to the application role —
/// fail-closed, and the first feature to touch it fails loudly with <c>42501</c>. A granted table
/// nobody writes a policy for is fully readable and writable by that role across every tenant —
/// fail-open, silent, and indistinguishable from working. Only a check that derives its subject from
/// the live schema can notice the second half was forgotten.
/// </para>
/// <para>
/// So the subject is <b>discovered</b> and never written down: every ordinary table in <c>public</c>.
/// What is written down is <see cref="Exemptions" />, and the direction of that list is the point.
/// A list of the tables that <i>are</i> policed fails open — the seventh table nobody adds to it
/// keeps every check green on the only day the check matters. A list of the tables that are
/// <i>exempt</i> fails closed — the seventh is named by no exemption, so it is required to have a
/// policy or to be classifiable at all, and it stays red until a human decides. Both are short lists
/// of names; only one of them can notice a new table. Do not "simplify" this into a list of policed
/// tables, and do not put a filter back into discovery — either change reopens the hole.
/// </para>
/// <para>
/// Filtering discovery is precisely how this check once lost <c>credentials</c>: subjects used to be
/// found by looking for a <c>budget_id</c> column, which silently exempted every table without one —
/// no decision, no record, just absence. That is the fail-open half of the asymmetry above,
/// reproduced inside the mechanism written to catch it. Both ownership columns still appear here, in
/// the opposite role: as facts read <i>about</i> a table, never as conditions applied <i>to</i> one.
/// </para>
/// <para>
/// The exemption list and the classification live in production code rather than in a test because
/// the deploy-time verifier must read the same rule the test does. Two <i>executed</i> lists that
/// disagree have no adjudicator, and the one that loses fails open — which is the failure mode this
/// whole type is about, one layer up. The prose restatement in <c>app-role-grants.sql</c> is a human
/// restatement for a different audience, not a second executed list.
/// </para>
/// <para>
/// Nothing here logs or writes: it returns data and the caller reports. A deploy tool wants an
/// exception naming the offenders, a test wants an assertion listing them, and neither shape belongs
/// in the code that reads the catalog.
/// </para>
/// </remarks>
public static class RowLevelSecurityCoverage
{
    /// <summary>The policy a budget-owned table owes, keyed on <c>app.current_budget_id</c>.</summary>
    public const string BudgetIsolationPolicyName = "budget_isolation";

    /// <summary>The policy a user-owned table owes, keyed on <c>app.current_user_id</c>.</summary>
    public const string UserIsolationPolicyName = "user_isolation";

    /// <summary>The one table that is user-owned by being the user rather than by referencing one.</summary>
    /// <remarks>
    /// Hardcoded, and that is safe in this one direction only. The row that <i>is</i> the user has no
    /// <c>user_id</c> column to be recognised by, so without the literal it would classify as owning
    /// nothing. A hardcoded name can only <b>add</b> a subject to the policed set — it can never
    /// remove one, because every other table still reaches its verdict from its columns. The
    /// dangerous direction would be a hardcoded list of what to check; this is a single name that
    /// makes the check stricter.
    /// </remarks>
    private const string UsersTableName = "users";

    /// <summary>
    /// Every ordinary table in <c>public</c>, with <c>relrowsecurity</c>, both ownership facts and
    /// all policies. <c>relkind = 'r'</c> is the only condition, and it excludes kinds of object
    /// rather than tables.
    /// </summary>
    /// <remarks>
    /// Policies are joined through <c>pg_policy.polrelid</c> rather than the <c>pg_policies</c>
    /// view's table <i>name</i>, because an oid cannot collide with a same-named table in another
    /// schema. The role expression mirrors that view: <c>polroles</c> is an oid array, <c>{0}</c>
    /// means <c>TO PUBLIC</c>, and the resolved names come back as <c>name[]</c>, which is cast to
    /// <c>text[]</c> so Npgsql reads it as strings. <c>pg_roles</c> rather than <c>pg_authid</c>:
    /// the former is world-readable, and nothing here needs the password column the latter guards.
    /// </remarks>
    private const string SchemaCoverageSql =
        """
        select c.relname,
               c.relrowsecurity,
               exists (
                   select 1
                   from pg_attribute a
                   where a.attrelid = c.oid
                     and a.attname = 'budget_id'
                     and a.attnum > 0
                     and not a.attisdropped) as budget_owned,
               exists (
                   select 1
                   from pg_attribute a
                   where a.attrelid = c.oid
                     and a.attname = 'user_id'
                     and a.attnum > 0
                     and not a.attisdropped) as user_owned,
               p.polname,
               case
                   when p.polroles = '{0}'::oid[] then array['public']::text[]
                   else array(
                       select r.rolname::text
                       from pg_roles r
                       where r.oid = any (p.polroles)
                       order by r.rolname)
               end as polroles
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        left join pg_policy p on p.polrelid = c.oid
        where n.nspname = 'public'
          and c.relkind = 'r'
        order by c.relname, p.polname
        """;

    /// <summary>
    /// The tables that need no isolation policy, each with the reason and the ownership it is exempt
    /// despite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three entries, and each one is a decision somebody made rather than a shape that fell out of a
    /// query. Adding a fourth is deliberately as visible as adding a policy: the point of the list is
    /// that a table can only leave the policed set through it.
    /// </para>
    /// <para>
    /// <c>credentials</c> is the entry that forced <see cref="TableExemption.ExemptDespite" /> to
    /// exist. It is genuinely user-owned and exempt anyway, so a blanket rule like "no exempt table
    /// may carry an ownership column" would be wrong rather than strict.
    /// </para>
    /// <para>
    /// Callers pass this to <see cref="Classify" /> rather than the classifier reaching for it, so
    /// the same schema can be classified against a different set. That is what makes discovery
    /// testable instead of merely trustworthy.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TableExemption> Exemptions { get; } =
    [
        new(
            "credentials",
            "read to discover who is asking — a policy keyed on the identity it resolves would "
            + "refuse the query that resolves it",
            TableOwnership.UserOwned),
        new(
            "currencies",
            "shared reference data belonging to no tenant",
            TableOwnership.None),
        new(
            "__EFMigrationsHistory",
            "EF's own bookkeeping, and the application role may only read it",
            TableOwnership.None),
    ];

    /// <summary>
    /// Reads every ordinary table in <c>public</c> from the catalogs on an already-open connection.
    /// </summary>
    /// <remarks>
    /// Any role may call this. <c>pg_class</c>, <c>pg_policy</c> and <c>pg_attribute</c> describe the
    /// schema, and the schema reads the same whoever asks — which is also why this is not a privilege
    /// question in the tests that use a superuser connection.
    /// </remarks>
    /// <param name="connection">An open connection to the database being inspected.</param>
    /// <param name="cancellationToken">Cancels the catalog query.</param>
    /// <returns>Every ordinary table in <c>public</c>, ordered by name.</returns>
    public static async Task<IReadOnlyList<DiscoveredTable>> DiscoverAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(SchemaCoverageSql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        // One row per (table, policy), so rows are folded back into one entry per table. Insertion
        // order is preserved by the accompanying list, because the query already sorts by name and
        // a caller reading a failure wants the same order twice in a row.
        Dictionary<string, List<TablePolicy>> policiesByTable = new(StringComparer.Ordinal);
        List<(string Name, bool RowSecurityEnabled, TableOwnership Ownership)> tables = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            string name = reader.GetString(0);
            if (!policiesByTable.TryGetValue(name, out List<TablePolicy>? policies))
            {
                policies = [];
                policiesByTable.Add(name, policies);
                tables.Add((
                    name,
                    reader.GetBoolean(1),
                    DetermineOwnership(name, reader.GetBoolean(2), reader.GetBoolean(3))));
            }

            // Null on the left join's miss: a table with no policies at all, which is the case this
            // whole type exists to notice.
            if (!await reader.IsDBNullAsync(4, cancellationToken))
            {
                policies.Add(new TablePolicy(
                    reader.GetString(4),
                    await reader.GetFieldValueAsync<string[]>(5, cancellationToken)));
            }
        }

        return tables
            .Select(entry => new DiscoveredTable(
                entry.Name,
                entry.RowSecurityEnabled,
                entry.Ownership,
                policiesByTable[entry.Name]))
            .ToList();
    }

    /// <summary>
    /// Sorts discovered tables into the tables that must be policed, the tables nobody has decided
    /// about, the exempt tables, and the exemptions that name nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure, and it takes <paramref name="exemptions" /> as a parameter rather than reading
    /// <see cref="Exemptions" />. A classifier hardwired to its own list could not be tested at all,
    /// only trusted: the guard against discovery quietly narrowing back to a column filter is a
    /// caller classifying the same live schema against an <b>empty</b> set and demanding every
    /// normally-exempt table come back unexcused.
    /// </para>
    /// <para>
    /// The exemption list is consulted first, so a written-down decision is never overridden by a
    /// column. Table names are compared with <see cref="StringComparison.Ordinal" />: <c>pg_class</c>
    /// stores <c>__EFMigrationsHistory</c> exactly as EF quotes it, and a loose comparison would let
    /// an exemption claim a table it does not name.
    /// </para>
    /// </remarks>
    /// <param name="tables">The output of <see cref="DiscoverAsync" />.</param>
    /// <param name="exemptions">The tables excused from needing a policy, and why.</param>
    /// <returns>Every table in exactly one bucket, plus any exemption matching no table.</returns>
    public static SchemaClassification Classify(
        IReadOnlyList<DiscoveredTable> tables,
        IReadOnlyList<TableExemption> exemptions)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(exemptions);

        List<ClassifiedTable> needingAPolicy = [];
        List<DiscoveredTable> unclassifiable = [];
        List<ExemptTable> exempt = [];
        HashSet<string> matchedExemptions = new(StringComparer.Ordinal);

        foreach (DiscoveredTable table in tables)
        {
            TableExemption? exemption = exemptions.FirstOrDefault(
                candidate => string.Equals(candidate.Table, table.Name, StringComparison.Ordinal));
            if (exemption is not null)
            {
                exempt.Add(new ExemptTable(exemption, table));
                matchedExemptions.Add(exemption.Table);
                continue;
            }

            switch (table.Ownership)
            {
                case TableOwnership.BudgetOwned:
                    needingAPolicy.Add(new ClassifiedTable(table, BudgetIsolationPolicyName));
                    break;
                case TableOwnership.UserOwned:
                    needingAPolicy.Add(new ClassifiedTable(table, UserIsolationPolicyName));
                    break;
                default:
                    unclassifiable.Add(table);
                    break;
            }
        }

        List<TableExemption> namingNoTable = exemptions
            .Where(exemption => !matchedExemptions.Contains(exemption.Table))
            .ToList();

        return new SchemaClassification(needingAPolicy, unclassifiable, exempt, namingNoTable);
    }

    /// <summary>
    /// Decides what a table owns from its own columns, budget-ownership first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is deliberate rather than incidental: a budget belongs to exactly one user, so
    /// <c>budget_id = current_budget</c> is strictly narrower than <c>user_id = current_user</c>. A
    /// table that grows both columns has to be protected by the narrower rule, not by whichever
    /// check happened to run first — and "whichever ran first" is exactly what an unordered pair of
    /// <c>if</c> statements would degrade into on the day someone reorders them.
    /// </para>
    /// <para>
    /// <see cref="TableOwnership.None" /> is the honest answer for a table carrying neither column,
    /// not a fallback. It is what sends the table to
    /// <see cref="SchemaClassification.Unclassifiable" />, because with two policy names in play
    /// there is no answer left to derive: such a table cannot be told whether it owes
    /// <see cref="BudgetIsolationPolicyName" /> or <see cref="UserIsolationPolicyName" />, and
    /// inventing one would be guessing.
    /// </para>
    /// </remarks>
    private static TableOwnership DetermineOwnership(
        string name,
        bool hasBudgetColumn,
        bool hasUserColumn) => (hasBudgetColumn, hasUserColumn) switch
        {
            (true, _) => TableOwnership.BudgetOwned,
            (_, true) => TableOwnership.UserOwned,
            _ when string.Equals(name, UsersTableName, StringComparison.Ordinal)
                => TableOwnership.UserOwned,
            _ => TableOwnership.None,
        };
}
