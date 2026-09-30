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
/// <c>SEQUENCE</c>, <c>ROUTINE</c>, <c>PARAMETER</c> or <c>DATABASE</c>.
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
/// One privilege granted to the application role itself by a role that is not the object's owner.
/// </summary>
/// <remarks>
/// The grant script's <c>REVOKE ALL … FROM budgetoid_app</c> is performed as the object's owner even
/// when a superuser sends it, and a <c>REVOKE</c> takes back only the entries its own grantor made —
/// so an entry recorded against another grantor survives every re-run.
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
/// What the live catalogs say the application role can reach, read on the admin connection before
/// anyone decides whether it is too much.
/// </summary>
/// <remarks>
/// Discovery reads; <see cref="AppRoleReach.FindProblems" /> judges. So the grant lists hold what the
/// role reaches, not only what is wrong with it — <c>USAGE</c> on schema <c>public</c> and
/// <c>CONNECT</c> through <c>PUBLIC</c> are in here, and are the rule's to accept.
/// </remarks>
/// <param name="RoleName">The role the snapshot describes.</param>
/// <param name="Attributes">
/// The role's attributes, or <see langword="null" /> when no role of that name exists — in which case
/// every list is empty, because there is no role to read them for.
/// </param>
/// <param name="MemberOf">
/// Every role the application role is a <b>member</b> of (<c>pg_auth_members.member</c>). Not the
/// roles that are members of it: on PostgreSQL 16 and later the role that created it is granted
/// membership in it automatically, which widens the creator and not the application role.
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
/// <c>USAGE</c> on that schema today.
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
/// current database, whose recorded grantor is not the object's owner.
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
    IReadOnlyList<NonOwnerGrant> NonOwnerGrants);

/// <summary>
/// Reads what the application role can reach and names every widening the grant script does not
/// converge away.
/// </summary>
/// <remarks>
/// <para>
/// A missing grant is fail-closed and loud — the first statement that needs it answers
/// <c>42501</c>. An extra one is fail-open and silent. The role's own grants on relations in
/// <c>public</c> are converged by every run of <c>app-role-grants.sql</c> — it <c>REVOKE</c>s all on
/// every table and every sequence in the schema before re-granting — and the matrix is pinned by
/// <c>AppRoleGrantMatrixTests</c>. That convergence reaches only the entries the object's owner
/// made: a <c>REVOKE</c> is performed as the owner, even when a superuser sends it, and takes back
/// nothing another grantor recorded. So what is read here is the reach a re-run leaves standing:
/// role attributes, memberships, <c>PUBLIC</c> grants, default privileges, ownership, grants on
/// relations outside <c>public</c>, grants to the role made by anyone but the object's owner, and
/// privileges on schemas, parameters, routines and the database. The expected answer to every rule
/// is a fixed "nothing" (bar <c>USAGE</c> on schemas and <c>CONNECT</c> on the database), so this is
/// not a second executed copy of the grant matrix.
/// </para>
/// <para>
/// Every ACL is read as <c>coalesce(acl, acldefault(kind, owner))</c>, because a null ACL is not
/// "no privileges" but the built-in default, which for functions and databases grants
/// <c>PUBLIC</c>. <c>pg_default_acl.defaclacl</c> is the one exception: the column is never null.
/// "Holds" means effectively: an entry counts when it names the role, <c>PUBLIC</c>, or any role the
/// role is a member of (<c>pg_has_role … 'MEMBER'</c>).
/// </para>
/// <para>
/// <b>Not checked, by scope rather than oversight:</b> privileges on types and domains, languages,
/// large objects, foreign data wrappers and foreign servers; per-role and per-database settings
/// (<c>rolconfig</c>, <c>pg_db_role_setting</c>); <c>USAGE</c> on a schema — only <c>CREATE</c> and
/// grant options are refused there; the grantor of a parameter grant, because a parameter has no
/// owner to compare it with and every parameter entry naming the role is refused whoever made it;
/// objects the role owns in another database of the cluster. A grant the script converges away on
/// the same run is not reported either — the deploy that removed it is the report.
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
    // membership on PostgreSQL 16+, which widens the creator and must not be refused.
    private const string MembershipSql =
        """
        select quote_ident(r.rolname)
        from pg_auth_members m
        join pg_roles r on r.oid = m.roleid
        where m.member = @role
        order by 1
        """;

    // The grantee filter every ACL read shares: the role itself, PUBLIC (grantee 0), or a role it is
    // a member of. pg_has_role(x, x, 'MEMBER') is true, so the first case is inside the third.
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
    // script's REVOKE ALL ON ALL TABLES / ALL SEQUENCES IN SCHEMA public converges the ones the owner
    // made; a grant to the role by anyone else survives that REVOKE and is NonOwnerSql's to refuse. A
    // grant to a role it is a member of is already a refused membership.
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
    // it is one GRANT away, and the relation grant would already be waiting behind it.
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
    // why acldefault is not optional here.
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

    // Grantee is the role itself, and the grantor is compared to the owner by oid — never to a role
    // name, because the owner differs per path (the container superuser, a non-superuser deploy
    // principal, pg_database_owner for schema public). A grant sent by a superuser or by a member of
    // the owning role is recorded with the owner as its grantor, so every grant the script makes
    // compares equal. Every schema is read, pg_catalog included: a foreign grantor is wrong anywhere.
    // Parameters are left out — pg_parameter_acl has no owner, and ParameterSql already refuses every
    // entry naming the role whoever made it.
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
    /// same answer the container superuser does.
    /// </remarks>
    /// <param name="connection">An open connection; the admin connection at deploy time.</param>
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
            nonOwnerGrants);
    }

    /// <summary>
    /// Names every widening in <paramref name="snapshot" />: a missing, elevated or non-login role;
    /// any membership; <c>CREATE</c> or a grant option on a schema; any default privilege; anything
    /// owned; any <c>PUBLIC</c> grant on a table or column in <c>public</c>; any grant the role holds on
    /// a relation or column outside <c>public</c>; any parameter grant; any executable non-system
    /// routine; <c>CREATE</c>, <c>TEMPORARY</c> or a grant option on the current database; and any
    /// grant to the role whose grantor is not the object's owner.
    /// </summary>
    /// <remarks>
    /// Two things are accepted rather than refused: <c>USAGE</c> without its grant option on a
    /// schema, and <c>CONNECT</c> without its grant option on the database — held through
    /// <c>PUBLIC</c> by decision, because revoking it would lock out every principal that connects by
    /// the default.
    /// </remarks>
    /// <param name="snapshot">What <see cref="DiscoverAsync" /> read.</param>
    /// <returns>
    /// One sentence per widening, each naming its object and how to remove it; empty when there are
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
                + "connection as the database owner — a REVOKE on a database by anyone else is a "
                + "warning that changes nothing.");
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
                + $"grantor rather than its owner {first.Owner}. A REVOKE is performed as the owner "
                + "even when a superuser sends it and takes back only the owner's own entries, so "
                + $"the grant script's REVOKE ALL … FROM {role} leaves this one standing on every "
                + $"re-run. Run SET ROLE {first.Grantor}; REVOKE {revoked} {on} FROM {role}; RESET "
                + "ROLE on the admin connection — REVOKE … GRANTED BY another role is refused with "
                + "0A000 — or have the owner run "
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
