using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins the application role's privileges as a <b>set</b>: exactly these tables, exactly these
/// privileges on each, and exactly these columns on each <c>UPDATE</c>. Every other test of the
/// grant matrix in this suite measures one statement at a time — <c>AppRoleGrantsTests</c> asks
/// "is this column refused" and "is this one permitted" — and a statement-shaped question can only
/// ever notice a privilege somebody thought to write a probe for. A <c>DELETE</c> granted on
/// <c>currencies</c>, a <c>TRUNCATE</c> granted anywhere, or a <c>GRANT UPDATE (name)</c> quietly
/// widened to table-wide are all invisible to every one of them, and the whole immutability
/// doctrine — a column is immutable by being <i>absent</i> from a <c>GRANT UPDATE</c> column list —
/// rests on those lists being what somebody wrote rather than a superset of it.
/// </summary>
/// <remarks>
/// <para>
/// The comparison runs in <b>both directions</b>, and each direction catches the failure the other
/// cannot. A subset check — "everything the role holds is on the list" — passes a grant that is too
/// narrow, so a deleted line ships as a feature failing with <c>42501</c> in production. A superset
/// check — "everything on the list is held" — passes a grant that is too wide, which is the whole
/// hazard this file exists for. So one assertion carries both halves and its failure names the
/// unexpected entries and the missing ones together: a widened column list shows up as both at once
/// (the table-wide privilege appears, the per-column ones vanish), and reading only one half of that
/// would describe it as the wrong bug.
/// </para>
/// <para>
/// The expected matrix is <b>restated here</b> rather than parsed out of
/// <c>app-role-grants.sql</c>. A test that derives its expectation from the file it checks has the
/// subject as its own oracle: rewrite the SQL and the expectation rewrites itself, so the assertion
/// can never fail. This is the argument <c>DeploymentProvisioningTests</c> already makes for its
/// policy-name and predicate constants, and it applies here with more force, because the thing being
/// pinned <i>is</i> the content of that file. The duplication fails closed and announces itself: a
/// new grant is red until somebody writes it down twice, on purpose, which is exactly the
/// conversation widening the role's reach ought to start.
/// </para>
/// <para>
/// The catalogs are read through <c>aclexplode</c> over <c>pg_class.relacl</c> and
/// <c>pg_attribute.attacl</c>, never through <c>information_schema.role_table_grants</c> or
/// <c>.column_privileges</c>. The reason is not the membership filter those views apply — on the
/// superuser connection this test uses, <c>pg_has_role</c> is true against every role, so that
/// filter would remove nothing. It is that the views cannot express two of the three ways this
/// matrix can be widened. <c>aclexplode</c> reports a grant to <c>PUBLIC</c> as grantee oid
/// <c>0</c>, a row those views render as a grantee named <c>PUBLIC</c> that no matrix keyed on a
/// role name would ever match; and it returns <c>is_grantable</c>, which is the difference between
/// a privilege the role holds and one it can hand to anybody — including <c>PUBLIC</c>, which
/// closes the loop back to the first. Both are read here, and both are part of the set key below.
/// </para>
/// <para>
/// Three widening vectors follow from that, and the grantee predicate answers all three at once
/// with <c>acl.grantee = 0 or pg_has_role(@role, acl.grantee, 'USAGE')</c>. A join to
/// <c>pg_roles</c> on the grantee oid — the shape this query used to have — silently drops the
/// <c>PUBLIC</c> row, because oid <c>0</c> has no <c>pg_roles</c> entry; it also drops a privilege
/// the role holds through membership in another role, because the grantee there is the
/// <i>parent</i>. Both are genuinely held: <c>has_table_privilege</c> says so. And a
/// <c>REVOKE ALL … FROM budgetoid_app</c> does not remove a <c>PUBLIC</c> grant, so the grants
/// file's own convergence does not cover for it either. <c>grantee = 0</c> catches <c>PUBLIC</c>,
/// <c>pg_has_role(…, 'USAGE')</c> resolves direct grants and inherited membership together, and
/// projecting <c>is_grantable</c> into the entry makes <c>users: SELECT WITH GRANT OPTION</c> a
/// different string from <c>users: SELECT</c> — the expected matrix holds none of the former, so
/// any grant option lands in the unexpected half.
/// </para>
/// <para>
/// What this still does not cover is <b>known and deferred</b> to the story that owns the
/// enumerated privilege list, not overlooked. Schema privileges are invisible here
/// (<c>pg_namespace.nspacl</c> — <c>GRANT CREATE ON SCHEMA public</c> is the largest widening
/// available and this query never reads that catalog). So are role attributes:
/// <c>ALTER ROLE budgetoid_app BYPASSRLS</c> voids every policy in the project and no grant matrix
/// of any shape would see it. So are <c>pg_default_acl</c>, which decides what future objects are
/// granted at creation, and <c>pg_proc.proacl</c>, which carries <c>EXECUTE</c> on functions. This
/// file pins table and column privileges on relations in <c>public</c>, and a reader must not trust
/// it one step further than that.
/// </para>
/// <para>
/// The two catalogs are read separately because PostgreSQL stores the two kinds of grant in
/// different places: a column-level <c>GRANT UPDATE (email)</c> lands in that column's
/// <c>attacl</c> and puts <b>nothing</b> in the table's <c>relacl</c>, while a table-wide
/// <c>GRANT UPDATE</c> lands in <c>relacl</c> and puts nothing in any <c>attacl</c>. That is what
/// makes a widened list observable at all: the two shapes are genuinely different rows, not the same
/// row spelled differently.
/// </para>
/// <para>
/// Every observation is made on the container superuser connection, and that is correct rather than
/// a privilege blind spot. <c>pg_class</c>, <c>pg_attribute</c> and <c>pg_roles</c> describe the
/// cluster, and the cluster reads the same whoever asks — the same point
/// <c>RowLevelSecurityCoverage.DiscoverAsync</c> makes about its own catalog reads. Sending these
/// queries on the application role's own connection would only reintroduce the filtering the
/// paragraph above rejects.
/// </para>
/// <para>
/// No <c>relkind</c> filter narrows the table query, on purpose. A privilege granted on a view, a
/// sequence or a materialized view in <c>public</c> is a widening of the role's reach exactly like a
/// privilege on a table, and a kind filter would be one more discovery filter deciding in silence
/// what this test is allowed to see.
/// </para>
/// </remarks>
public sealed class AppRoleGrantMatrixTests
{
    /// <summary>
    /// The grantee every row is filtered to. A literal for the same reason the matrix below is one:
    /// reading it from <c>DatabaseProvisioning</c> would let a rename in production rename the
    /// expectation with it.
    /// </summary>
    private const string AppRoleName = "budgetoid_app";

