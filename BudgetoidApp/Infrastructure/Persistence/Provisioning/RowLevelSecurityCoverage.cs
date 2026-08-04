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
/// <para>
/// <paramref name="ColumnsTheReasonCovers" /> answers the same question one grain down, and it
/// exists because an exemption is argued about a <b>query</b> and applied by PostgreSQL to a whole
/// <b>table</b>. There is no finer grain to apply it at: the reason on <c>credentials</c> is "this
/// is the table read to discover who is asking", which is an argument about a handful of columns,
/// while the effect is a table-wide <c>SELECT</c> every application session holds regardless of
/// which user it names. Coverage fails closed on a new table and says nothing at all about a new
/// column on a table already exempt, so without the column set recorded, material that is only ever
/// read <i>after</i> authentication answers who is asking can land on the one table whose whole
/// justification is being readable before any tenant is known — and nothing goes red.
/// </para>
/// <para>
/// Non-null means "this exemption was argued over exactly this column set, and a new column
/// invalidates the argument". Null means the reason does not turn on the table's shape at all, and
/// then <paramref name="Reason" /> has to say so in words rather than leave it inferred — a null
/// that nobody justified is indistinguishable from a null somebody reached for to make the code
/// compile.
/// </para>
/// <para>
/// <b>When the pin goes red, the fix is to move the column, not to append its name here.</b>
/// Appending is the drift the pin exists to stop, and it is the fix that looks obvious at the exact
/// moment it is least true. The line the split follows is already drawn by the reason: the exempt
/// table keeps what answers "who is asking" and "is this really them", read before any identity
/// exists, and everything read <i>after</i> that answer belongs on a table carrying <c>user_id</c>
/// — which the classifier then requires a policy on by itself, with no new rule. That mirrors the
/// identity / key-custody split the requirements already draw.
/// </para>
/// <para>
/// The honest limit: the pin trips on <b>any</b> new column, including a benign one such as a
/// last-used timestamp that leaks nothing. That is intended rather than a false positive — what is
/// being forced is the decision, not necessarily the column's exclusion — but the first legitimate
/// red will read as the rule being wrong unless this is said out loud.
/// </para>
/// </remarks>
/// <param name="Table">The table name exactly as <c>pg_class</c> stores it.</param>
/// <param name="Reason">Why this table belongs to no tenant, or cannot be policed as if it did.</param>
/// <param name="ExemptDespite">The ownership the schema is expected to still report for it.</param>
/// <param name="ColumnsTheReasonCovers">
/// Every column the reason was argued over, or null when the reason does not depend on the table's
/// shape. No default value: an exemption whose scope nobody stated is the state this member was
/// added to leave behind, so adding one has to answer the question out loud.
/// </param>
public sealed record TableExemption(
    string Table,
    string Reason,
    TableOwnership ExemptDespite,
    IReadOnlyList<string>? ColumnsTheReasonCovers);

/// <summary>
/// The kinds of relation discovery reaches, split by whether an enforced policy can be attached at
/// all.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>relkind</c> is <b>not</b> a uniform "is this a table" question. Two of
/// these kinds can carry a policy PostgreSQL will enforce and three cannot, and the three that
/// cannot are precisely the ones an ordinary-tables-only discovery never had to have an opinion
/// about — which is how they became a way past every check in this file.
/// </para>
/// <para>
/// <see cref="PartitionedTable" /> is the shape that most needed naming. Its partitions are
/// <see cref="OrdinaryTable" /> and were always discovered; the parent was not, while PostgreSQL
/// applies the <i>parent's</i> policies to every query routed through the parent. So the relation
/// the application actually reads through was the one relation nobody was required to police.
/// </para>
/// </remarks>
public enum RelationKind
{
    /// <summary>A plain table, or one partition of a partitioned one. <c>relkind = 'r'</c>.</summary>
    OrdinaryTable,

    /// <summary>A partitioned parent, whose policies are the ones a routed query obeys. <c>'p'</c>.</summary>
    PartitionedTable,

    /// <summary>A view, which runs as its owner and so routes around policies entirely. <c>'v'</c>.</summary>
    View,

    /// <summary>A materialized view, whose stored rows no policy can filter. <c>'m'</c>.</summary>
    MaterializedView,

    /// <summary>A foreign table, whose rows live where no policy of ours reaches. <c>'f'</c>.</summary>
    ForeignTable,
}

