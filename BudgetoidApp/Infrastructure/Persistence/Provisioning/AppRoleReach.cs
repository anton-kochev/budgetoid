using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.Persistence.Provisioning;

/// <summary>The role-level attributes of the application role, as <c>pg_roles</c> reports them.</summary>
/// <param name="CanLogin">
/// <c>rolcanlogin</c>. Required: a role that cannot log in is not the role the API connects as.
/// </param>
/// <param name="Superuser">
/// <c>rolsuper</c>. A superuser bypasses every grant and every policy at once.
/// </param>
/// <param name="CreateRole"><c>rolcreaterole</c>.</param>
/// <param name="CreateDatabase"><c>rolcreatedb</c>.</param>
/// <param name="Replication"><c>rolreplication</c>.</param>
/// <param name="BypassRowLevelSecurity">
/// <c>rolbypassrls</c>. Switches every isolation policy off for this role while leaving each one
/// present and correct in the catalog.
/// </param>
public sealed record AppRoleAttributes(
    bool CanLogin,
    bool Superuser,
    bool CreateRole,
    bool CreateDatabase,
    bool Replication,
    bool BypassRowLevelSecurity);

/// <summary>
/// One privilege the application role holds on one object, read from the object's effective ACL —
/// granted to the role itself, to <c>PUBLIC</c>, or to a role it is a member of.
/// </summary>
/// <param name="ObjectKind">
/// The object's keyword as <c>REVOKE … ON</c> takes it: <c>SCHEMA</c>, <c>TABLE</c>,
/// <c>SEQUENCE</c>, <c>ROUTINE</c>, <c>PARAMETER</c>, <c>DATABASE</c> or <c>TABLESPACE</c>.
/// </param>
/// <param name="ObjectName">The object's name, quoted and qualified as <c>REVOKE</c> takes it.</param>
/// <param name="Column">
/// The column the privilege is on, for a column-level grant; <see langword="null" /> otherwise.
/// </param>
/// <param name="Grantee">
/// Who the ACL entry names: the application role, <c>PUBLIC</c>, or a role the application role is a
/// member of.
/// </param>
/// <param name="Privilege">The privilege keyword, e.g. <c>CREATE</c> or <c>EXECUTE</c>.</param>
/// <param name="WithGrantOption">
/// Whether the grantee may pass the privilege on. Any grant option is a widening: it lets the
/// holder hand out what the grant matrix gave it alone.
/// </param>
public sealed record ReachGrant(
    string ObjectKind,
    string ObjectName,
    string? Column,
    string Grantee,
    string Privilege,
    bool WithGrantOption);

/// <summary>
/// One privilege granted to the application role itself by a role that is not the object's owner,
/// and not accepted as the verifying principal's own grant.
/// </summary>
/// <remarks>
/// A <c>GRANT</c> or <c>REVOKE</c> sent by the owner, a superuser or a member inheriting the owner is
/// recorded as the owner's; one sent through a role holding a grant option is recorded as that
/// role's. A <c>REVOKE</c> takes back only the entries recorded against its own grantor — so an entry
/// recorded against another grantor survives every re-run of the grant script.
/// </remarks>
/// <param name="ObjectKind">
/// The object's keyword as <c>REVOKE … ON</c> takes it: <c>TABLE</c>, <c>SEQUENCE</c>,
/// <c>SCHEMA</c>, <c>DATABASE</c> or <c>ROUTINE</c>.
/// </param>
/// <param name="ObjectName">The object's name, quoted and qualified as <c>REVOKE</c> takes it.</param>
/// <param name="Column">
/// The column the privilege is on, for a column-level grant; <see langword="null" /> otherwise.
/// </param>
/// <param name="Grantor">The role the ACL entry records as having made the grant.</param>
/// <param name="Owner">The object's owner, which the grantor is not.</param>
/// <param name="Privilege">The privilege keyword, e.g. <c>SELECT</c> or <c>UPDATE</c>.</param>
/// <param name="WithGrantOption">Whether the role may pass the privilege on.</param>
public sealed record NonOwnerGrant(
    string ObjectKind,
    string ObjectName,
    string? Column,
    string Grantor,
    string Owner,
    string Privilege,
    bool WithGrantOption);

/// <summary>
/// One entry of a <c>pg_default_acl</c> row naming the application role, <c>PUBLIC</c>, or a role the
/// application role is a member of.
/// </summary>
/// <param name="CreatorRole">The role whose future objects the entry applies to.</param>
/// <param name="Schema">
/// The schema the entry is limited to, or <see langword="null" /> for a database-wide entry.
/// </param>
/// <param name="ObjectType">
/// The object class as <c>ALTER DEFAULT PRIVILEGES</c> spells it, e.g. <c>TABLES</c>.
/// </param>
/// <param name="Grantee">Who the entry grants to.</param>
/// <param name="Privilege">The privilege keyword the next such object will be created with.</param>
/// <param name="WithGrantOption">Whether that privilege arrives with its grant option.</param>
public sealed record DefaultPrivilege(
    string CreatorRole,
    string? Schema,
    string ObjectType,
    string Grantee,
    string Privilege,
    bool WithGrantOption);

/// <summary>Something the application role owns, as <c>pg_shdepend</c> records it.</summary>
/// <param name="Description">
/// The owned object as <c>pg_describe_object</c> names it, e.g. <c>table sabotage_owned</c>.
/// </param>
/// <param name="Shared">
/// Whether the object is cluster-wide (a database, a tablespace, a role) rather than local to the
/// current database.
/// </param>
public sealed record OwnedObject(string Description, bool Shared);

/// <summary>
/// One parameter a <c>pg_db_role_setting</c> row applies to the application role's sessions. The
/// value is deliberately absent: it is never read.
/// </summary>
/// <param name="Database">
/// The database the row is limited to, quoted as <c>ALTER … IN DATABASE</c> takes it, or
/// <see langword="null" /> for a row that applies in every database.
/// </param>
/// <param name="ForEveryRole">
/// Whether the row is stored for every role in <paramref name="Database" /> (<c>setrole = 0</c>,
/// written by <c>ALTER DATABASE … SET</c>) rather than for the application role.
/// </param>
/// <param name="Parameter">The parameter's name.</param>
public sealed record SessionDefault(string? Database, bool ForEveryRole, string Parameter);

/// <summary>A trigger somebody created, as <c>pg_trigger</c> records it.</summary>
/// <param name="Name">The trigger's name, quoted as <c>DROP TRIGGER</c> takes it.</param>
/// <param name="Table">The table it is on, quoted and qualified.</param>
/// <param name="Enabled">Whether it fires today; a disabled one is one statement from firing.</param>
/// <param name="IsConstraintTrigger">Whether it was created as a constraint trigger.</param>
public sealed record UserTrigger(string Name, string Table, bool Enabled, bool IsConstraintTrigger);

/// <summary>A rewrite rule other than a view's own <c>_RETURN</c>.</summary>
/// <param name="Name">The rule's name, quoted as <c>DROP RULE</c> takes it.</param>
/// <param name="Table">The relation it is on, quoted and qualified.</param>
public sealed record RewriteRule(string Name, string Table);

/// <summary>
/// A column a foreign key's referential action writes, set off by something the application role
/// can do, which the role could not <c>UPDATE</c> itself.
/// </summary>
/// <param name="Table">The written column's table, quoted and qualified.</param>
/// <param name="Column">The written column, quoted.</param>
/// <param name="Constraints">
/// Every foreign key whose action reaches the column, quoted as <c>ALTER TABLE … DROP CONSTRAINT</c>
/// takes it.
/// </param>
public sealed record ReferentialActionWrite(
    string Table,
    string Column,
    IReadOnlyList<string> Constraints);

/// <summary>
/// A generated column on a table where the application role can cause a column to be written, and
/// which the role could not <c>UPDATE</c> itself.
/// </summary>
/// <param name="Table">The generated column's table, quoted and qualified.</param>
/// <param name="Column">The generated column, quoted.</param>
public sealed record GeneratedColumnWrite(string Table, string Column);