    /// <summary>
    /// Every table-level privilege the role is meant to hold, by table. Restated from
    /// <c>app-role-grants.sql</c> — see the class remarks for why it is restated rather than read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UPDATE</c> appears on no line here, and its absence is the load-bearing half of this
    /// constant rather than an omission. Every <c>UPDATE</c> this role holds is column-level and
    /// therefore belongs in <see cref="ExpectedUpdateColumnGrants" />; a table-wide one would show up
    /// here as an unexpected entry, which is precisely how a "simplified" column list is caught.
    /// </para>
    /// <para>
    /// <c>__EFMigrationsHistory</c> is spelled the way EF creates it and the way the catalog stores
    /// it — mixed case, no quoting, because a catalog name is not SQL — so every comparison in this
    /// file is <see cref="StringComparison.Ordinal" />.
    /// </para>
    /// </remarks>
    private static readonly (string Table, string[] Privileges)[] ExpectedTableGrants =
    [
        ("currencies", ["SELECT"]),
        ("users", ["SELECT", "INSERT", "DELETE"]),
        ("credentials", ["SELECT", "INSERT"]),
        ("sessions", ["SELECT", "INSERT"]),
        ("passkey_public_keys", ["SELECT", "INSERT"]),
        ("passkey_signature_counters", ["SELECT", "INSERT"]),
        ("webauthn_challenges", ["SELECT", "INSERT", "DELETE"]),
        ("budgets", ["SELECT", "INSERT"]),
        ("accounts", ["SELECT", "INSERT", "DELETE"]),
        ("category_groups", ["SELECT", "INSERT", "DELETE"]),
        ("categories", ["SELECT", "INSERT", "DELETE"]),
        ("payees", ["SELECT", "INSERT"]),
        ("transactions", ["SELECT", "INSERT", "DELETE"]),
        ("__EFMigrationsHistory", ["SELECT"]),
    ];