/// <summary>A policy attached to a table, with the roles it binds and the rule it enforces.</summary>
/// <remarks>
/// <para>
/// The name and the roles say which rule is <i>claimed</i> to be here; the four members after them
/// say what it actually does. Those come apart silently and in every direction — a policy named
/// <c>budget_isolation</c>, bound to the application role, that reads <c>USING (true)</c> is a real,
/// enforced, correctly-named policy that isolates nothing.
/// </para>
/// </remarks>
/// <param name="Name">The policy name — which rule, not merely that a rule exists.</param>
/// <param name="Roles">
/// The role names the policy is written <c>TO</c>, or the single entry <c>public</c> when it binds
/// every non-owner role.
/// </param>
/// <param name="Permissive">
/// <c>polpermissive</c>. A restrictive policy grants no access on its own — it only narrows what a
/// permissive one already allowed — so it is not a stricter version of the rule owed, it is a
/// different kind of rule that leaves the table with nothing granting anything.
/// </param>
/// <param name="Command">
/// <c>polcmd</c> as the catalog spells it: <c>*</c> for <c>ALL</c>, <c>r</c> <c>SELECT</c>,
/// <c>a</c> <c>INSERT</c>, <c>w</c> <c>UPDATE</c>, <c>d</c> <c>DELETE</c>. Every command a policy
/// does not name is a command it leaves unconstrained.
/// </param>
/// <param name="Using">
/// <c>pg_get_expr(polqual, …)</c> — the rule deciding which existing rows are visible, normalized by
/// the server rather than echoed as typed. Null means no <c>USING</c> clause, which is every row.
/// </param>
/// <param name="WithCheck">
/// <c>pg_get_expr(polwithcheck, …)</c> — the rule deciding where a write may land. Null is
/// <b>safe</b> and not missing: PostgreSQL reuses <paramref name="Using" /> for the check when the
/// clause is omitted, so a null here is the same rule stated once.
/// </param>
public sealed record TablePolicy(
    string Name,
    IReadOnlyList<string> Roles,
    bool Permissive,
    string Command,
    string? Using,
    string? WithCheck);

/// <summary>A relation as the live catalog describes it, before anyone decides what it owes.</summary>
/// <param name="Name">The relation name as <c>pg_class</c> stores it.</param>
/// <param name="Kind">
/// <c>relkind</c>, read as a fact rather than applied as a filter. It decides whether an enforced
/// policy is even possible here, which is a different question from whether one is present.
/// </param>
/// <param name="RowSecurityEnabled">
/// <c>relrowsecurity</c>. A policy on a table with this off is inert: PostgreSQL keeps the
/// definition and enforces nothing, so a table can be fully policed on paper and open in practice.
/// </param>
/// <param name="Ownership">Read from the table's columns as a fact about it, never as a filter.</param>
/// <param name="NullableOwnershipColumns">
/// Which of <c>budget_id</c>, <c>user_id</c> and <c>id</c> the relation carries <b>without</b>
/// <c>NOT NULL</c>. A nullable ownership column fails closed rather than open — <c>NULL = anything</c>
/// is NULL and never true, so such a row is invisible to every session including the one that wrote
/// it — which makes it undiagnosable rather than harmless.
/// </param>
/// <param name="Policies">
/// <b>All</b> of the relation's policies, not the ones a caller was looking for. Callers need the
/// count as much as the contents: these policies are permissive and permissive policies OR together,
/// so a second one can only widen what the first allows.
/// </param>
public sealed record DiscoveredTable(
    string Name,
    RelationKind Kind,
    bool RowSecurityEnabled,
    TableOwnership Ownership,
    IReadOnlyList<string> NullableOwnershipColumns,
    IReadOnlyList<TablePolicy> Policies);

/// <summary>A table that must be policed, paired with everything its ownership requires of it.</summary>
/// <remarks>
/// All three requirements are derived in <see cref="RowLevelSecurityCoverage.Classify" /> from the
/// table's own ownership rather than passed in, so that a caller cannot check a table against a rule
/// it does not owe. They travel together because none of them is sufficient alone: the name says
/// which rule is claimed, and the setting and the column are the two things the rule cannot work
/// without.
/// </remarks>
/// <param name="Table">The discovered table.</param>
/// <param name="RequiredPolicyName">
/// <see cref="RowLevelSecurityCoverage.BudgetIsolationPolicyName" /> or
/// <see cref="RowLevelSecurityCoverage.UserIsolationPolicyName" />. Naming the wrong one is a real
/// policy, enforced, and wider than the tenancy the table is supposed to have.
/// </param>
/// <param name="RequiredSessionSetting">
/// <see cref="RowLevelSecurityCoverage.BudgetSessionSetting" /> or
/// <see cref="RowLevelSecurityCoverage.UserSessionSetting" />. A predicate keyed on the other one is
/// comparing ids drawn from different spaces, which never matches and reads as an empty table.
/// </param>
/// <param name="RequiredOwnershipColumn">
/// <c>budget_id</c>, <c>user_id</c>, or <c>id</c> on <c>users</c> — the table that is user-owned by
/// <i>being</i> the person rather than by referencing one.
/// </param>
public sealed record ClassifiedTable(
    DiscoveredTable Table,
    string RequiredPolicyName,
    string RequiredSessionSetting,
    string RequiredOwnershipColumn);