/// <summary>
/// What the live catalogs say the application role can reach, read on the admin connection before
/// anyone decides whether it is too much.
/// </summary>
/// <remarks>
/// <para>
/// Discovery reads; <see cref="AppRoleReach.FindProblems" /> judges. So the grant lists hold what the
/// role reaches, not only what is wrong with it — <c>USAGE</c> on schema <c>public</c> and
/// <c>CONNECT</c> through <c>PUBLIC</c> are in here, and are the rule's to accept.
/// </para>
/// <para>
/// The categories after <paramref name="NonOwnerGrants" /> are init-only properties rather than
/// positional parameters, each defaulting to empty. <see cref="AppRoleReach.DiscoverAsync" /> is the
/// one production constructor and sets every one of them; a snapshot built by hand without them
/// describes a role those rules found nothing on.
/// </para>
/// </remarks>
/// <param name="RoleName">The role the snapshot describes.</param>
/// <param name="Attributes">
/// The role's attributes, or <see langword="null" /> when no role of that name exists — in which case
/// every list is empty, because there is no role to read them for.
/// </param>
/// <param name="MemberOf">
/// Every role the application role is a <b>member</b> of (<c>pg_auth_members.member</c>). Not the
/// roles that are members of it: on PostgreSQL 16 and later a role created by a non-superuser
/// <c>CREATEROLE</c> role gets its creator as a member, with <c>ADMIN OPTION</c> (measured on
/// postgres:17.10 and 18.3; a superuser-created role gets no such row). That widens the creator and
/// not the application role.
/// </param>
/// <param name="SchemaGrants">Every privilege the role effectively holds on any schema.</param>
/// <param name="DefaultPrivileges">
/// <c>pg_default_acl</c> entries naming the role, <c>PUBLIC</c> or a role it is a member of, each of
/// which grants the next object created rather than any existing one.
/// </param>
/// <param name="OwnedObjects">
/// Objects the role owns, in this database or cluster-wide. An owner is not subject to row-level
/// security and may grant itself anything on what it owns.
/// </param>
/// <param name="PublicRelationGrants">
/// <c>PUBLIC</c> privileges on relations and columns in schema <c>public</c>.
/// </param>
/// <param name="OutsidePublicRelationGrants">
/// Privileges the role effectively holds on relations and columns of every kind in any schema other
/// than <c>public</c>, <c>pg_catalog</c> and <c>information_schema</c> — whether or not it holds
/// <c>USAGE</c> on that schema today. The two system schemas are <see cref="SystemSchemaGrants" />.
/// </param>
/// <param name="ParameterGrants">
/// <c>pg_parameter_acl</c> entries naming the role, <c>PUBLIC</c> or a role it is a member of.
/// </param>
/// <param name="ExecutableRoutines">
/// <c>EXECUTE</c> the role effectively holds on routines outside <c>pg_catalog</c> and
/// <c>information_schema</c>.
/// </param>
/// <param name="DatabaseGrants">Every privilege the role effectively holds on the current database.</param>
/// <param name="NonOwnerGrants">
/// Privileges granted to the role itself, on relations, columns, sequences, schemas, routines and the
/// current database, whose recorded grantor is not the object's owner, less the schema entries
/// <see cref="AppRoleReach.FindProblems" /> accepts as the verifying principal's own.
/// </param>
public sealed record AppRoleReachSnapshot(
    string RoleName,
    AppRoleAttributes? Attributes,
    IReadOnlyList<string> MemberOf,
    IReadOnlyList<ReachGrant> SchemaGrants,
    IReadOnlyList<DefaultPrivilege> DefaultPrivileges,
    IReadOnlyList<OwnedObject> OwnedObjects,
    IReadOnlyList<ReachGrant> PublicRelationGrants,
    IReadOnlyList<ReachGrant> OutsidePublicRelationGrants,
    IReadOnlyList<ReachGrant> ParameterGrants,
    IReadOnlyList<ReachGrant> ExecutableRoutines,
    IReadOnlyList<ReachGrant> DatabaseGrants,
    IReadOnlyList<NonOwnerGrant> NonOwnerGrants)
{
    /// <summary>
    /// Privileges granted to the role itself — not to <c>PUBLIC</c> or a role it is a member of — on
    /// relations, columns and routines in <c>pg_catalog</c> and <c>information_schema</c>.
    /// </summary>
    public IReadOnlyList<ReachGrant> SystemSchemaGrants { get; init; } = [];

    /// <summary>
    /// Parameters stored in <c>pg_db_role_setting</c> for the role in any database, or for every role
    /// in the current database.
    /// </summary>
    public IReadOnlyList<SessionDefault> SessionDefaults { get; init; } = [];

    /// <summary>
    /// Every privilege the role effectively holds on any tablespace, through itself, <c>PUBLIC</c> or
    /// a role it is a member of.
    /// </summary>
    public IReadOnlyList<ReachGrant> TablespaceGrants { get; init; } = [];

    /// <summary>Every trigger that is not internal, in any schema, enabled or not.</summary>
    public IReadOnlyList<UserTrigger> Triggers { get; init; } = [];

    /// <summary>
    /// Every rewrite rule outside <c>pg_catalog</c> and <c>information_schema</c> other than a view's
    /// <c>_RETURN</c>.
    /// </summary>
    public IReadOnlyList<RewriteRule> Rules { get; init; } = [];

    /// <summary>
    /// Columns a chain of referential actions writes, starting from a <c>DELETE</c> or column
    /// <c>UPDATE</c> the role holds, which the role cannot <c>UPDATE</c> itself.
    /// </summary>
    public IReadOnlyList<ReferentialActionWrite> ReferentialActionWrites { get; init; } = [];

    /// <summary>
    /// Generated columns the role cannot <c>UPDATE</c>, on a table where a column the role can write,
    /// directly or through a referential action, lives.
    /// </summary>
    public IReadOnlyList<GeneratedColumnWrite> GeneratedColumnWrites { get; init; } = [];
}