    /// <summary>
    /// Every column the role may write, by table — the load-bearing half of the matrix. Each list is
    /// the whole of one <c>GRANT UPDATE (…)</c> in <c>app-role-grants.sql</c>, and every column of
    /// those tables that is <i>not</i> named here is immutable by that absence:
    /// <c>created_at_utc</c> everywhere, <c>budget_id</c> and <c>user_id</c> on every owned table,
    /// <c>accounts.currency_code</c>, and every identity column of a session or a counter.
    /// </summary>
    /// <remarks>
    /// The tables absent from this array hold no <c>UPDATE</c> grant of any shape —
    /// <c>currencies</c>, <c>credentials</c>, <c>passkey_public_keys</c>,
    /// <c>webauthn_challenges</c>, <c>budgets</c> and <c>__EFMigrationsHistory</c> — and their
    /// absence is checked in the same direction as everything else: a column grant appearing on one
    /// of them has no entry to match and is reported as unexpected.
    /// </remarks>
    private static readonly (string Table, string[] Columns)[] ExpectedUpdateColumnGrants =
    [
        ("users", ["email"]),
        ("sessions", ["revoked_at_utc"]),
        ("passkey_signature_counters", ["signature_counter"]),
        ("accounts", ["name", "type", "opening_balance"]),
        ("category_groups", ["name", "description", "position"]),
        ("categories", ["name", "description", "position", "category_group_id"]),
        ("payees", ["name"]),
        ("transactions", ["amount", "date", "description", "account_id", "payee_id", "category_id"]),
    ];

