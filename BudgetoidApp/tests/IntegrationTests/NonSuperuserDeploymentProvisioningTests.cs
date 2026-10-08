using Infrastructure.Persistence.Provisioning;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Runs the whole deploy-time provisioning step as the kind of principal production actually has: a
/// role that may create roles and owns the schema, and is <b>not</b> a superuser. Azure Database for
/// PostgreSQL hands out no superuser to anyone — the deploy identity is a member of
/// <c>azure_pg_admin</c> — so every privilege check inside <c>app-role-grants.sql</c> is unverified by
/// a test that runs the script as the server's superuser.
/// </summary>
/// <remarks>
/// <para>
/// That gap has already shipped a broken deploy once. The script carried
/// <c>ALTER ROLE budgetoid_app SET app.current_budget_id = ''</c>, which PostgreSQL allows only to a
/// superuser because <c>app.current_budget_id</c> is a <i>placeholder</i> parameter — no loaded
/// extension has registered it, so the server cannot validate the value and refuses to store it as a
/// role default for anyone else. Under Testcontainers the statement ran as superuser and passed; in
/// production it failed with <c>42501</c> and took the deploy with it.
/// </para>
/// <para>
/// <c>DeploymentProvisioningTests</c> deliberately observes the schema through the superuser
/// connection, and that is right for what it asserts: <c>pg_class</c> and <c>pg_policy</c> read the
/// same whoever asks. This file asserts the one thing that connection structurally cannot — that the
/// statements provisioning sends are within reach of the identity that will send them.
/// </para>
/// </remarks>
public sealed class NonSuperuserDeploymentProvisioningTests : IAsyncDisposable
{
    /// <summary>
    /// This test's database and roles on the shared server; see <see cref="ProvisioningSandbox" />.
    /// Every role here is the sandbox's, so a deploy principal created by one test is never the one
    /// another test is restricting.
    /// </summary>
    private ProvisioningSandbox? _sandbox;

    private ProvisioningSandbox Sandbox =>
        _sandbox ?? throw new InvalidOperationException("The sandbox is created before each test.");

    /// <summary>The application role this file provisions, in place of <c>budgetoid_app</c>.</summary>
    private string AppRoleName => Sandbox.Role.Name;

    /// <summary>
    /// The deploy principal this file provisions through: <c>LOGIN CREATEROLE</c>, owner of the
    /// application database, and no superuser attribute.
    /// </summary>
    /// <remarks>
    /// <c>CREATEROLE</c> alone is not enough to reach <c>budgetoid_app</c>: PostgreSQL grants a
    /// <c>CREATEROLE</c> non-superuser authority only over roles it created (or was given
    /// <c>ADMIN OPTION</c> on), so a <c>budgetoid_app</c> that already existed by another hand would
    /// refuse every <c>ALTER ROLE</c> here with "Only roles with the CREATEROLE attribute and the
    /// ADMIN option". The script's <c>DO</c> block creates the role itself, which is what makes this
    /// work and is also how production reaches the same state.
    /// </remarks>
    private string DeployAdminRole => Sandbox.HelperRole("deploy_admin");

    /// <summary>
    /// Password for <see cref="DeployAdminRole"/>. A constant is fine: the role lives for one test and
    /// is dropped with its sandbox.
    /// </summary>
    private const string DeployAdminPassword = "deploy-admin-password";

    /// <summary>
    /// The session setting the isolation policies read. Setting it as a role default is the statement
    /// that broke the deploy, and re-sending it below is what proves this server reproduces the
    /// Azure restriction rather than merely running a different script successfully.
    /// </summary>
    private const string PlaceholderParameter = "app.current_budget_id";

    /// <summary>
    /// A role holding <c>ALL WITH GRANT OPTION</c> on schema <c>public</c> and on the database, and
    /// owning neither — the part <c>azure_pg_admin</c> plays for the deploy principal on Azure.
    /// </summary>
    private string GrantHolderRole => Sandbox.HelperRole("grant_holder");

    /// <summary>A second holder of the same grant option, for the two-holder refusal.</summary>
    private string SecondGrantHolderRole => Sandbox.HelperRole("second_holder");

    [Before(Test)]
    public async Task CreateSandboxAsync() => _sandbox = await ProvisioningSandbox.CreateAsync();

    public async ValueTask DisposeAsync()
    {
        if (_sandbox is not null)
        {
            await _sandbox.DisposeAsync();
        }
    }