/// <summary>
/// Reads what the application role can reach, over the catalogs listed below, and names every
/// widening there the grant script does not converge away.
/// </summary>
/// <remarks>
/// <para>
/// A missing grant is fail-closed and loud — the first statement that needs it answers
/// <c>42501</c>. An extra one is fail-open and silent. The role's own grants on relations in
/// <c>public</c> are converged by every run of <c>app-role-grants.sql</c> — it <c>REVOKE</c>s all on
/// every table and every sequence in the schema before re-granting — and the matrix is pinned by
/// <c>AppRoleGrantMatrixTests</c>. That convergence reaches only the entries recorded against the
/// script's own grantor: a <c>REVOKE</c> sent by the owner, a superuser or a member inheriting the
/// owner is performed as the owner, one sent through a grant-option holder is performed as that
/// holder, and neither takes back an entry another grantor recorded. So what is read here is the
/// reach a re-run leaves standing: role attributes, memberships, <c>PUBLIC</c> grants, default
/// privileges, ownership, grants on relations outside <c>public</c>, grants to the role itself in
/// <c>pg_catalog</c> and <c>information_schema</c>, grants to the role made by anyone but the
/// object's owner, privileges on schemas, parameters, routines, tablespaces and the database, stored
/// session defaults, and the writes the role can cause without holding <c>UPDATE</c> on what is
/// written — triggers, rewrite rules, referential actions and generated columns. The expected answer
/// to every rule is a fixed "nothing" (bar <c>USAGE</c> on schemas and <c>CONNECT</c> on the
/// database), so this is not a second executed copy of the grant matrix.
/// </para>
/// <para>
/// Every ACL is read as <c>coalesce(acl, acldefault(kind, owner))</c>, because a null ACL is not
/// "no privileges" but the built-in default, which for functions and databases grants
/// <c>PUBLIC</c>. <c>pg_default_acl.defaclacl</c> is the one exception: the column is never null.
/// "Holds" means effectively: an entry counts when it names the role, <c>PUBLIC</c>, or any role the
/// role is a member of (<c>pg_has_role … 'MEMBER'</c>). The system-schema rule is narrower on
/// purpose and reads only entries naming the role itself: out of the box <c>pg_catalog</c> and
/// <c>information_schema</c> grant to <c>PUBLIC</c>, the bootstrap superuser, <c>pg_monitor</c> and
/// <c>pg_read_all_stats</c> (measured on postgres:17.10 and 18.3), and a membership is refused on its
/// own.
/// </para>
/// <para>
/// The non-owner rule depends on who is asking. On a schema it accepts a grant whose grantor is the
/// one grant-option holder the verifying principal inherits, when that principal does not inherit the
/// owner — the shape a deploy principal that owns nothing has, where every <c>GRANT USAGE</c> the
/// script sends is recorded against that holder. <see cref="DiscoverAsync" /> therefore has to run on
/// a connection as the principal that ran the script; both call sites,
/// <see cref="DeploymentDatabaseProvisioning.ProvisionAsync" /> and the deploy tool, pass the one
/// admin connection string they provisioned with. Read as anybody else, the same catalog can answer
/// differently.
/// </para>
/// <para>
/// <b>Not checked, by scope rather than oversight:</b> privileges on types and domains, languages,
/// large objects, foreign data wrappers and foreign servers; <c>USAGE</c> on a schema — only
/// <c>CREATE</c> and grant options are refused there; <c>PUBLIC</c> grants and grants to a role the
/// application role is a member of in <c>pg_catalog</c> and <c>information_schema</c>; rewrite rules
/// in those two schemas; event triggers; a setting stored for every role in a database other than the
/// current one, although the role can connect to it; <c>CONNECT</c> and <c>TEMPORARY</c> on the
/// cluster's other databases, such as <c>postgres</c>, which the role holds through <c>PUBLIC</c>
/// because the grant script and the database rule are both scoped to <c>current_database()</c>; the
/// grantor of a parameter grant, because a parameter has no owner to compare it with and every
/// parameter entry naming the role is refused whoever made it; objects the role owns in another
/// database of the cluster; and any write a referential action makes into <c>pg_catalog</c> or
/// <c>information_schema</c>. A grant the script converges away on the same run is not reported
/// either — the deploy that removed it is the report.
/// </para>
/// </remarks>
public static class AppRoleReach
{
    // The grantee spelling of an ACL entry for every role, as the SQL below writes it.
    private const string Public = "PUBLIC";

    private const string RoleSql =
        """
        select oid, rolcanlogin, rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls
        from pg_roles
        where rolname = @roleName
        """;

    // member = the role. The reverse direction (roleid = the role) is the creator's automatic
    // membership on PostgreSQL 16+ when the creator is not a superuser, which widens the creator and
    // must not be refused.
    private const string MembershipSql =
        """
        select quote_ident(r.rolname)
        from pg_auth_members m
        join pg_roles r on r.oid = m.roleid
        where m.member = @role
        order by 1
        """;

    // The grantee filter every effective ACL read shares: the role itself, PUBLIC (grantee 0), or a
    // role it is a member of. pg_has_role(x, x, 'MEMBER') is true, so the first case is inside the
    // third.
    private const string GranteeFilter = "(a.grantee = 0 or pg_has_role(@role, a.grantee, 'MEMBER'))";

    private const string GranteeName =
        "case when a.grantee = 0 then 'PUBLIC' else quote_ident(pg_get_userbyid(a.grantee)) end";

    private const string SchemaSql =
        $"""
        select 'SCHEMA', quote_ident(n.nspname), null::text, {GranteeName}, a.privilege_type,
               a.is_grantable
        from pg_namespace n
        cross join lateral aclexplode(coalesce(n.nspacl, acldefault('n', n.nspowner))) a
        where {GranteeFilter}
        order by 2, 4, 5
        """;

    private const string DefaultPrivilegeSql =
        $"""
        select quote_ident(pg_get_userbyid(d.defaclrole)),
               quote_ident(n.nspname),
               case d.defaclobjtype
                   when 'r' then 'TABLES'
                   when 'S' then 'SEQUENCES'
                   when 'f' then 'FUNCTIONS'
                   when 'T' then 'TYPES'
                   when 'n' then 'SCHEMAS'
                   when 'L' then 'LARGE OBJECTS'
                   else d.defaclobjtype::text
               end,
               {GranteeName}, a.privilege_type, a.is_grantable
        from pg_default_acl d
        left join pg_namespace n on n.oid = d.defaclnamespace
        cross join lateral aclexplode(d.defaclacl) a
        where {GranteeFilter}
        order by 1, 2, 3, 4, 5
        """;

    // This database's objects and the cluster's shared ones (dbid 0). Objects in another database
    // cannot be described from here and are out of scope.
    private const string OwnedSql =
        """
        select coalesce(pg_describe_object(s.classid, s.objid, s.objsubid),
                        format('object %s of class %s', s.objid, s.classid::regclass)),
               s.dbid = 0
        from pg_shdepend s
        where s.refclassid = 'pg_authid'::regclass
          and s.refobjid = @role
          and s.deptype = 'o'
          and s.dbid in (0, (select oid from pg_database where datname = current_database()))
        order by 2 desc, 1
        """;

    // PUBLIC only. The role's own table, column and sequence grants in public are the matrix, and the
    // script's REVOKE ALL ON ALL TABLES / ALL SEQUENCES IN SCHEMA public converges the ones recorded
    // against its own grantor; a grant to the role by anyone else survives that REVOKE and is
    // NonOwnerSql's to refuse. A grant to a role it is a member of is already a refused membership.
    private const string PublicRelationSql =
        """
        select case when c.relkind = 'S' then 'SEQUENCE' else 'TABLE' end,
               format('%I.%I', n.nspname, c.relname), null::text, 'PUBLIC', a.privilege_type,
               a.is_grantable
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        cross join lateral aclexplode(coalesce(
            c.relacl,
            acldefault((case when c.relkind = 'S' then 's' else 'r' end)::"char", c.relowner))) a
        where n.nspname = 'public'
          and c.relkind in ('r', 'p', 'v', 'm', 'S', 'f')
          and a.grantee = 0
        union all
        select 'TABLE', format('%I.%I', n.nspname, c.relname), quote_ident(att.attname), 'PUBLIC',
               a.privilege_type, a.is_grantable
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        join pg_attribute att on att.attrelid = c.oid and att.attnum > 0 and not att.attisdropped
        cross join lateral aclexplode(coalesce(att.attacl, acldefault('c', c.relowner))) a
        where n.nspname = 'public'
          and c.relkind in ('r', 'p', 'v', 'm', 'f')
          and a.grantee = 0
        order by 2, 3 nulls first, 5
        """;

    // Every relkind and every grantee the role reaches through, because the script names no schema
    // but public and so converges nothing here. USAGE on the schema is deliberately not a condition:
    // it is one GRANT away, and the relation grant would already be waiting behind it. The two system
    // schemas are SystemSchemaSql's, which reads a narrower grantee set.
    private const string OutsidePublicRelationSql =
        $"""
        select case when c.relkind = 'S' then 'SEQUENCE' else 'TABLE' end,
               format('%I.%I', n.nspname, c.relname), null::text, {GranteeName}, a.privilege_type,
               a.is_grantable
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        cross join lateral aclexplode(coalesce(
            c.relacl,
            acldefault((case when c.relkind = 'S' then 's' else 'r' end)::"char", c.relowner))) a
        where n.nspname not in ('pg_catalog', 'information_schema', 'public')
          and {GranteeFilter}
        union all
        select 'TABLE', format('%I.%I', n.nspname, c.relname), quote_ident(att.attname),
               {GranteeName}, a.privilege_type, a.is_grantable
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        join pg_attribute att on att.attrelid = c.oid and att.attnum > 0 and not att.attisdropped
        cross join lateral aclexplode(coalesce(att.attacl, acldefault('c', c.relowner))) a
        where n.nspname not in ('pg_catalog', 'information_schema', 'public')
          and {GranteeFilter}
        order by 2, 3 nulls first, 4, 5
        """;