/// <summary>An exemption paired with the table it currently names.</summary>
/// <param name="Exemption">The written-down decision.</param>
/// <param name="Table">The table as the schema describes it today.</param>
public sealed record ExemptTable(TableExemption Exemption, DiscoveredTable Table);

/// <summary>
/// Every discovered relation sorted into exactly one of four buckets, plus the exemptions that
/// matched nothing.
/// </summary>
/// <param name="NeedingAPolicy">Owned tables, each with the policy name its own ownership requires.</param>
/// <param name="Unclassifiable">
/// Tables carrying neither ownership column and named by no exemption. This bucket is red, not
/// empty-by-convenience: with two policy names in play there is nothing to derive for a table that
/// owns nothing, and choosing one for it would be guessing in a type whose whole job is to refuse
/// to guess.
/// </param>
/// <param name="Unpoliceable">
/// Relations no enforced policy can be attached to — views, materialized views and foreign tables —
/// and named by no exemption. Red for the same reason <paramref name="Unclassifiable" /> is, and
/// distinct from it because the decision being asked for is a different one: unclassifiable asks
/// which tenant owns these rows, unpoliceable asks why this relation exists and what stops the
/// application role reading every tenant through it.
/// </param>
/// <param name="Exempt">Tables an exemption names, so a caller can check the schema still agrees.</param>
/// <param name="ExemptionsNamingNoTable">
/// Exemptions matching no table. Reported rather than dropped: such a name lies in wait for whatever
/// is next called that, ready to hand it a reason written about something else entirely.
/// </param>
public sealed record SchemaClassification(
    IReadOnlyList<ClassifiedTable> NeedingAPolicy,
    IReadOnlyList<DiscoveredTable> Unclassifiable,
    IReadOnlyList<DiscoveredTable> Unpoliceable,
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
/// So the subject is <b>discovered</b> and never written down: every relation in <c>public</c> that
/// could hold rows of its own. What is written down is <see cref="Exemptions" />, and the direction
/// of that list is the point.
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
/// <c>relkind = 'r'</c> was the same mistake wearing a narrower disguise and lasted longer, because
/// it read as an object-kind check rather than as a filter — see <see cref="SchemaCoverageSql" />.
/// </para>
/// <para>
/// What each policy <i>says</i> is checked too, by <see cref="FindProblems" />, and that is a second
/// axis rather than more of the same one. Coverage answers "is a rule enforced on this table"; the
/// content rules answer "is the rule enforced here the rule this table owes". A policy carrying the
/// required name, bound to the application role, reading <c>USING (true)</c> passes every question
/// the first axis can ask.
/// </para>
/// <para>
/// A view is refused <b>outright</b>, with no <c>security_invoker</c> escape hatch, and that is a
/// decision rather than a gap. <c>security_invoker</c> is a reloption one <c>ALTER VIEW</c> away from
/// being flipped back, so trusting it would leave this type guarding something that is not a policy
/// and cannot be reasoned about as one. The escape is the written exemption list, the same as it is
/// for everything else here.
/// </para>
/// <para>
/// Refusing only the views the application role is <i>granted</i> on was considered and rejected. It
/// needs <c>aclexplode</c> over <c>relacl</c>, grantee <c>0</c> read as <c>PUBLIC</c>, and
/// role-membership resolution — and that query silently matching nothing is fail-open, which is this
/// type's own defect reintroduced as a second mechanism. Making the grant irrelevant is the point:
/// a grant can be added after a deploy, and a refusal that depends on one can be dissolved by
/// adding it.
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

    /// <summary>The session setting naming the ambient budget, which every budget-owned policy reads.</summary>
    /// <remarks>
    /// <c>BudgetSessionInterceptor</c> writes this on every connection open. A budget-owned policy
    /// that reads the other setting compares a budget id against a user id — two id spaces that never
    /// meet — so it never matches and the table reads as empty for every tenant, under a policy the
    /// deploy log reports as present and correctly named.
    /// </remarks>
    public const string BudgetSessionSetting = "app.current_budget_id";

    /// <summary>The session setting naming the authenticated user, which every user-owned policy reads.</summary>
    public const string UserSessionSetting = "app.current_user_id";

    /// <summary>The <c>polcmd</c> value for <c>FOR ALL</c>, the only command coverage this type accepts.</summary>
    private const string AllCommands = "*";

    /// <summary>The ownership column a budget-owned table isolates on.</summary>
    private const string BudgetOwnershipColumn = "budget_id";

    /// <summary>The ownership column a user-owned table isolates on, except on <c>users</c>.</summary>
    private const string UserOwnershipColumn = "user_id";

    /// <summary>The ownership column of <c>users</c>: the row's own primary key.</summary>
    /// <remarks>
    /// The special case that makes <see cref="ClassifiedTable.RequiredOwnershipColumn" /> worth
    /// deriving rather than assuming. <c>users</c> is user-owned by <i>being</i> the person, so its
    /// policy reads <c>id = …</c>; requiring <c>user_id</c> of it would refuse the one policy the
    /// schema actually ships.
    /// </remarks>
    private const string UsersOwnershipColumn = "id";

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
    /// Every relation in <c>public</c> that can hold rows of its own, with its kind,
    /// <c>relrowsecurity</c>, both ownership facts, which ownership columns are nullable, and every
    /// policy attached to it in full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>relkind = 'r'</c> used to be the only condition here, described as excluding kinds of
    /// object rather than tables. It was a discovery filter, and it hid three shapes. A <b>view</b>
    /// granted to the application role runs with its <i>owner's</i> privileges unless
    /// <c>security_invoker</c> is set, and an owner bypasses row-level security, so the role reads
    /// every tenant through it while every policy underneath stays correct and the verifier stays
    /// green. A <b>materialized view</b> holds rows no policy can filter. A <b>partitioned table</b>
    /// is the worst of the three: partitions are <c>'r'</c> and were discovered, the parent is
    /// <c>'p'</c> and was not, and PostgreSQL applies the <i>parent's</i> policies to queries routed
    /// through the parent — so a contributor was pushed into policing partitions whose policies never
    /// fire.
    /// </para>
    /// <para>
    /// The kinds still left out — index, sequence, composite type, TOAST table, partitioned index —
    /// expose no rows of their own, so there is nothing about them for a tenancy rule to be wrong
    /// about. That is the test any future narrowing has to pass, and <c>'r'</c> did not.
    /// </para>
    /// <para>
    /// Policies are joined through <c>pg_policy.polrelid</c> rather than the <c>pg_policies</c>
    /// view's table <i>name</i>, because an oid cannot collide with a same-named table in another
    /// schema — and because that view filters its rows by <c>pg_has_role</c>, which is a discovery
    /// filter of exactly the kind the paragraphs above are about. The role expression mirrors it:
    /// <c>polroles</c> is an oid array, <c>{0}</c> means <c>TO PUBLIC</c>, and the resolved names
    /// come back as <c>name[]</c>, cast to <c>text[]</c> so Npgsql reads them as strings.
    /// <c>pg_roles</c> rather than <c>pg_authid</c>: the former is world-readable, and nothing here
    /// needs the password column the latter guards.
    /// </para>
    /// <para>
    /// <c>polqual</c> and <c>polwithcheck</c> come back through <c>pg_get_expr</c>, which returns the
    /// server's normalized rendering rather than what was typed. That is what makes a content check
    /// on the text a claim about the rule rather than about spelling.
    /// </para>
    /// </remarks>
    private const string SchemaCoverageSql =
        """
        select c.relname,
               c.relkind::text,
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
               array(
                   select a.attname::text
                   from pg_attribute a
                   where a.attrelid = c.oid
                     and a.attname in ('budget_id', 'user_id', 'id')
                     and a.attnum > 0
                     and not a.attisdropped
                     and not a.attnotnull
                   order by a.attname) as nullable_ownership_columns,
               p.polname,
               p.polpermissive,
               p.polcmd::text,
               pg_get_expr(p.polqual, p.polrelid) as pol_using,
               pg_get_expr(p.polwithcheck, p.polrelid) as pol_with_check,
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
          and c.relkind = any (array['r', 'p', 'v', 'm', 'f'])
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
    /// It is also the only entry whose <see cref="TableExemption.ColumnsTheReasonCovers" /> is
    /// pinned, and the pin is the whole of what keeps its exemption honest: the six columns below
    /// are the ones "read to discover who is asking" is an argument about. Key material and per-
    /// factor secrets are specified to arrive in this area, and they are read only after that
    /// question has been answered, so they belong on a table carrying <c>user_id</c> — not appended
    /// to this list. See <see cref="TableExemption" /> for why widening the list is the wrong fix.
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
            TableOwnership.UserOwned,
            ["id", "user_id", "type", "provider", "subject", "created_at_utc"]),
        new(
            "currencies",
            "shared reference data belonging to no tenant, whatever columns it grows — the reason "
            + "is about who owns the rows and not about the table's shape, so no column set is "
            + "pinned",
            TableOwnership.None,
            null),
        new(
            "__EFMigrationsHistory",
            "EF's own bookkeeping, and the application role may only read it — EF owns this "
            + "table's shape, so pinning its columns would turn an EF upgrade into a red with "
            + "nothing to decide",
            TableOwnership.None,
            null),
    ];

    /// <summary>
    /// Reads every row-bearing relation in <c>public</c> from the catalogs on an already-open
    /// connection.
    /// </summary>
    /// <remarks>
    /// Any role may call this. <c>pg_class</c>, <c>pg_policy</c> and <c>pg_attribute</c> describe the
    /// schema, and the schema reads the same whoever asks — which is also why this is not a privilege
    /// question in the tests that use a superuser connection. It is likewise why the catalogs are
    /// read directly rather than through <c>pg_policies</c>, whose <c>pg_has_role</c> filter would
    /// make the answer depend on who was asking.
    /// </remarks>
    /// <param name="connection">An open connection to the database being inspected.</param>
    /// <param name="cancellationToken">Cancels the catalog query.</param>
    /// <returns>Every row-bearing relation in <c>public</c>, ordered by name.</returns>
    public static async Task<IReadOnlyList<DiscoveredTable>> DiscoverAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(SchemaCoverageSql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        // One row per (relation, policy), so rows are folded back into one entry per relation.
        // Insertion order is preserved by the accompanying list, because the query already sorts by
        // name and a caller reading a failure wants the same order twice in a row.
        Dictionary<string, List<TablePolicy>> policiesByTable = new(StringComparer.Ordinal);
        List<DiscoveredTable> tables = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            string name = reader.GetString(0);
            if (!policiesByTable.TryGetValue(name, out List<TablePolicy>? policies))
            {
                policies = [];
                policiesByTable.Add(name, policies);
                tables.Add(new DiscoveredTable(
                    name,
                    ParseRelationKind(reader.GetString(1)),
                    reader.GetBoolean(2),
                    DetermineOwnership(name, reader.GetBoolean(3), reader.GetBoolean(4)),
                    await reader.GetFieldValueAsync<string[]>(5, cancellationToken),
                    policies));
            }

            // Null on the left join's miss: a relation with no policies at all, which is the case
            // this whole type exists to notice.
            if (!await reader.IsDBNullAsync(6, cancellationToken))
            {
                policies.Add(new TablePolicy(
                    reader.GetString(6),
                    await reader.GetFieldValueAsync<string[]>(11, cancellationToken),
                    reader.GetBoolean(7),
                    reader.GetString(8),
                    await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
                    await reader.IsDBNullAsync(10, cancellationToken)
                        ? null
                        : reader.GetString(10)));
            }
        }

        return tables;
    }

    /// <summary>
    /// Sorts discovered relations into the tables that must be policed, the tables nobody has decided
    /// about, the relations no policy can cover, the exempt tables, and the exemptions that name
    /// nothing.
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
    /// column — or by a relation kind. That ordering is what makes the exemption list the escape
    /// hatch for an unpoliceable relation too: a view somebody genuinely wants is excused the same
    /// way everything else is, by name and with a reason, rather than by a property of the view.
    /// Table names are compared with <see cref="StringComparison.Ordinal" />: <c>pg_class</c> stores
    /// <c>__EFMigrationsHistory</c> exactly as EF quotes it, and a loose comparison would let an
    /// exemption claim a table it does not name.
    /// </para>
    /// <para>
    /// Kind is decided before ownership, because a view carrying <c>budget_id</c> is not a
    /// budget-owned table that happens to be a view — a policy on it is a statement PostgreSQL will
    /// not accept, so demanding one would be demanding something nobody can write.
    /// </para>
    /// </remarks>
    /// <param name="tables">The output of <see cref="DiscoverAsync" />.</param>
    /// <param name="exemptions">The tables excused from needing a policy, and why.</param>
    /// <returns>Every relation in exactly one bucket, plus any exemption matching no table.</returns>
    public static SchemaClassification Classify(
        IReadOnlyList<DiscoveredTable> tables,
        IReadOnlyList<TableExemption> exemptions)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(exemptions);

        List<ClassifiedTable> needingAPolicy = [];
        List<DiscoveredTable> unclassifiable = [];
        List<DiscoveredTable> unpoliceable = [];
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

            if (table.Kind is RelationKind.View
                or RelationKind.MaterializedView
                or RelationKind.ForeignTable)
            {
                unpoliceable.Add(table);
                continue;
            }

            switch (table.Ownership)
            {
                case TableOwnership.BudgetOwned:
                    needingAPolicy.Add(new ClassifiedTable(
                        table,
                        BudgetIsolationPolicyName,
                        BudgetSessionSetting,
                        BudgetOwnershipColumn));
                    break;
                case TableOwnership.UserOwned:
                    needingAPolicy.Add(new ClassifiedTable(
                        table,
                        UserIsolationPolicyName,
                        UserSessionSetting,
                        string.Equals(table.Name, UsersTableName, StringComparison.Ordinal)
                            ? UsersOwnershipColumn
                            : UserOwnershipColumn));
                    break;
                default:
                    unclassifiable.Add(table);
                    break;
            }
        }

        List<TableExemption> namingNoTable = exemptions
            .Where(exemption => !matchedExemptions.Contains(exemption.Table))
            .ToList();

        return new SchemaClassification(
            needingAPolicy, unclassifiable, unpoliceable, exempt, namingNoTable);
    }

    /// <summary>
    /// Reports everything wrong with one classified table's protection, as sentences an operator can
    /// act on. An empty list is a healthy table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure, and public, because "what a protected table looks like" was being executed in two
    /// places — the deploy verifier and the coverage suite — and two executed copies of a rule have
    /// no adjudicator when they disagree. The copy no test reads is the copy that rots, and here the
    /// rot direction is fail-open.
    /// </para>
    /// <para>
    /// The first two rules short-circuit because they dominate everything after them: with row-level
    /// security off, the policies are inert whatever they say, and with a count other than one there
    /// is no single policy for the content rules to be about. The rest accumulate, and <b>one defect
    /// produces exactly one entry</b> — a report that turns a single nullable column into three
    /// sentences makes the reader hunt for three problems.
    /// </para>
    /// <para>
    /// The content rules are structural rather than a comparison against an expected string, and that
    /// is deliberate. <c>pg_get_expr</c> emits the server's normalized SQL, so an equivalently
    /// rewritten but entirely correct policy would fail a string match and refuse a legitimate
    /// deploy. What is demanded instead is the two things the rule cannot work without: the session
    /// setting it is keyed on, and the ownership column its tenancy is decided by.
    /// </para>
    /// <para>
    /// <b>The limit is worth stating.</b> These rules catch drift, accident, and a hand-edit that
    /// weakened one clause. They are not proof against a deliberately crafted predicate that is wider
    /// but still plausible — <c>… OR true</c> reads the setting and names the column and passes. That
    /// is outside the threat model rather than an oversight: anyone who can rewrite <c>pg_policy</c>
    /// can <c>DISABLE ROW LEVEL SECURITY</c> instead, and a verifier that pretended otherwise would be
    /// claiming a guarantee it cannot hold.
    /// </para>
    /// </remarks>
    /// <param name="table">A table the classifier decided must be policed.</param>
    /// <returns>One sentence per defect, empty when the table is protected as its ownership requires.</returns>
    public static IReadOnlyList<string> FindProblems(ClassifiedTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        DiscoveredTable relation = table.Table;

        // V1. First and alone: a table with relrowsecurity off is open no matter what its policies
        // say. PostgreSQL keeps the definitions and enforces none of them, so this is fully policed
        // on paper and readable across every tenant in practice.
        if (!relation.RowSecurityEnabled)
        {
            return [$"{relation.Name} has row-level security disabled."];
        }

        // V2. Exactly one, not at least one. These policies are permissive and permissive policies OR
        // together, so a second one can only widen what the first allows — and with no single policy
        // there is nothing for the rules below to be about.
        if (relation.Policies is not [TablePolicy policy])
        {
            return
            [
                $"{relation.Name} has {relation.Policies.Count} policies, wanted exactly 1.",
            ];
        }

        List<string> problems = [];

        // V3. A count answers "is a rule enforced here"; only the name answers "is the rule enforced
        // here the rule this table owes". With two isolation rules in the schema, a budget-owned
        // table carrying user_isolation has a real, enforced policy that is simply wider than its
        // tenancy.
        if (!string.Equals(policy.Name, table.RequiredPolicyName, StringComparison.Ordinal))
        {
            problems.Add(
                $"{relation.Name} is policed by '{policy.Name}', wanted "
                + $"'{table.RequiredPolicyName}'.");
        }

        // V4. "Binds the application role", not "names it and nothing else". A policy written
        // TO PUBLIC binds every non-owner role, so it covers the app role too and is broader rather
        // than weaker; refusing it would make this check brittle about spelling instead of about
        // protection. Anything else leaves the app role unpoliced.
        if (!policy.Roles.Contains(DatabaseProvisioning.AppRoleName)
            && !policy.Roles.Contains("public"))
        {
            problems.Add(
                $"{relation.Name} is policed by '{policy.Name}', which binds "
                + $"[{string.Join(", ", policy.Roles)}] and so does not bind "
                + $"{DatabaseProvisioning.AppRoleName}.");
        }

        // V5. Restrictive is a different kind of rule rather than a stricter one. Permissive policies
        // OR together to say what a role MAY reach and restrictive ones AND onto that result to
        // narrow it, so a restrictive policy grants nothing on its own: a table whose only policy is
        // restrictive has no rule granting anything, and the application reads zero rows in every
        // tenant while the catalog shows the policy present and correct.
        if (!policy.Permissive)
        {
            problems.Add(
                $"{relation.Name} is policed by '{policy.Name}', which is AS RESTRICTIVE and so "
                + "grants no access on its own — it can only narrow a permissive policy, and there "
                + "is none.");
        }

        // V6. Every command a policy does not name is a command it leaves unconstrained — and, once
        // row-level security is on, denied outright for a role with no other permissive policy. The
        // command is named in words because the catalog's single character is not something an
        // operator reading a failed deploy should have to look up.
        if (!string.Equals(policy.Command, AllCommands, StringComparison.Ordinal))
        {
            problems.Add(
                $"{relation.Name} is policed by '{policy.Name}', which covers only "
                + $"{DescribeCommand(policy.Command)} and leaves every other command "
                + "unconstrained.");
        }

        // V7. No USING clause is not a narrower rule, it is no rule: every existing row is visible to
        // the application role.
        if (policy.Using is null)
        {
            problems.Add(
                $"{relation.Name} is policed by '{policy.Name}', which has no USING expression, so "
                + "every existing row is visible to the application role.");
        }
        else
        {
            // V8. A predicate that reads no session setting — or reads the wrong one — is not keyed
            // on the session's tenant. The wrong one is the sharper case: a budget id compared
            // against a user id is drawn from a different id space, never matches, and reads as an
            // empty table under a correctly-named policy.
            if (!policy.Using.Contains(
                    $"current_setting('{table.RequiredSessionSetting}'", StringComparison.Ordinal))
            {
                problems.Add(
                    $"{relation.Name} is policed by '{policy.Name}', whose USING expression does not "
                    + $"read current_setting('{table.RequiredSessionSetting}'): {policy.Using}");
            }

            // V9. And a predicate that reads the right setting while naming no ownership column
            // isolates nothing — it asserts only that somebody is signed in. The match is on a WORD
            // BOUNDARY rather than a substring, and that is load-bearing rather than tidy: the
            // required column on users is 'id', which is a substring of app.current_user_id, of
            // budget_id and of user_id, so a Contains check cannot fail on the one table where
            // failing matters most.
            if (!ReferencesColumn(policy.Using, table.RequiredOwnershipColumn))
            {
                problems.Add(
                    $"{relation.Name} is policed by '{policy.Name}', whose USING expression does not "
                    + $"reference {table.RequiredOwnershipColumn} and so does not isolate on this "
                    + $"table's ownership: {policy.Using}");
            }

            // V10. USING and WITH CHECK are two rules and the table owes both. A null WITH CHECK is
            // SAFE and deliberately accepted: PostgreSQL reuses USING for the check when the clause
            // is omitted, so it is the same rule stated once, and refusing it would fail a deploy
            // over a clause whose presence changes nothing.
            //
            // Comparing the two as text is conservative in one direction — a legitimately NARROWER
            // WITH CHECK would be refused. None exists or is planned, and both strings come from the
            // same normalizer on the same server, so an equivalent rewrite normalizes identically on
            // both sides.
            if (policy.WithCheck is not null
                && !string.Equals(policy.WithCheck, policy.Using, StringComparison.Ordinal))
            {
                problems.Add(
                    $"{relation.Name} is policed by '{policy.Name}', whose WITH CHECK expression "
                    + $"differs from its USING expression, so a write can land where a read cannot "
                    + $"reach: USING {policy.Using}, WITH CHECK {policy.WithCheck}");
            }
        }

        // V11. The opposite sign from everything above, and not a leak. A NULL owner makes a row
        // invisible to every session, including the one that wrote it, because NULL = anything is
        // NULL and never true. That fails closed — and is undiagnosable, because the write succeeds,
        // the policy is correct, and nothing in the schema says why the row cannot be read back.
        if (relation.NullableOwnershipColumns.Contains(
                table.RequiredOwnershipColumn, StringComparer.Ordinal))
        {
            problems.Add(
                $"{relation.Name} decides tenancy on {table.RequiredOwnershipColumn}, which is "
                + "nullable: a row whose owner is NULL is invisible to every session, including the "
                + "one that wrote it. The column must be NOT NULL.");
        }

        return problems;
    }

    /// <summary>
    /// Says why a relation cannot carry an enforced policy, and what the two ways forward are.
    /// </summary>
    /// <remarks>
    /// This is the entire actionable output of the unpoliceable bucket, which is the one bucket whose
    /// remedy cannot be inferred from its name: "needs a policy" names its own fix, "unpoliceable"
    /// names only the refusal. So it says which relation, why the relation is a problem rather than a
    /// curiosity, and both resolutions — drop it, or write down why it may stay. Nothing here decides
    /// anything; the caller reports.
    /// </remarks>
    /// <param name="relation">A relation the classifier put in
    /// <see cref="SchemaClassification.Unpoliceable" />.</param>
    /// <returns>A sentence naming the relation, the hazard, and both ways forward.</returns>
    public static string DescribeUnpoliceable(DiscoveredTable relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        string hazard = relation.Kind switch
        {
            RelationKind.View =>
                "is a view, which runs with its owner's privileges unless security_invoker is set — "
                + "and an owner is not subject to row-level security, so the application role reads "
                + "every tenant through it however well the tables underneath are policed",
            RelationKind.MaterializedView =>
                "is a materialized view, which cannot carry an enforced policy at all: its stored "
                + "rows were computed by whoever refreshed it, so the isolation question was already "
                + "answered somewhere this check cannot see",
            RelationKind.ForeignTable =>
                "is a foreign table, whose rows live on another server where no policy of ours "
                + "reaches",
            _ =>
                "cannot carry an enforced row-level security policy",
        };

        return $"{relation.Name} {hazard}. Drop it, or add it to "
            + "RowLevelSecurityCoverage.Exemptions with a written reason.";
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

    /// <summary>
    /// Turns <c>relkind</c> into the distinction this type actually cares about.
    /// </summary>
    /// <remarks>
    /// The default arm throws rather than folding an unknown kind into the policeable side. Discovery
    /// asks for five kinds by name, so reaching it means the query and this switch have drifted apart
    /// — and the safe direction for that disagreement is a loud failure, not a relation quietly
    /// treated as an ordinary table it is not.
    /// </remarks>
    private static RelationKind ParseRelationKind(string relkind) => relkind switch
    {
        "r" => RelationKind.OrdinaryTable,
        "p" => RelationKind.PartitionedTable,
        "v" => RelationKind.View,
        "m" => RelationKind.MaterializedView,
        "f" => RelationKind.ForeignTable,
        _ => throw new InvalidOperationException(
            $"Discovery returned relkind '{relkind}', which it does not ask for. The catalog query "
            + "and the relation kinds it is read into have drifted apart."),
    };

    /// <summary>
    /// Spells <c>polcmd</c> out for a person reading a failed deploy.
    /// </summary>
    /// <remarks>
    /// The catalog stores a single <c>"char"</c>, and <c>FOR r</c> in a refusal message is a value an
    /// operator has to go and look up at the moment they can least afford to. The unknown arm keeps
    /// the raw character rather than guessing, because a message that invents a command name is worse
    /// than one that admits it does not recognise the value.
    /// </remarks>
    private static string DescribeCommand(string polcmd) => polcmd switch
    {
        AllCommands => "FOR ALL",
        "r" => "FOR SELECT",
        "a" => "FOR INSERT",
        "w" => "FOR UPDATE",
        "d" => "FOR DELETE",
        _ => $"the command PostgreSQL records as '{polcmd}'",
    };

    /// <summary>
    /// Reports whether an expression names <paramref name="column" /> as a whole word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A substring test is not merely weaker here, it is <b>vacuous on the one table where it matters
    /// most</b>. The ownership column of <c>users</c> is <c>id</c>, which appears inside
    /// <c>app.current_user_id</c>, inside <c>budget_id</c> and inside <c>user_id</c>, so
    /// <c>Contains("id")</c> succeeds on a predicate that names no column called <c>id</c> at all —
    /// including on the predicate that asks only whether somebody is signed in and therefore shows
    /// every person's row to every session.
    /// </para>
    /// <para>
    /// Hand-scanned rather than a regular expression because the boundary rule is one line of it and
    /// the pattern would have to be built from a runtime string anyway. <c>_</c> counts as a word
    /// character, which is exactly what keeps <c>budget_id</c> and <c>current_user_id</c> from
    /// matching <c>id</c>.
    /// </para>
    /// </remarks>
    private static bool ReferencesColumn(string expression, string column)
    {
        for (int start = expression.IndexOf(column, StringComparison.Ordinal);
             start >= 0;
             start = expression.IndexOf(column, start + 1, StringComparison.Ordinal))
        {
            int after = start + column.Length;
            bool boundedLeft = start == 0 || !IsWordCharacter(expression[start - 1]);
            bool boundedRight = after == expression.Length || !IsWordCharacter(expression[after]);
            if (boundedLeft && boundedRight)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a character can be part of an unquoted SQL identifier, for the boundary rule above.
    /// </summary>
    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';
}