    [Test]
    public async Task ProvisionAsync_AsANonSuperuserCreateroleAdmin_ProvisionsTheDatabase()
    {
        // Arrange — a bare database plus a deploy principal built to match what Azure gives out:
        // allowed to create roles, owner of the database (which in PostgreSQL 15+ is what carries
        // CREATE on schema public, so the migration and the grants have somewhere to land), and
        // without the superuser attribute that makes every privilege check below vacuous.
        await using (NpgsqlConnection superuser = await OpenSuperuserAsync())
        {
            await ExecuteAsync(
                superuser,
                $"create role {DeployAdminRole} with login createrole "
                + $"password '{DeployAdminPassword}'");
            await ExecuteAsync(superuser, $"alter database {Sandbox.Database} owner to {DeployAdminRole}");
        }

        string deployAdminConnectionString = BuildDeployAdminConnectionString();

        // Act
        Exception? provisioningFailure = null;
        try
        {
            await DeploymentDatabaseProvisioning.ProvisionAsync(deployAdminConnectionString, Sandbox.Role);
        }
        catch (Exception exception)
        {
            provisioningFailure = exception;
        }

        await using NpgsqlConnection admin = await OpenSuperuserAsync();
        bool deployAdminIsSuperuser = await IsSuperuserAsync(admin, DeployAdminRole);
        (bool roleExists, bool canLogin, bool hasNoPassword) = await ReadAppRoleAsync(admin);

        // The control that makes this test mean what it claims. Provisioning succeeding proves the
        // script is within the deploy principal's reach only if that principal is genuinely
        // restricted — so the removed statement is sent again, by the same role, on the same server.
        await using NpgsqlConnection deployAdmin = new(deployAdminConnectionString);
        await deployAdmin.OpenAsync();
        string? placeholderDefaultSqlState = await TrySetRoleDefaultAsync(
            deployAdmin, $"{PlaceholderParameter} = ''");

        // And the counter-control: the same role, the same ALTER ROLE ... SET grammar, a parameter
        // the server knows. Without this, the refusal above would be equally explained by a principal
        // that cannot alter budgetoid_app at all, and the test would be pinning the wrong restriction.
        string? knownParameterSqlState = await TrySetRoleDefaultAsync(
            deployAdmin, "statement_timeout = '5s'");

        // Taken back at once: a stored session default is itself a widening the reach verifier
        // refuses, and the counter-control has already said what it came to say.
        await ExecuteAsync(
            deployAdmin, $"alter role {AppRoleName} reset statement_timeout");

        // The membership this principal holds IN the application role because it created it.
        // PostgreSQL grants it only to a creator that is not a superuser (measured on
        // postgres:17.10: a superuser-created role has no pg_auth_members row, a CREATEROLE
        // creator's has one, with ADMIN OPTION). It widens the creator, not the role, which is why
        // the reach rule reads pg_auth_members.member and never roleid. Asserted present so the
        // verifier's acceptance below is not vacuous: this is the one row a rule reading the wrong
        // column would refuse.
        bool creatorIsMemberOfAppRole =
            await CreatorMembershipExistsAsync(admin, DeployAdminRole);

        // The reach verifier, called on its own as this principal. ProvisionAsync above already ran
        // it as its last step; calling it again here keeps this line red or green by itself, so a
        // refusal reads as the verifier's rather than as one more way provisioning can fail.
        Exception? reachFailure = null;
        try
        {
            await DeploymentDatabaseProvisioning.VerifyAppRoleReachAsync(deployAdminConnectionString, Sandbox.Role);
        }
        catch (Exception exception)
        {
            reachFailure = exception;
        }

        // Assert — the failure first and as the exception rather than a boolean, so a regression
        // reports the SQLSTATE and the statement that produced it instead of "expected true".
        await Assert.That(provisioningFailure).IsNull();
        await Assert.That(creatorIsMemberOfAppRole).IsTrue();
        await Assert.That(reachFailure).IsNull();

        await Assert.That(deployAdminIsSuperuser).IsFalse();
        await Assert.That(placeholderDefaultSqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(knownParameterSqlState).IsNull();

        // The role provisioning was supposed to leave behind, in the same shape the superuser path
        // produces. A run that swallowed a privilege failure part-way through the script would still
        // satisfy "no exception" — this is what says the whole script ran.
        await Assert.That(roleExists).IsTrue();
        await Assert.That(canLogin).IsTrue();
        await Assert.That(hasNoPassword).IsTrue();
    }

    [Test]
    public async Task ProvisionAsync_AsAPrincipalInheritingTheOneSchemaGrantOptionHolder_ProvisionsTheDatabase()
    {
        // Arrange — the Azure shape. The superuser keeps the database, as the platform
        // keeps it on Azure; the deploy principal owns nothing and reaches schema public and the
        // database only by inheriting a role that holds ALL WITH GRANT OPTION on both, as a member
        // of azure_pg_admin does. Every GRANT USAGE the script sends is then recorded with that
        // role, not the schema's owner, as its grantor — and it is the grant the script means to
        // make.
        //
        // The owner takes TEMPORARY from PUBLIC here because the script cannot: a REVOKE sent by a
        // grant-option holder takes back only that holder's own entries, and PUBLIC's TEMPORARY is
        // the owner's. That refusal is correct and is the database rule's to report; leaving it in
        // would make this test red for a reason it is not about.
        await using (NpgsqlConnection superuser = await OpenSuperuserAsync())
        {
            await ExecuteAsync(superuser, $"create role {GrantHolderRole} nologin");
            await ExecuteAsync(
                superuser, $"grant all on schema public to {GrantHolderRole} with grant option");
            await ExecuteAsync(
                superuser, $"grant all on database {Sandbox.Database} to {GrantHolderRole} with grant option");
            await ExecuteAsync(
                superuser,
                $"create role {DeployAdminRole} with login createrole "
                + $"password '{DeployAdminPassword}'");
            await ExecuteAsync(
                superuser, $"grant {GrantHolderRole} to {DeployAdminRole} with inherit true");
            await ExecuteAsync(superuser, $"revoke temporary on database {Sandbox.Database} from public");
        }

        string deployAdminConnectionString = BuildDeployAdminConnectionString();

        // Act
        Exception? provisioningFailure = null;
        try
        {
            await DeploymentDatabaseProvisioning.ProvisionAsync(deployAdminConnectionString, Sandbox.Role);
        }
        catch (Exception exception)
        {
            provisioningFailure = exception;
        }

        await using NpgsqlConnection admin = await OpenSuperuserAsync();
        IReadOnlyList<string> usageGrantors = await ReadSchemaPublicUsageGrantorsAsync(admin);

        // Assert — the script's own GRANT USAGE landed with the holder as its grantor, which is the
        // shape under test; then the deploy went through.
        await Assert.That(usageGrantors).IsEquivalentTo(new[] { GrantHolderRole });
        await Assert.That(provisioningFailure).IsNull();
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_GrantMadeThroughASetOnlyMembership_ThrowsNamingTheGrantor()
    {
        // Arrange — the same holder, but the principal is a member it may SET ROLE to and does not
        // inherit from. Such a member's own GRANT USAGE on the schema grants nothing (measured on
        // postgres:17.10: WARNING "no privileges were granted"), so a USAGE entry naming the holder
        // was made by somebody who switched to it on purpose, and the principal's REVOKE does not
        // reach it. Provisioned as the superuser, then the holder's grant made by hand, then
        // verified as the principal.
        await using (NpgsqlConnection superuser = await OpenSuperuserAsync())
        {
            await ExecuteAsync(superuser, $"create role {GrantHolderRole} nologin");
            await ExecuteAsync(
                superuser, $"grant all on schema public to {GrantHolderRole} with grant option");
            await ExecuteAsync(
                superuser, $"grant all on database {Sandbox.Database} to {GrantHolderRole} with grant option");
            await ExecuteAsync(
                superuser,
                $"create role {DeployAdminRole} with login createrole "
                + $"password '{DeployAdminPassword}'");
            await ExecuteAsync(
                superuser,
                $"grant {GrantHolderRole} to {DeployAdminRole} with inherit false, set true");
            await ExecuteAsync(superuser, $"revoke temporary on database {Sandbox.Database} from public");
        }

        await DeploymentDatabaseProvisioning.ProvisionAsync(Sandbox.AdminConnectionString, Sandbox.Role);

        await using NpgsqlConnection admin = await OpenSuperuserAsync();
        await ExecuteAsync(
            admin,
            $"set role {GrantHolderRole}; "
            + $"grant usage on schema public to {AppRoleName}; reset role");
        IReadOnlyList<string> usageGrantors = await ReadSchemaPublicUsageGrantorsAsync(admin);
        bool principalInheritsHolder =
            await HasRoleAsync(admin, DeployAdminRole, GrantHolderRole, "USAGE");
        bool principalIsMemberOfHolder =
            await HasRoleAsync(admin, DeployAdminRole, GrantHolderRole, "MEMBER");

        // Act
        Exception? reachFailure = null;
        try
        {
            await DeploymentDatabaseProvisioning.VerifyAppRoleReachAsync(
                BuildDeployAdminConnectionString(), Sandbox.Role);
        }
        catch (Exception exception)
        {
            reachFailure = exception;
        }

        // Assert — the holder's entry is there beside the owner's, the membership is SET-only, and
        // the non-owner sentence names the holder.
        await Assert.That(usageGrantors).Contains(GrantHolderRole);
        await Assert.That(principalInheritsHolder).IsFalse();
        await Assert.That(principalIsMemberOfHolder).IsTrue();
        await Assert.That(reachFailure).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsWhere(
                    reachFailure,
                    problem => problem.Contains("Schema public", StringComparison.Ordinal)
                        && problem.Contains("USAGE", StringComparison.Ordinal)
                        && problem.Contains(
                            $"with {GrantHolderRole} as the grantor rather than its owner",
                            StringComparison.Ordinal)))
            .IsNotEmpty();
    }

    [Test]
    public async Task ProvisionAsync_AsAPrincipalInheritingTwoSchemaGrantOptionHolders_RefusesTheDeploy()
    {
        // Arrange — the principal inherits two roles that each hold the grant option on schema
        // public. PostgreSQL records one of them as the grantor of the script's GRANT USAGE, and
        // its documentation for GRANT leaves which one unspecified, so the principal's own REVOKE cannot be relied on to reach the
        // entry the other left. Only a single inherited holder makes the grantor something the
        // principal's statements decide.
        await using (NpgsqlConnection superuser = await OpenSuperuserAsync())
        {
            await ExecuteAsync(superuser, $"create role {GrantHolderRole} nologin");
            await ExecuteAsync(superuser, $"create role {SecondGrantHolderRole} nologin");
            await ExecuteAsync(
                superuser, $"grant all on schema public to {GrantHolderRole} with grant option");
            await ExecuteAsync(
                superuser,
                $"grant all on schema public to {SecondGrantHolderRole} with grant option");
            await ExecuteAsync(
                superuser, $"grant all on database {Sandbox.Database} to {GrantHolderRole} with grant option");
            await ExecuteAsync(
                superuser,
                $"create role {DeployAdminRole} with login createrole "
                + $"password '{DeployAdminPassword}'");
            await ExecuteAsync(
                superuser, $"grant {GrantHolderRole} to {DeployAdminRole} with inherit true");
            await ExecuteAsync(
                superuser, $"grant {SecondGrantHolderRole} to {DeployAdminRole} with inherit true");
            await ExecuteAsync(superuser, $"revoke temporary on database {Sandbox.Database} from public");
        }

        // Act
        Exception? provisioningFailure = null;
        try
        {
            await DeploymentDatabaseProvisioning.ProvisionAsync(
                BuildDeployAdminConnectionString(), Sandbox.Role);
        }
        catch (Exception exception)
        {
            provisioningFailure = exception;
        }

        await using NpgsqlConnection admin = await OpenSuperuserAsync();
        IReadOnlyList<string> usageGrantors = await ReadSchemaPublicUsageGrantorsAsync(admin);
        long inheritedHolders = await ScalarLongAsync(
            admin,
            "select count(*) from pg_namespace n, aclexplode(n.nspacl) a "
            + "where n.nspname = 'public' and a.is_grantable and a.privilege_type = 'USAGE' "
            + $"and pg_has_role('{DeployAdminRole}', a.grantee, 'USAGE')");

        // Assert — two holders reach the principal, one of them is the recorded grantor, and the
        // deploy is refused with the non-owner sentence naming whichever it was.
        await Assert.That(inheritedHolders).IsEqualTo(2L);
        await Assert.That(usageGrantors.Count).IsEqualTo(1);
        await Assert.That(new[] { GrantHolderRole, SecondGrantHolderRole }).Contains(usageGrantors[0]);
        await Assert.That(provisioningFailure).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                ProblemsWhere(
                    provisioningFailure,
                    problem => problem.Contains("Schema public", StringComparison.Ordinal)
                        && problem.Contains("USAGE", StringComparison.Ordinal)
                        && problem.Contains(
                            $"with {usageGrantors[0]} as the grantor rather than its owner",
                            StringComparison.Ordinal)))
            .IsNotEmpty();
    }