    // The role itself only. PUBLIC, the bootstrap superuser, pg_monitor and pg_read_all_stats hold
    // grants here out of the box, so the effective grantee filter would refuse every fresh database;
    // a grant through a membership is already a refused membership. Relations, columns and routines,
    // the three ACLs a system schema's objects carry.
    private const string SystemSchemaSql =
        """
        select case when c.relkind = 'S' then 'SEQUENCE' else 'TABLE' end,
               format('%I.%I', n.nspname, c.relname), null::text,
               quote_ident(pg_get_userbyid(a.grantee)), a.privilege_type, a.is_grantable
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        cross join lateral aclexplode(coalesce(
            c.relacl,
            acldefault((case when c.relkind = 'S' then 's' else 'r' end)::"char", c.relowner))) a
        where n.nspname in ('pg_catalog', 'information_schema')
          and a.grantee = @role
        union all
        select 'TABLE', format('%I.%I', n.nspname, c.relname), quote_ident(att.attname),
               quote_ident(pg_get_userbyid(a.grantee)), a.privilege_type, a.is_grantable
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        join pg_attribute att on att.attrelid = c.oid and att.attnum > 0 and not att.attisdropped
        cross join lateral aclexplode(coalesce(att.attacl, acldefault('c', c.relowner))) a
        where n.nspname in ('pg_catalog', 'information_schema')
          and a.grantee = @role
        union all
        select 'ROUTINE',
               format('%I.%I(%s)', n.nspname, p.proname, pg_get_function_identity_arguments(p.oid)),
               null::text, quote_ident(pg_get_userbyid(a.grantee)), a.privilege_type, a.is_grantable
        from pg_proc p
        join pg_namespace n on n.oid = p.pronamespace
        cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
        where n.nspname in ('pg_catalog', 'information_schema')
          and a.grantee = @role
        order by 2, 3 nulls first, 5
        """;

    // A parameter has no owner; its built-in default is the bootstrap superuser's (oid 10) and names
    // nobody else, so the owner argument only decides a row the grantee filter drops.
    private const string ParameterSql =
        $"""
        select 'PARAMETER', p.parname, null::text, {GranteeName}, a.privilege_type, a.is_grantable
        from pg_parameter_acl p
        cross join lateral aclexplode(coalesce(p.paracl, acldefault('p', 10::oid))) a
        where {GranteeFilter}
        order by 2, 4, 5
        """;

    // A routine created with no grant carries a NULL proacl, which reads as PUBLIC EXECUTE; that is
    // why acldefault is not optional here. The system schemas are SystemSchemaSql's.
    private const string RoutineSql =
        $"""
        select 'ROUTINE',
               format('%I.%I(%s)', n.nspname, p.proname, pg_get_function_identity_arguments(p.oid)),
               null::text, {GranteeName}, a.privilege_type, a.is_grantable
        from pg_proc p
        join pg_namespace n on n.oid = p.pronamespace
        cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
        where n.nspname not in ('pg_catalog', 'information_schema')
          and {GranteeFilter}
        order by 2, 4, 5
        """;

    private const string DatabaseSql =
        $"""
        select 'DATABASE', quote_ident(d.datname), null::text, {GranteeName}, a.privilege_type,
               a.is_grantable
        from pg_database d
        cross join lateral aclexplode(coalesce(d.datacl, acldefault('d', d.datdba))) a
        where d.datname = current_database()
          and {GranteeFilter}
        order by 4, 5
        """;

    // Only CREATE exists on a tablespace, so every entry the filter keeps is a finding.
    private const string TablespaceSql =
        $"""
        select 'TABLESPACE', quote_ident(t.spcname), null::text, {GranteeName}, a.privilege_type,
               a.is_grantable
        from pg_tablespace t
        cross join lateral aclexplode(coalesce(t.spcacl, acldefault('t', t.spcowner))) a
        where {GranteeFilter}
        order by 2, 4, 5
        """;

    // pg_db_role_setting only: rolconfig is the setdatabase = 0 subset of these rows, so reading it
    // too would report the same setting twice. The value is split off in SQL and never leaves the
    // server — the parameter is the finding, and a value can be anything somebody typed.
    private const string SessionDefaultSql =
        """
        select case when s.setdatabase = 0 then null else quote_ident(d.datname) end,
               s.setrole = 0,
               split_part(setting, '=', 1)
        from pg_db_role_setting s
        left join pg_database d on d.oid = s.setdatabase
        cross join lateral unnest(s.setconfig) setting
        where s.setrole = @role
           or (s.setrole = 0
               and s.setdatabase = (select oid from pg_database where datname = current_database()))
        order by 2, 1 nulls first, 3
        """;

    // tgisinternal is what separates a foreign key's own triggers, which the referential-action rule
    // judges, from one somebody created. A constraint trigger is not internal; a disabled one is one
    // ALTER TABLE from firing.
    private const string TriggerSql =
        """
        select quote_ident(t.tgname), format('%I.%I', n.nspname, c.relname), t.tgenabled <> 'D',
               t.tgconstraint <> 0
        from pg_trigger t
        join pg_class c on c.oid = t.tgrelid
        join pg_namespace n on n.oid = c.relnamespace
        where not t.tgisinternal
        order by 2, 1
        """;

    // _RETURN is a view's definition, not a rule anybody added. pg_catalog carries two rules of its
    // own on pg_settings.
    private const string RuleSql =
        """
        select quote_ident(r.rulename), format('%I.%I', n.nspname, c.relname)
        from pg_rewrite r
        join pg_class c on c.oid = r.ev_class
        join pg_namespace n on n.oid = c.relnamespace
        where r.rulename <> '_RETURN'
          and n.nspname not in ('pg_catalog', 'information_schema')
        order by 2, 1
        """;

    // What the role can cause to be written, as (table, column) pairs — attnum 0 for a whole row
    // deleted. Seeds: tables it can DELETE from and columns it can UPDATE, effectively. A referential
    // action runs as the referencing table's owner, so each step is taken whatever the role holds on
    // the child: ON DELETE CASCADE from a reached row deletes the child row; ON DELETE SET NULL / SET
    // DEFAULT writes the child's confdelsetcols (all of conkey when that is empty); ON UPDATE CASCADE /
    // SET NULL / SET DEFAULT writes the child's conkey when a reached column is in confkey. UNION, not
    // UNION ALL, so a cycle of foreign keys stops. via is the constraint that took the step.
    private const string ReachedWritesCte =
        """
        with recursive fk as (
            select con.conname, con.conrelid, con.confrelid, con.conkey, con.confkey,
                   con.confupdtype, con.confdeltype,
                   coalesce(nullif(con.confdelsetcols, '{}'), con.conkey) as delete_written
            from pg_constraint con
            join pg_class cc on cc.oid = con.conrelid
            join pg_namespace cn on cn.oid = cc.relnamespace
            where con.contype = 'f'
              and cn.nspname not in ('pg_catalog', 'information_schema')
        ),
        reach (relid, attnum, via) as (
            select c.oid, 0::int2, null::name
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname not in ('pg_catalog', 'information_schema')
              and c.relkind in ('r', 'p')
              and has_table_privilege(@role, c.oid, 'DELETE')
            union
            select a.attrelid, a.attnum, null::name
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname not in ('pg_catalog', 'information_schema')
              and c.relkind in ('r', 'p')
              and a.attnum > 0
              and not a.attisdropped
              and has_column_privilege(@role, a.attrelid, a.attnum, 'UPDATE')
            union
            select w.relid, w.attnum, fk.conname
            from reach r
            join fk on fk.confrelid = r.relid
            cross join lateral (
                select fk.conrelid, 0::int2
                where r.attnum = 0 and fk.confdeltype = 'c'
                union all
                select fk.conrelid, x
                from unnest(fk.delete_written) x
                where r.attnum = 0 and fk.confdeltype in ('n', 'd')
                union all
                select fk.conrelid, x
                from unnest(fk.conkey) x
                where r.attnum = any (fk.confkey) and fk.confupdtype in ('c', 'n', 'd')
            ) w (relid, attnum)
        )
        """;

