using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Holds the half of "an immutable column is one absent from a <c>GRANT UPDATE</c> list" that no
/// grant reader can see: that the <c>UPDATE</c> grant is the <b>only</b> way the application role can
/// make a column change. A column privilege is checked against the statement the role sends. Three
/// kinds of catalog object write columns the statement never named, and each runs with somebody
/// else's privileges — a trigger, a rewrite rule, and a foreign key's referential action.
/// </summary>
/// <remarks>
/// <para>
/// Each of the three was run on postgres:17.10 as a role holding <c>SELECT</c> on two tables and
/// <c>UPDATE (name)</c> on the parent alone. A <c>BEFORE UPDATE</c> trigger assigning
/// <c>NEW.id</c> rewrote the parent's key on the role's <c>set name = …</c> (<c>UPDATE 1</c>), while
/// <c>update … set id = default where false</c> on the same connection still answered <c>42501</c>
/// — so the column probe in <c>AppRoleGrantMatrixTests</c> reported the column refused while it was
/// being written. An <c>ON UPDATE CASCADE</c> foreign key, with <c>UPDATE (id)</c> added on the
/// parent, rewrote the child's referencing column, which the same probe on the child reported
/// refused. A rule <c>ON UPDATE … DO ALSO INSERT</c> into a table the role holds nothing on wrote a
/// row there on the role's update, while the role's own <c>INSERT</c> into it answered
/// <c>42501</c>. Hence three arms, one test each.
/// </para>
/// <para>
/// <b>(i) No user-defined trigger</b>, anywhere: <c>pg_trigger</c> rows with
/// <c>not tgisinternal</c>. A disabled trigger counts — <c>ENABLE TRIGGER</c> is one statement away
/// — and so does a constraint trigger, which <c>CREATE CONSTRAINT TRIGGER</c> records with
/// <c>tgisinternal</c> false (both measured). The triggers a foreign key installs are internal and
/// are what arm (iii) judges instead.
/// </para>
/// <para>
/// <b>(ii) No rewrite rule but a view's own</b>: <c>pg_rewrite</c> rows other than
/// <c>_RETURN</c>, which is the rule every view carries as its definition (measured). Any other rule
/// runs its action under the rule owner's privileges.
/// </para>
/// <para>
/// <b>(iii) No foreign key action that writes a referencing column the role cannot
/// <c>UPDATE</c></b>, and only when the role can set the action off. An <c>ON UPDATE</c>
/// <c>CASCADE</c>, <c>SET NULL</c> or <c>SET DEFAULT</c> fires when the role can <c>UPDATE</c> some
/// referenced column; an <c>ON DELETE SET NULL</c> or <c>SET DEFAULT</c> fires when it holds
/// <c>DELETE</c> on the referenced table. <c>ON DELETE CASCADE</c> deletes rows rather than
/// rewriting a column, so it is not this arm's subject; the migration uses it on sixteen keys and
/// <c>RESTRICT</c> on seven, with no <c>ON UPDATE</c> action anywhere. The written columns are
/// <c>conkey</c>, or <c>confdelsetcols</c> where an <c>ON DELETE SET NULL (…)</c> names a subset. The
/// role's privileges are asked through <c>has_column_privilege</c> and <c>has_table_privilege</c>
/// rather than restated: <c>AppRoleEffectivePrivileges_MatchTheDeclaredMatrix</c> pins those lists,
/// and a copy here would be a second list to keep right.
/// </para>
/// <para>
/// The scan covers every schema but <c>pg_catalog</c> and <c>information_schema</c>, and every
/// relation kind, because a trigger on a view or a rule on a table in another schema writes the same
/// columns as one in <c>public</c>. Each arm also asserts a floor, so a scan whose filter excluded
/// everything cannot pass as a clean one.
/// </para>
/// <para>
/// <b>This is a CI gate only, by decision</b>, and not a rule the deploy verifier runs. A trigger, a
/// rule or a referential action reaches production by one of two roads. A migration is the first,
/// and CI runs every migration into the database this class reads. An administrator's hand-edit is
/// the second, and it is outside the threat model for the reason
/// <c>RowLevelSecurityCoverage.FindProblems</c> gives about a crafted policy: anyone able to create a
/// trigger on a policed table can <c>DISABLE ROW LEVEL SECURITY</c> on it instead, and a deploy gate
/// that claimed to catch the one would be claiming a guarantee it cannot hold against the other.
/// </para>
/// </remarks>
public sealed class ImmutableColumnRewritePathTests
{
    /// <summary>
    /// The role the privilege half of arm (iii) is asked about. A literal for the reason
    /// <c>AppRoleGrantMatrixTests</c> gives for its own.
    /// </summary>
    private const string AppRoleName = "budgetoid_app";

    /// <summary>
    /// A foreign key the migration is known to create, so arm (iii) cannot scan nothing.
    /// </summary>
    private const string KnownForeignKey = "FK_accounts_budgets_budget_id";

    private const string TriggerSql =
        """
        select format('trigger %I on %I.%I (tgenabled %s, constraint trigger %s)',
                      t.tgname, n.nspname, c.relname, t.tgenabled, t.tgconstraint <> 0)
        from pg_trigger t
        join pg_class c on c.oid = t.tgrelid
        join pg_namespace n on n.oid = c.relnamespace
        where n.nspname not in ('pg_catalog', 'information_schema')
          and not t.tgisinternal
        order by 1
        """;

    // The same scan with the one predicate that matters removed: the foreign keys' own triggers.
    private const string InternalTriggerCountSql =
        """
        select count(*)
        from pg_trigger t
        join pg_class c on c.oid = t.tgrelid
        join pg_namespace n on n.oid = c.relnamespace
        where n.nspname not in ('pg_catalog', 'information_schema')
          and t.tgisinternal
        """;