    [Test]
    public async Task AppRoleGrants_MatchTheDeclaredMatrix()
    {
        // Arrange — a provisioned container, and the superuser connection to read its catalogs
        // through. StartAsync has already migrated the schema and run the grants script, so what the
        // catalogs hold at this point is what a deploy would leave behind.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — what the role actually holds, in the same "subject: PRIVILEGE" shape the expectation
        // is flattened into, so a difference between the two sets reads as a line of the grants file
        // rather than as a row of a catalog.
        HashSet<string> heldTablePrivileges = await ReadTablePrivilegesAsync(admin);
        HashSet<string> heldColumnPrivileges = await ReadColumnPrivilegesAsync(admin);

        HashSet<string> held = new(heldTablePrivileges, StringComparer.Ordinal);
        held.UnionWith(heldColumnPrivileges);
        HashSet<string> expected = ExpectedMatrix();

        // Assert — one set-equality assertion carrying both directions. Anything held and not
        // declared is a widening; anything declared and not held is a privilege the application will
        // discover as a 42501 in production. Collected rather than asserted one at a time so a single
        // red run names every difference instead of stopping at the first.
        List<string> differences =
        [
            .. held.Except(expected, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(entry => $"unexpected grant: {entry}"),
            .. expected.Except(held, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(entry => $"missing grant: {entry}"),
        ];

        await Assert.That(differences).IsEmpty();
    }

    /// <summary>
    /// Flattens both halves of the declared matrix into the one comparable set. Table entries read
    /// <c>table: PRIVILEGE</c> and column entries <c>table.column: UPDATE</c>, which keeps the two
    /// kinds of grant distinguishable in the same set — a table-wide <c>UPDATE</c> can never
    /// accidentally satisfy a column-level expectation, or the widening this file is about would
    /// cancel itself out.
    /// </summary>
    private static HashSet<string> ExpectedMatrix()
    {
        HashSet<string> expected = new(StringComparer.Ordinal);

        foreach ((string table, string[] privileges) in ExpectedTableGrants)
        {
            foreach (string privilege in privileges)
            {
                expected.Add(TableEntry(table, privilege));
            }
        }

        foreach ((string table, string[] columns) in ExpectedUpdateColumnGrants)
        {
            foreach (string column in columns)
            {
                expected.Add(ColumnEntry(table, column, "UPDATE"));
            }
        }

        return expected;
    }

    private static string TableEntry(string table, string privilege) => $"{table}: {privilege}";

    private static string ColumnEntry(string table, string column, string privilege) =>
        $"{table}.{column}: {privilege}";

    /// <summary>
    /// Marks an entry read out of the catalog as grantable, so a privilege carrying
    /// <c>WITH GRANT OPTION</c> is a different member of the set from the plain privilege of the
    /// same name. Nothing in the expected matrix is ever built through this, which is the point: the
    /// declared matrix holds no grant option anywhere, so a grantable privilege can only ever land in
    /// the unexpected half of the comparison.
    /// </summary>
    private static string Grantable(string entry, bool isGrantable) =>
        isGrantable ? $"{entry} WITH GRANT OPTION" : entry;

    /// <summary>
    /// Every table-level privilege the role holds on a relation in <c>public</c>, straight out of
    /// <c>relacl</c>.
    /// </summary>
    /// <remarks>
    /// <c>aclexplode</c> turns the access-control list into one row per (grantor, grantee,
    /// privilege, is_grantable), which is what makes both the grantee and the grant option
    /// observable: <c>relacl</c> stores oids and option flags packed into an aclitem, and neither is
    /// readable without exploding it. <c>cross join lateral</c> rather than a plain call so that a
    /// relation with a null <c>relacl</c> — no grant of any kind, the default — contributes no rows
    /// instead of a null one. The grantee predicate is the whole of the class remarks' second
    /// paragraph in one line: oid <c>0</c> is <c>PUBLIC</c>, which has no <c>pg_roles</c> row to join
    /// to, and <c>pg_has_role(…, 'USAGE')</c> answers both a direct grant and one inherited through
    /// membership. <c>cast(@role as name)</c> because the parameter arrives as <c>text</c> and the
    /// three-argument overloads are declared over <c>name</c>.
    /// </remarks>
    private const string TablePrivilegeSql =
        """
        select c.relname, acl.privilege_type, acl.is_grantable
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        cross join lateral aclexplode(c.relacl) acl
        where n.nspname = 'public'
          and (acl.grantee = 0 or pg_has_role(cast(@role as name), acl.grantee, 'USAGE'))
        """;

    /// <summary>
    /// Every column-level privilege the role holds in <c>public</c>, straight out of <c>attacl</c>.
    /// </summary>
    /// <remarks>
    /// <c>attnum &gt; 0</c> excludes the system columns, which carry no grants and would only add
    /// noise; <c>not attisdropped</c> excludes the tombstones a dropped column leaves behind, whose
    /// name is a placeholder rather than anything a grants file could have written. The privilege
    /// type is read rather than assumed to be <c>UPDATE</c>: a column-level <c>SELECT</c> or
    /// <c>INSERT</c> grant is a widening too, and one nobody would think to probe for. The grantee
    /// predicate and <c>is_grantable</c> are read exactly as they are for
    /// <see cref="TablePrivilegeSql" /> — a column grant can be made to <c>PUBLIC</c>, inherited
    /// through membership, or handed out <c>WITH GRANT OPTION</c> just as a table grant can.
    /// </remarks>
    private const string ColumnPrivilegeSql =
        """
        select c.relname, a.attname, acl.privilege_type, acl.is_grantable
        from pg_attribute a
        join pg_class c on c.oid = a.attrelid
        join pg_namespace n on n.oid = c.relnamespace
        cross join lateral aclexplode(a.attacl) acl
        where n.nspname = 'public'
          and a.attnum > 0
          and not a.attisdropped
          and (acl.grantee = 0 or pg_has_role(cast(@role as name), acl.grantee, 'USAGE'))
        """;

    private static async Task<HashSet<string>> ReadTablePrivilegesAsync(NpgsqlConnection connection)
    {
        HashSet<string> held = new(StringComparer.Ordinal);
        await using NpgsqlCommand command = new(TablePrivilegeSql, connection);
        command.Parameters.AddWithValue("role", AppRoleName);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            held.Add(Grantable(
                TableEntry(reader.GetString(0), reader.GetString(1)), reader.GetBoolean(2)));
        }

        return held;
    }

    private static async Task<HashSet<string>> ReadColumnPrivilegesAsync(NpgsqlConnection connection)
    {
        HashSet<string> held = new(StringComparer.Ordinal);
        await using NpgsqlCommand command = new(ColumnPrivilegeSql, connection);
        command.Parameters.AddWithValue("role", AppRoleName);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            held.Add(Grantable(
                ColumnEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
                reader.GetBoolean(3)));
        }

        return held;
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