    // A column reached through a constraint that the role could not have written itself. One row per
    // column, whichever constraints reach it.
    private const string ReferentialActionSql =
        $"""
        {ReachedWritesCte}
        select format('%I.%I', n.nspname, c.relname), quote_ident(a.attname),
               array_agg(distinct quote_ident(r.via::text) order by quote_ident(r.via::text))
        from reach r
        join pg_attribute a on a.attrelid = r.relid and a.attnum = r.attnum
        join pg_class c on c.oid = r.relid
        join pg_namespace n on n.oid = c.relnamespace
        where r.attnum > 0
          and r.via is not null
          and not has_column_privilege(@role, r.relid, r.attnum, 'UPDATE')
        group by n.nspname, c.relname, a.attname
        order by 1, 2
        """;

    // attgenerated <> '' rather than = 's': PostgreSQL 18's virtual generated columns move with their
    // base columns too.
    private const string GeneratedColumnSql =
        $"""
        {ReachedWritesCte}
        select format('%I.%I', n.nspname, c.relname), quote_ident(g.attname)
        from pg_attribute g
        join pg_class c on c.oid = g.attrelid
        join pg_namespace n on n.oid = c.relnamespace
        where g.attnum > 0
          and not g.attisdropped
          and g.attgenerated <> ''
          and exists (select 1 from reach r where r.relid = g.attrelid and r.attnum > 0)
          and not has_column_privilege(@role, g.attrelid, g.attnum, 'UPDATE')
        order by 1, 2
        """;

    // Grantee is the role itself, and the grantor is compared to the owner by oid — never to a role
    // name, because the owner differs per path (the container superuser, a non-superuser deploy
    // principal, pg_database_owner for schema public). A grant sent by a superuser or by a member of
    // the owning role is recorded with the owner as its grantor, so every grant the script makes as
    // one of those compares equal. Every schema is read, pg_catalog included: a foreign grantor is
    // wrong anywhere. Parameters are left out — pg_parameter_acl has no owner, and ParameterSql
    // already refuses every entry naming the role whoever made it.
    //
    // The schema arm alone also accepts the grant the script itself makes as a deploy principal that
    // owns nothing: a GRANT sent through an inherited grant-option holder is recorded with that holder
    // as its grantor. All three conditions are about current_user, the principal that ran the script:
    // it does not inherit the owner (a superuser or the database owner does, and falls back to
    // owner-only); it inherits the grantor ('USAGE', not 'MEMBER' — a SET-only member's own GRANT
    // records nothing, so such an entry was made by somebody switching roles on purpose); and the
    // grantor is the only role it inherits holding any grant option in the schema's ACL, because with
    // two, which one PostgreSQL records is not the principal's to decide and its REVOKE can miss the
    // other's entry. The owner's own entry carries no grant option in aclexplode, so it never counts.
    private const string NonOwnerSql =
        """
        select case when c.relkind = 'S' then 'SEQUENCE' else 'TABLE' end,
               format('%I.%I', n.nspname, c.relname), null::text,
               quote_ident(pg_get_userbyid(a.grantor)), quote_ident(pg_get_userbyid(c.relowner)),
               a.privilege_type, a.is_grantable
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        cross join lateral aclexplode(coalesce(
            c.relacl,
            acldefault((case when c.relkind = 'S' then 's' else 'r' end)::"char", c.relowner))) a
        where a.grantee = @role and a.grantor <> c.relowner
        union all
        select 'TABLE', format('%I.%I', n.nspname, c.relname), quote_ident(att.attname),
               quote_ident(pg_get_userbyid(a.grantor)), quote_ident(pg_get_userbyid(c.relowner)),
               a.privilege_type, a.is_grantable
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        join pg_attribute att on att.attrelid = c.oid and att.attnum > 0 and not att.attisdropped
        cross join lateral aclexplode(coalesce(att.attacl, acldefault('c', c.relowner))) a
        where a.grantee = @role and a.grantor <> c.relowner
        union all
        select 'SCHEMA', quote_ident(n.nspname), null::text,
               quote_ident(pg_get_userbyid(a.grantor)), quote_ident(pg_get_userbyid(n.nspowner)),
               a.privilege_type, a.is_grantable
        from pg_namespace n
        cross join lateral aclexplode(coalesce(n.nspacl, acldefault('n', n.nspowner))) a
        where a.grantee = @role and a.grantor <> n.nspowner
          and not (
              not pg_has_role(current_user, n.nspowner, 'USAGE')
              and pg_has_role(current_user, a.grantor, 'USAGE')
              and not exists (
                  select 1
                  from aclexplode(coalesce(n.nspacl, acldefault('n', n.nspowner))) holder
                  where holder.is_grantable
                    and holder.grantee <> 0
                    and holder.grantee <> a.grantor
                    and pg_has_role(current_user, holder.grantee, 'USAGE')))
        union all
        select 'DATABASE', quote_ident(d.datname), null::text,
               quote_ident(pg_get_userbyid(a.grantor)), quote_ident(pg_get_userbyid(d.datdba)),
               a.privilege_type, a.is_grantable
        from pg_database d
        cross join lateral aclexplode(coalesce(d.datacl, acldefault('d', d.datdba))) a
        where d.datname = current_database()
          and a.grantee = @role and a.grantor <> d.datdba
        union all
        select 'ROUTINE',
               format('%I.%I(%s)', n.nspname, p.proname, pg_get_function_identity_arguments(p.oid)),
               null::text,
               quote_ident(pg_get_userbyid(a.grantor)), quote_ident(pg_get_userbyid(p.proowner)),
               a.privilege_type, a.is_grantable
        from pg_proc p
        join pg_namespace n on n.oid = p.pronamespace
        cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
        where a.grantee = @role and a.grantor <> p.proowner
        order by 1, 2, 3 nulls first, 4, 6
        """;