    /// <summary>
    /// Opens a connection to the sandbox database as the server's superuser. Used only to build the
    /// restricted principal and to read the catalogs afterwards — never to provision, which is the
    /// entire point of this file.
    /// </summary>
    private Task<NpgsqlConnection> OpenSuperuserAsync() => Sandbox.OpenAdminAsync();

    /// <summary>
    /// The sandbox's connection string re-pointed at <see cref="DeployAdminRole"/>, so provisioning
    /// reaches the same database over the same options as everything else here.
    /// </summary>
    private string BuildDeployAdminConnectionString() =>
        new NpgsqlConnectionStringBuilder(Sandbox.AdminConnectionString)
        {
            Username = DeployAdminRole,
            Password = DeployAdminPassword,
        }.ConnectionString;

    /// <summary>
    /// Sends <c>ALTER ROLE budgetoid_app SET <paramref name="assignment"/></c> and reports the
    /// SQLSTATE it was refused with, or <see langword="null"/> if it was accepted.
    /// </summary>
    /// <remarks>
    /// The assignment is spliced rather than bound because <c>ALTER ROLE ... SET</c> takes no
    /// parameter; both call sites pass a constant of this class.
    /// </remarks>
    private async Task<string?> TrySetRoleDefaultAsync(
        NpgsqlConnection connection,
        string assignment)
    {
        try
        {
            await ExecuteAsync(
                connection, $"alter role {AppRoleName} set {assignment}");
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    /// <summary>Reports whether a role carries the superuser attribute.</summary>
    private static async Task<bool> IsSuperuserAsync(NpgsqlConnection connection, string role)
    {
        await using NpgsqlCommand command = new(
            "select coalesce((select rolsuper from pg_roles where rolname = @role), false)",
            connection);
        command.Parameters.AddWithValue("role", role);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Reads whether the application role exists, may log in, and has no password, from
    /// <c>pg_authid</c> in one row — the same three facts <c>DeploymentProvisioningTests</c> pins on
    /// the superuser path, asserted here about the restricted one.
    /// </summary>
    private async Task<(bool Exists, bool CanLogin, bool HasNoPassword)> ReadAppRoleAsync(
        NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            select rolcanlogin, rolpassword is null
            from pg_authid
            where rolname = @role
            """,
            connection);
        command.Parameters.AddWithValue("role", AppRoleName);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (false, false, false);
        }

        return (true, reader.GetBoolean(0), reader.GetBoolean(1));
    }

    /// <summary>
    /// Reports whether <c>pg_auth_members</c> holds the row in which <paramref name="creator"/> is a
    /// member of the application role (<c>roleid</c> = the application role,
    /// <c>member</c> = the creator).
    /// </summary>
    private async Task<bool> CreatorMembershipExistsAsync(
        NpgsqlConnection connection,
        string creator)
    {
        await using NpgsqlCommand command = new(
            """
            select exists (
                select 1
                from pg_auth_members m
                join pg_roles granted on granted.oid = m.roleid
                join pg_roles holder on holder.oid = m.member
                where granted.rolname = @appRole and holder.rolname = @creator)
            """,
            connection);
        command.Parameters.AddWithValue("appRole", AppRoleName);
        command.Parameters.AddWithValue("creator", creator);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The recorded grantor of every <c>USAGE</c> entry on schema <c>public</c> whose grantee is the
    /// application role, raw from <c>nspacl</c>.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadSchemaPublicUsageGrantorsAsync(
        NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            select pg_get_userbyid(a.grantor)
            from pg_namespace n
            cross join lateral aclexplode(n.nspacl) a
            where n.nspname = 'public'
              and a.privilege_type = 'USAGE'
              and a.grantee = (select oid from pg_roles where rolname = @role)
            order by 1
            """,
            connection);
        command.Parameters.AddWithValue("role", AppRoleName);

        List<string> grantors = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            grantors.Add(reader.GetString(0));
        }

        return grantors;
    }

    /// <summary>
    /// <c>pg_has_role(member, role, mode)</c>: <c>USAGE</c> asks whether the member inherits the
    /// role's privileges, <c>MEMBER</c> whether it belongs to it at all.
    /// </summary>
    private static async Task<bool> HasRoleAsync(
        NpgsqlConnection connection,
        string member,
        string role,
        string mode)
    {
        await using NpgsqlCommand command = new("select pg_has_role(@member, @role, @mode)", connection);
        command.Parameters.AddWithValue("member", member);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("mode", mode);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Runs a query returning one <c>bigint</c>. The SQL is built from constants.</summary>
    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The problems of a caught <see cref="AppRoleReachException" /> that satisfy
    /// <paramref name="predicate" />; empty when nothing was caught or it was another type. A
    /// collection, because TUnit truncates string assertions.
    /// </summary>
    private static List<string> ProblemsWhere(Exception? caught, Func<string, bool> predicate) =>
        (caught as AppRoleReachException)?.Problems.Where(predicate).ToList() ?? [];

    /// <summary>
    /// Sends one statement that is expected to succeed. Used only for setup, where the SQL is built
    /// from constants of this class rather than from input.
    /// </summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