    private const string RuleSql =
        """
        select format('rule %I on %I.%I (event %s, instead %s)',
                      r.rulename, n.nspname, c.relname, r.ev_type, r.is_instead)
        from pg_rewrite r
        join pg_class c on c.oid = r.ev_class
        join pg_namespace n on n.oid = c.relnamespace
        where n.nspname not in ('pg_catalog', 'information_schema')
          and r.rulename <> '_RETURN'
        order by 1
        """;

    // The views' own rules, which the scan above skips by name.
    private const string ViewRuleCountSql =
        """
        select count(*)
        from pg_rewrite r
        join pg_class c on c.oid = r.ev_class
        join pg_namespace n on n.oid = c.relnamespace
        where n.nspname not in ('pg_catalog', 'information_schema')
          and r.rulename = '_RETURN'
        """;

    /// <summary>
    /// Every foreign key the role can fire an action on that writes a referencing column it cannot
    /// <c>UPDATE</c> itself, named with the child columns that action writes and the ones of those
    /// the role is refused.
    /// </summary>
    /// <remarks>
    /// The two arms are computed separately because they write different columns: an
    /// <c>ON UPDATE</c> action always writes the whole of <c>conkey</c>, while an
    /// <c>ON DELETE SET NULL (…)</c> may name a subset in <c>confdelsetcols</c>.
    /// </remarks>
    private const string ForeignKeyActionSql =
        """
        with fk as (
            select con.conname, con.conrelid, con.confrelid, con.conkey, con.confkey,
                   con.confupdtype, con.confdeltype,
                   coalesce(nullif(con.confdelsetcols, '{}'), con.conkey) as delete_written,
                   format('%I.%I', cn.nspname, cc.relname) as child,
                   format('%I.%I', pn.nspname, pc.relname) as parent
            from pg_constraint con
            join pg_class cc on cc.oid = con.conrelid
            join pg_namespace cn on cn.oid = cc.relnamespace
            join pg_class pc on pc.oid = con.confrelid
            join pg_namespace pn on pn.oid = pc.relnamespace
            where con.contype = 'f'
              and cn.nspname not in ('pg_catalog', 'information_schema')
        ),
        armed as (
            select fk.conname, fk.conrelid, fk.child, fk.parent,
                   format('ON UPDATE %s', fk.confupdtype) as action, fk.conkey as written
            from fk
            where fk.confupdtype in ('c', 'n', 'd')
              and exists (
                  select 1 from unnest(fk.confkey) k(attnum)
                  where has_column_privilege(cast(@role as name), fk.confrelid, k.attnum, 'UPDATE'))
            union all
            select fk.conname, fk.conrelid, fk.child, fk.parent,
                   format('ON DELETE %s', fk.confdeltype), fk.delete_written
            from fk
            where fk.confdeltype in ('n', 'd')
              and has_table_privilege(cast(@role as name), fk.confrelid, 'DELETE')
        )
        select format('%s on %s -> %s (%s) writes %s, refused to the role: %s',
                      a.conname, a.child, a.parent, a.action,
                      (select string_agg(quote_ident(att.attname), ', ' order by att.attnum)
                       from pg_attribute att
                       where att.attrelid = a.conrelid and att.attnum = any(a.written)),
                      string_agg(quote_ident(att.attname), ', ' order by att.attnum))
        from armed a
        join pg_attribute att on att.attrelid = a.conrelid and att.attnum = any(a.written)
        where not has_column_privilege(cast(@role as name), a.conrelid, att.attnum, 'UPDATE')
        group by a.conname, a.conrelid, a.child, a.parent, a.action, a.written
        order by 1
        """;

    private const string KnownForeignKeySeenSql =
        """
        select exists (
            select 1
            from pg_constraint con
            join pg_class cc on cc.oid = con.conrelid
            join pg_namespace cn on cn.oid = cc.relnamespace
            where con.contype = 'f'
              and cn.nspname not in ('pg_catalog', 'information_schema')
              and con.conname = @name)
        """;

    [Test]
    public async Task Triggers_NoneIsUserDefined_InAnySchema()
    {
        // Arrange — the migrated schema with the grant script applied, as a deploy leaves it.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        List<string> userTriggers = await ReadStringsAsync(admin, TriggerSql);
        long internalTriggers = await ScalarAsync<long>(admin, InternalTriggerCountSql);

        // Assert — nothing but the foreign keys' own triggers, and those are there to be seen.
        await Assert.That(userTriggers).IsEmpty();
        await Assert.That(internalTriggers).IsGreaterThan(0L);
    }

    [Test]
    public async Task RewriteRules_NoneBeyondAViewsOwn_InAnySchema()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, "create view public.sabotage_rule_floor as select 1 as x");

        // Act
        List<string> rules = await ReadStringsAsync(admin, RuleSql);
        long viewRules = await ScalarAsync<long>(admin, ViewRuleCountSql);

        // Assert — the floor view's _RETURN is seen and skipped; nothing else is there.
        await Assert.That(rules).IsEmpty();
        await Assert.That(viewRules).IsGreaterThanOrEqualTo(1L);
    }

    [Test]
    public async Task ForeignKeyActions_NoneWritesAColumnTheAppRoleCannotUpdate()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        List<string> offenders = await ReadStringsAsync(
            admin, ForeignKeyActionSql, ("role", AppRoleName));
        bool knownForeignKeySeen = await ScalarAsync<bool>(
            admin, KnownForeignKeySeenSql, ("name", KnownForeignKey));

        // Assert
        await Assert.That(offenders).IsEmpty();
        await Assert.That(knownForeignKeySeen).IsTrue();
    }

    private static async Task<List<string>> ReadStringsAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, string value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        List<string> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, string value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