    /// <summary>
    /// Reads the catalogs on <paramref name="connection" /> into a snapshot of what
    /// <paramref name="roleName" /> can reach.
    /// </summary>
    /// <remarks>
    /// Every catalog read here is open to any role, so a non-superuser deploy principal reads the
    /// same rows the container superuser does. What differs is the non-owner rule's schema arm, which
    /// asks about <c>current_user</c>: <paramref name="connection" /> must be open as the principal
    /// that ran the grant script.
    /// </remarks>
    /// <param name="connection">
    /// An open connection as the principal that ran <c>app-role-grants.sql</c>; the admin connection
    /// at deploy time.
    /// </param>
    /// <param name="roleName">The application role to describe.</param>
    /// <param name="cancellationToken">Cancels the catalog reads.</param>
    /// <returns>The snapshot; a missing role yields <see langword="null" /> attributes.</returns>
    public static async Task<AppRoleReachSnapshot> DiscoverAsync(
        NpgsqlConnection connection,
        string roleName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(roleName);

        (uint Oid, AppRoleAttributes Attributes)? role =
            await ReadRoleAsync(connection, roleName, cancellationToken);
        if (role is not { } found)
        {
            return new AppRoleReachSnapshot(
                roleName,
                Attributes: null,
                MemberOf: [],
                SchemaGrants: [],
                DefaultPrivileges: [],
                OwnedObjects: [],
                PublicRelationGrants: [],
                OutsidePublicRelationGrants: [],
                ParameterGrants: [],
                ExecutableRoutines: [],
                DatabaseGrants: [],
                NonOwnerGrants: []);
        }

        IReadOnlyList<string> memberOf = await ReadAsync(
            connection, MembershipSql, found.Oid, reader => reader.GetString(0), cancellationToken);
        IReadOnlyList<ReachGrant> schemaGrants =
            await ReadGrantsAsync(connection, SchemaSql, found.Oid, cancellationToken);
        IReadOnlyList<DefaultPrivilege> defaultPrivileges = await ReadAsync(
            connection,
            DefaultPrivilegeSql,
            found.Oid,
            reader => new DefaultPrivilege(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5)),
            cancellationToken);
        IReadOnlyList<OwnedObject> ownedObjects = await ReadAsync(
            connection,
            OwnedSql,
            found.Oid,
            reader => new OwnedObject(reader.GetString(0), reader.GetBoolean(1)),
            cancellationToken);
        IReadOnlyList<ReachGrant> publicRelationGrants =
            await ReadGrantsAsync(connection, PublicRelationSql, roleOid: null, cancellationToken);
        IReadOnlyList<ReachGrant> outsidePublicRelationGrants =
            await ReadGrantsAsync(connection, OutsidePublicRelationSql, found.Oid, cancellationToken);
        IReadOnlyList<ReachGrant> parameterGrants =
            await ReadGrantsAsync(connection, ParameterSql, found.Oid, cancellationToken);
        IReadOnlyList<ReachGrant> executableRoutines =
            await ReadGrantsAsync(connection, RoutineSql, found.Oid, cancellationToken);
        IReadOnlyList<ReachGrant> databaseGrants =
            await ReadGrantsAsync(connection, DatabaseSql, found.Oid, cancellationToken);
        IReadOnlyList<NonOwnerGrant> nonOwnerGrants = await ReadAsync(
            connection,
            NonOwnerSql,
            found.Oid,
            reader => new NonOwnerGrant(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(6)),
            cancellationToken);
        IReadOnlyList<ReachGrant> systemSchemaGrants =
            await ReadGrantsAsync(connection, SystemSchemaSql, found.Oid, cancellationToken);
        IReadOnlyList<SessionDefault> sessionDefaults = await ReadAsync(
            connection,
            SessionDefaultSql,
            found.Oid,
            reader => new SessionDefault(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetString(2)),
            cancellationToken);
        IReadOnlyList<ReachGrant> tablespaceGrants =
            await ReadGrantsAsync(connection, TablespaceSql, found.Oid, cancellationToken);
        IReadOnlyList<UserTrigger> triggers = await ReadAsync(
            connection,
            TriggerSql,
            roleOid: null,
            reader => new UserTrigger(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3)),
            cancellationToken);
        IReadOnlyList<RewriteRule> rules = await ReadAsync(
            connection,
            RuleSql,
            roleOid: null,
            reader => new RewriteRule(reader.GetString(0), reader.GetString(1)),
            cancellationToken);
        IReadOnlyList<ReferentialActionWrite> referentialActionWrites = await ReadAsync(
            connection,
            ReferentialActionSql,
            found.Oid,
            reader => new ReferentialActionWrite(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<string[]>(2)),
            cancellationToken);
        IReadOnlyList<GeneratedColumnWrite> generatedColumnWrites = await ReadAsync(
            connection,
            GeneratedColumnSql,
            found.Oid,
            reader => new GeneratedColumnWrite(reader.GetString(0), reader.GetString(1)),
            cancellationToken);

        return new AppRoleReachSnapshot(
            roleName,
            found.Attributes,
            memberOf,
            schemaGrants,
            defaultPrivileges,
            ownedObjects,
            publicRelationGrants,
            outsidePublicRelationGrants,
            parameterGrants,
            executableRoutines,
            databaseGrants,
            nonOwnerGrants)
        {
            SystemSchemaGrants = systemSchemaGrants,
            SessionDefaults = sessionDefaults,
            TablespaceGrants = tablespaceGrants,
            Triggers = triggers,
            Rules = rules,
            ReferentialActionWrites = referentialActionWrites,
            GeneratedColumnWrites = generatedColumnWrites,
        };
    }

    /// <summary>
    /// Names every widening in <paramref name="snapshot" />: a missing, elevated or non-login role;
    /// any membership; <c>CREATE</c> or a grant option on a schema; any default privilege; anything
    /// owned; any <c>PUBLIC</c> grant on a table or column in <c>public</c>; any grant the role holds on
    /// a relation or column outside <c>public</c>; any grant to the role itself in a system schema; any
    /// parameter grant; any executable non-system routine; <c>CREATE</c>, <c>TEMPORARY</c> or a grant
    /// option on the current database; any tablespace privilege; any stored session default; any
    /// trigger or rewrite rule somebody created; any column a referential action or a generated
    /// column writes for the role past its <c>UPDATE</c>; and any grant to the role whose grantor is
    /// neither the object's owner nor, on a schema, the verifying principal's own grant-option holder.
    /// </summary>
    /// <remarks>
    /// Two things are accepted rather than refused: <c>USAGE</c> without its grant option on a
    /// schema, and <c>CONNECT</c> without its grant option on the database — held through
    /// <c>PUBLIC</c> by decision, because revoking it would lock out every principal that connects by
    /// the default.
    /// </remarks>
    /// <param name="snapshot">What <see cref="DiscoverAsync" /> read.</param>
    /// <returns>
    /// One sentence per finding, each naming its object and how to remove it; empty when there are
    /// none.
    /// </returns>
    public static IReadOnlyList<string> FindProblems(AppRoleReachSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string role = snapshot.RoleName;

        // A missing role reads as empty lists everywhere, which is "nothing found", not "nothing
        // wrong" — so it is the whole answer rather than one problem among none.
        if (snapshot.Attributes is not { } attributes)
        {
            return
            [
                $"No role named {role} exists, so nothing the API could log in as was verified; "
                + "re-run provisioning, which creates it, and look for a role renamed away from "
                + "that name.",
            ];
        }

        List<string> problems = [];

        if (!attributes.CanLogin)
        {
            problems.Add(
                $"Role {role} cannot LOGIN, so it is not the role the API connects as; re-run "
                + $"provisioning, or run ALTER ROLE {role} LOGIN on the admin connection.");
        }

        AddAttributeProblem(problems, role, attributes.Superuser, "SUPERUSER",
            "bypasses every grant and every policy at once");
        AddAttributeProblem(problems, role, attributes.CreateRole, "CREATEROLE",
            "lets it create roles and grant itself their reach");
        AddAttributeProblem(problems, role, attributes.CreateDatabase, "CREATEDB",
            "lets it create databases it owns");
        AddAttributeProblem(problems, role, attributes.Replication, "REPLICATION",
            "lets it stream every row of the cluster past every policy");
        AddAttributeProblem(problems, role, attributes.BypassRowLevelSecurity, "BYPASSRLS",
            "switches every isolation policy off for it while each one stays present and correct in "
            + "the catalog");

        foreach (string granted in snapshot.MemberOf)
        {
            problems.Add(
                $"Role {role} is a member of {granted}, and so reaches whatever {granted} reaches — "
                + "a membership is no privilege on any table, so no REVOKE in the grant script "
                + $"touches it; run REVOKE {granted} FROM {role} on the admin connection.");
        }

        foreach (IReadOnlyList<ReachGrant> group in GroupByHolder(
                     snapshot.SchemaGrants.Where(IsSchemaWidening)))
        {
            ReachGrant first = group[0];
            problems.Add(
                $"Schema {first.ObjectName} grants {DescribePrivileges(group)} to "
                + $"{DescribeHolder(first.Grantee, role)}, beyond what the grant matrix gives it "
                + "there; a role able to make objects owns them, an owner is not subject to "
                + "row-level security, and a privilege it may pass on reaches anybody. Run "
                + $"{DescribeRevoke(group, grant => grant.Privilege != "USAGE")} on the admin "
                + "connection.");
        }

        foreach (IGrouping<(string CreatorRole, string? Schema, string ObjectType, string Grantee),
                     DefaultPrivilege> group in snapshot.DefaultPrivileges.GroupBy(
                     entry => (entry.CreatorRole, entry.Schema, entry.ObjectType, entry.Grantee)))
        {
            (string creator, string? schema, string objectType, string grantee) = group.Key;
            string inSchema = schema is null ? string.Empty : $" in schema {schema}";
            string inSchemaClause = schema is null ? string.Empty : $" IN SCHEMA {schema}";
            string privileges = string.Join(
                ", ",
                group.Select(entry => entry.WithGrantOption
                    ? $"{entry.Privilege} WITH GRANT OPTION"
                    : entry.Privilege));
            problems.Add(
                $"A default privilege gives {privileges} on {objectType} created by "
                + $"{creator}{inSchema} to {DescribeHolder(grantee, role)}: it widens nothing that "
                + "exists yet and every such object created next, before any grant script has a "
                + $"line for it. Run ALTER DEFAULT PRIVILEGES FOR ROLE {creator}{inSchemaClause} "
                + $"REVOKE {string.Join(", ", group.Select(entry => entry.Privilege).Distinct())} "
                + $"ON {objectType} FROM {grantee} on the admin connection.");
        }

        foreach (OwnedObject owned in snapshot.OwnedObjects)
        {
            string scope = owned.Shared ? " (cluster-wide)" : string.Empty;
            problems.Add(
                $"Role {role} owns {owned.Description}{scope}; an owner is not subject to row-level "
                + "security, may grant itself anything on what it owns, and keeps it through every "
                + "REVOKE. Hand it to another owner with ALTER … OWNER TO, or drop it, on the admin "
                + "connection.");
        }

        foreach (IReadOnlyList<ReachGrant> group in GroupByHolder(snapshot.PublicRelationGrants))
        {
            problems.Add(
                $"PUBLIC holds {DescribePrivileges(group)} on {DescribeObject(group[0])}, and every "
                + $"role inherits PUBLIC — {role} included, past the grant matrix, where the script's "
                + $"REVOKE ALL … FROM {role} does not reach. Run "
                + $"{DescribeRevoke(group, _ => true)} on the admin connection.");
        }

        foreach (IReadOnlyList<ReachGrant> group in GroupByHolder(snapshot.OutsidePublicRelationGrants))
        {
            problems.Add(
                $"{Capitalize(DescribeObject(group[0]))} grants {DescribePrivileges(group)} to "
                + $"{DescribeHolder(group[0].Grantee, role)}; no line of the grant script names a "
                + "schema other than public, so nothing takes it back, and it needs only USAGE on "
                + $"the schema to be reachable. Run {DescribeRevoke(group, _ => true)} on the admin "
                + "connection.");
        }

        foreach (IReadOnlyList<ReachGrant> group in GroupByHolder(snapshot.SystemSchemaGrants))
        {
            problems.Add(
                $"In a system schema, {DescribeObject(group[0])} grants {DescribePrivileges(group)} "
                + $"to {role} by name, beyond what PostgreSQL grants every role there; a system "
                + "catalog or function reaches across every tenant at once, and no line of the grant "
                + $"script names it. Run {DescribeRevoke(group, _ => true)} as a role allowed to.");
        }

        foreach (IReadOnlyList<ReachGrant> group in GroupByHolder(snapshot.ParameterGrants))
        {
            ReachGrant first = group[0];
            problems.Add(
                $"Parameter {first.ObjectName} grants {DescribePrivileges(group)} to "
                + $"{DescribeHolder(first.Grantee, role)}; no line of the grant script names a "
                + $"parameter, so nothing takes it back. Run {DescribeRevoke(group, _ => true)} on "
                + "the admin connection.");
        }

        foreach (IReadOnlyList<ReachGrant> group in GroupByHolder(snapshot.ExecutableRoutines))
        {
            ReachGrant first = group[0];
            problems.Add(
                $"Routine {first.ObjectName} grants {DescribePrivileges(group)} to "
                + $"{DescribeHolder(first.Grantee, role)}; a routine does whatever its body does, as "
                + "its owner when it is SECURITY DEFINER, and the grant matrix declares none. Run "
                + $"{DescribeRevoke(group, _ => true)} on the admin connection, or drop the routine.");
        }

        foreach (IReadOnlyList<ReachGrant> group in GroupByHolder(
                     snapshot.DatabaseGrants.Where(IsDatabaseWidening)))
        {
            ReachGrant first = group[0];
            problems.Add(
                $"Database {first.ObjectName} grants {DescribePrivileges(group)} to "
                + $"{DescribeHolder(first.Grantee, role)}, beyond the one privilege the role is "
                + "meant to hold there; it gives the role a place to put objects or rows no grant or "
                + "policy in this repository describes, or the power to hand that on. Run "
                + $"{DescribeRevoke(group, grant => grant.Privilege != "CONNECT")} on the admin "
                + "connection as the database owner — sent by anyone else it changes nothing: a role "
                + "without the grant option gets a warning, and a holder of it takes back only its "
                + "own entries, silently.");
        }

        foreach (IReadOnlyList<ReachGrant> group in GroupByHolder(snapshot.TablespaceGrants))
        {
            ReachGrant first = group[0];
            problems.Add(
                $"Tablespace {first.ObjectName} grants {DescribePrivileges(group)} to "
                + $"{DescribeHolder(first.Grantee, role)}; with CREATE there the role can place a "
                + "relation it owns, and no line of the grant script names a tablespace. Run "
                + $"{DescribeRevoke(group, _ => true)} as the tablespace's owner.");
        }

        foreach (SessionDefault setting in snapshot.SessionDefaults)
        {
            problems.Add(DescribeSessionDefault(setting, role));
        }

        foreach (UserTrigger trigger in snapshot.Triggers)
        {
            string state = (trigger.IsConstraintTrigger, trigger.Enabled) switch
            {
                (true, true) => "a constraint trigger, enabled",
                (true, false) => "a constraint trigger, disabled and one ALTER TABLE from firing",
                (false, true) => "enabled",
                (false, false) => "disabled and one ALTER TABLE from firing",
            };
            problems.Add(
                $"Trigger {trigger.Name} on {trigger.Table} ({state}) runs a function on the role's "
                + "statements that may write columns the statement never named, and a column "
                + "privilege checks only what the statement names; the grant matrix declares no "
                + $"trigger. Run DROP TRIGGER {trigger.Name} ON {trigger.Table} as the table's owner.");
        }

        foreach (RewriteRule rule in snapshot.Rules)
        {
            problems.Add(
                $"Rule {rule.Name} on {rule.Table} rewrites the statements sent to it, and its "
                + "actions run with the rule owner's privileges rather than the role's; the grant "
                + $"matrix declares no rule. Run DROP RULE {rule.Name} ON {rule.Table} as the "
                + "relation's owner.");
        }

        foreach (ReferentialActionWrite write in snapshot.ReferentialActionWrites)
        {
            string constraints = string.Join(", ", write.Constraints);
            problems.Add(
                $"A referential action writes column {write.Column} of table {write.Table}, which "
                + $"{role} cannot UPDATE, whenever {role} deletes or updates what it may: the action "
                + "runs as the referencing table's owner, so no column privilege checks it. "
                + $"Constraint(s): {constraints}. Drop each, or re-create it with NO ACTION or "
                + "RESTRICT in place of the writing action, as the table's owner.");
        }

        foreach (GeneratedColumnWrite generated in snapshot.GeneratedColumnWrites)
        {
            problems.Add(
                $"A generated column, column {generated.Column} of table {generated.Table}, is "
                + $"recomputed whenever a column it reads is written, and {role} can cause a write to "
                + "that table, directly or through a foreign key's action, while it cannot UPDATE "
                + "this one; nobody's UPDATE privilege is asked about the recomputation. Drop the "
                + "generation expression, or take back what lets the role write that table, as its "
                + "owner.");
        }

        foreach (IGrouping<(string ObjectKind, string ObjectName, string? Column, string Grantor),
                     NonOwnerGrant> group in snapshot.NonOwnerGrants.GroupBy(
                     grant => (grant.ObjectKind, grant.ObjectName, grant.Column, grant.Grantor)))
        {
            NonOwnerGrant first = group.First();
            string target = first.Column is null
                ? $"{first.ObjectKind.ToLowerInvariant()} {first.ObjectName}"
                : $"column {first.Column} of {first.ObjectKind.ToLowerInvariant()} {first.ObjectName}";
            string privileges = string.Join(
                ", ",
                group.Select(grant => grant.WithGrantOption
                    ? $"{grant.Privilege} WITH GRANT OPTION"
                    : grant.Privilege));
            string columnList = first.Column is null ? string.Empty : $" ({first.Column})";
            string revoked = string.Join(
                ", ", group.Select(grant => grant.Privilege).Distinct().Select(p => p + columnList));
            string on = $"ON {first.ObjectKind} {first.ObjectName}";
            problems.Add(
                $"{Capitalize(target)} grants {privileges} to {role} with {first.Grantor} as the "
                + $"grantor rather than its owner {first.Owner}. A REVOKE takes back only the entries "
                + "recorded against its own grantor — the owner's when the owner, a superuser or a "
                + "member inheriting the owner sends it — so the grant script's REVOKE ALL … FROM "
                + $"{role} leaves this one standing on every re-run. Run SET ROLE {first.Grantor}; "
                + $"REVOKE {revoked} {on} FROM {role}; RESET ROLE on the admin connection — "
                + "REVOKE … GRANTED BY another role is refused with 0A000 — or have the owner run "
                + $"REVOKE GRANT OPTION FOR {revoked} {on} FROM {first.Grantor} CASCADE.");
        }

        return problems;
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static bool IsSchemaWidening(ReachGrant grant) =>
        grant.Privilege != "USAGE" || grant.WithGrantOption;

    private static bool IsDatabaseWidening(ReachGrant grant) =>
        grant.Privilege != "CONNECT" || grant.WithGrantOption;

    private static void AddAttributeProblem(
        List<string> problems,
        string role,
        bool isSet,
        string keyword,
        string consequence)
    {
        if (isSet)
        {
            problems.Add(
                $"Role {role} has {keyword}, which {consequence}; a role attribute is no grant, so "
                + $"the grant script never takes it back. Run ALTER ROLE {role} NO{keyword} as a "
                + "role allowed to change that attribute.");
        }
    }

    // The value is never in the sentence — it is never read. The remedy names the one statement that
    // removes this row, and the caveat is measured on postgres:17.10 and 18.3: as a CREATEROLE
    // administrator, or as the database owner, RESET of a superuser-only parameter answers 42501, and
    // RESET ALL succeeds while leaving that parameter's setting stored.
    private static string DescribeSessionDefault(SessionDefault setting, string role)
    {
        (string where, string reset) = setting switch
        {
            { ForEveryRole: true } =>
                ($"Database {setting.Database} stores a session default for {setting.Parameter} for "
                 + $"every role, {role} included",
                 $"ALTER DATABASE {setting.Database} RESET {setting.Parameter}"),
            { Database: null } =>
                ($"Role {role} stores a session default for {setting.Parameter} in every database",
                 $"ALTER ROLE {role} RESET {setting.Parameter}"),
            _ =>
                ($"Role {role} stores a session default for {setting.Parameter} in database "
                 + $"{setting.Database}",
                 $"ALTER ROLE {role} IN DATABASE {setting.Database} RESET {setting.Parameter}"),
        };

        return
            $"{where}: the server applies it to the role's sessions before the API sends a "
            + "statement, with no grant on the parameter, so no REVOKE in the grant script touches "
            + $"it. Run {reset} as a principal allowed to — a CREATEROLE administrator's or the "
            + "database owner's RESET of a superuser-only parameter answers 42501, and RESET ALL "
            + "succeeds but keeps that setting.";
    }

    // One sentence per object and grantee rather than per privilege: ALL on a table is seven rows
    // and one decision.
    private static IEnumerable<IReadOnlyList<ReachGrant>> GroupByHolder(IEnumerable<ReachGrant> grants) =>
        grants
            .GroupBy(grant => (grant.ObjectKind, grant.ObjectName, grant.Column, grant.Grantee))
            .Select(group => (IReadOnlyList<ReachGrant>)group.ToList());

    private static string DescribeHolder(string grantee, string role) =>
        grantee == role ? role
        : grantee == Public ? "PUBLIC, which every role inherits"
        : $"{grantee}, which {role} is a member of";

    private static string DescribeObject(ReachGrant grant) =>
        grant.Column is null
            ? $"{grant.ObjectKind.ToLowerInvariant()} {grant.ObjectName}"
            : $"column {grant.Column} of {grant.ObjectKind.ToLowerInvariant()} {grant.ObjectName}";

    private static string DescribePrivileges(IReadOnlyList<ReachGrant> group) =>
        string.Join(
            ", ",
            group.Select(grant => grant.WithGrantOption
                ? $"{grant.Privilege} WITH GRANT OPTION"
                : grant.Privilege));

    // A privilege that is itself the widening is revoked outright, which takes its grant option with
    // it; a declared privilege held with its grant option loses only the option.
    private static string DescribeRevoke(
        IReadOnlyList<ReachGrant> group,
        Func<ReachGrant, bool> privilegeIsTheWidening)
    {
        ReachGrant first = group[0];
        string columnList = first.Column is null ? string.Empty : $" ({first.Column})";
        string target = $"ON {first.ObjectKind} {first.ObjectName} FROM {first.Grantee}";

        List<string> statements = [];
        List<string> revoked = group.Where(privilegeIsTheWidening)
            .Select(grant => grant.Privilege + columnList)
            .ToList();
        if (revoked.Count > 0)
        {
            statements.Add($"REVOKE {string.Join(", ", revoked)} {target}");
        }

        List<string> optionOnly = group.Where(grant => !privilegeIsTheWidening(grant))
            .Select(grant => grant.Privilege + columnList)
            .ToList();
        if (optionOnly.Count > 0)
        {
            statements.Add($"REVOKE GRANT OPTION FOR {string.Join(", ", optionOnly)} {target}");
        }

        return string.Join(" and ", statements);
    }

    private static async Task<(uint Oid, AppRoleAttributes Attributes)?> ReadRoleAsync(
        NpgsqlConnection connection,
        string roleName,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(RoleSql, connection);
        command.Parameters.AddWithValue("roleName", roleName);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetFieldValue<uint>(0),
            new AppRoleAttributes(
                CanLogin: reader.GetBoolean(1),
                Superuser: reader.GetBoolean(2),
                CreateRole: reader.GetBoolean(3),
                CreateDatabase: reader.GetBoolean(4),
                Replication: reader.GetBoolean(5),
                BypassRowLevelSecurity: reader.GetBoolean(6)));
    }

    private static Task<IReadOnlyList<ReachGrant>> ReadGrantsAsync(
        NpgsqlConnection connection,
        string sql,
        uint? roleOid,
        CancellationToken cancellationToken) =>
        ReadAsync(
            connection,
            sql,
            roleOid,
            reader => new ReachGrant(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5)),
            cancellationToken);

    private static async Task<IReadOnlyList<T>> ReadAsync<T>(
        NpgsqlConnection connection,
        string sql,
        uint? roleOid,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection);
        if (roleOid is { } oid)
        {
            command.Parameters.Add(new NpgsqlParameter("role", NpgsqlDbType.Oid) { Value = oid });
        }

        List<T> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}
