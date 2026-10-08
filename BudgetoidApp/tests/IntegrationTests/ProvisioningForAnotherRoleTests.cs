using Infrastructure.Persistence.Provisioning;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins that the internal provisioning overloads act on the role they are given and on nothing else:
/// that role gets the whole grant matrix, policies and checks, and <c>budgetoid_app</c> — which every
/// other test on the server connects as — gets nothing in that database.
/// </summary>
/// <remarks>
/// This is what makes a <see cref="ProvisioningSandbox" /> honest. A deployment test that provisioned
/// its own role while the code quietly kept writing <c>budgetoid_app</c> would pass by measuring the
/// wrong role, and would be sabotaging the role the rest of the suite runs on.
/// </remarks>
public sealed class ProvisioningForAnotherRoleTests
{
    [Test]
    public async Task ProvisionAsync_GrantsAndPolicesTheGivenRole_AndLeavesBudgetoidAppOut()
    {
        // Arrange
        await using ProvisioningSandbox sandbox = await ProvisioningSandbox.CreateAsync();
        List<string> log = [];

        // Act — its own RLS coverage and reach checks run for the same role, so returning at all means
        // both passed for it.
        await DeploymentDatabaseProvisioning.ProvisionAsync(sandbox.AdminConnectionString, sandbox.Role, log.Add);

        // Assert
        await using NpgsqlConnection admin = await sandbox.OpenAdminAsync();
        string role = sandbox.Role.Name;
        await Assert.That(await ScalarAsync<bool>(admin, $"select has_table_privilege('{role}', 'public.currencies', 'SELECT')")).IsTrue();
        await Assert.That(await ScalarAsync<bool>(admin, "select has_table_privilege('budgetoid_app', 'public.currencies', 'SELECT')")).IsFalse();
        // A grant of its own, not has_schema_privilege: every role reaches public's USAGE through PUBLIC.
        await Assert.That(await ScalarAsync<long>(admin, "select count(*) from pg_namespace, aclexplode(nspacl) a where nspname = 'public' and a.grantee = 'budgetoid_app'::regrole")).IsEqualTo(0);
        await Assert.That(await ScalarAsync<long>(admin, $"select count(*) from pg_namespace, aclexplode(nspacl) a where nspname = 'public' and a.grantee = '{role}'::regrole")).IsGreaterThan(0);
        await Assert.That(await ScalarAsync<long>(admin, $"select count(*) from pg_policies where '{role}' = any(roles)")).IsGreaterThan(0);
        await Assert.That(await ScalarAsync<long>(admin, "select count(*) from pg_policies where 'budgetoid_app' = any(roles)")).IsEqualTo(0);
        await Assert.That(log).Contains(line => line.Contains($"role {role} ", StringComparison.Ordinal));
        await Assert.That(log).DoesNotContain(line => line.Contains("budgetoid_app", StringComparison.Ordinal));
    }

    [Test]
    public async Task AttachAppRolePasswordAsync_LetsTheGivenRoleLogIn()
    {
        // Arrange
        await using ProvisioningSandbox sandbox = await ProvisioningSandbox.CreateAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(sandbox.AdminConnectionString, sandbox.Role);

        // Act
        await DatabaseProvisioning.AttachAppRolePasswordAsync(sandbox.AdminConnectionString, sandbox.Role, "sandbox-password");

        // Assert
        await using NpgsqlConnection asRole = new(
            new NpgsqlConnectionStringBuilder(sandbox.AdminConnectionString)
            {
                Username = sandbox.Role.Name,
                Password = "sandbox-password",
            }.ConnectionString);
        await asRole.OpenAsync();
        await Assert.That(await ScalarAsync<string>(asRole, "select current_user")).IsEqualTo(sandbox.Role.Name);
    }

    [Test]
    public async Task VerifyAppRoleReachAsync_JudgesTheGivenRole()
    {
        // Arrange — provisioned, then the given role over-granted; budgetoid_app is untouched here.
        await using ProvisioningSandbox sandbox = await ProvisioningSandbox.CreateAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(sandbox.AdminConnectionString, sandbox.Role);
        await using NpgsqlConnection admin = await sandbox.OpenAdminAsync();
        await using (NpgsqlCommand grant = new($"grant create on schema public to {sandbox.Role.Name}", admin))
        {
            await grant.ExecuteNonQueryAsync();
        }

        // Act
        AppRoleReachException? caught = await Assert.That(
                () => DeploymentDatabaseProvisioning.VerifyAppRoleReachAsync(sandbox.AdminConnectionString, sandbox.Role))
            .Throws<AppRoleReachException>();

        // Assert
        await Assert.That(caught!.Message).Contains(sandbox.Role.Name);
    }

    [Test]
    public async Task VerifyRowLevelSecurityCoverageAsync_JudgesTheGivenRole()
    {
        // Arrange — provisioned for the given role, then one policy rebound to budgetoid_app alone: for
        // production's role that would be coverage, for the given role it is a hole.
        await using ProvisioningSandbox sandbox = await ProvisioningSandbox.CreateAsync();
        await DeploymentDatabaseProvisioning.ProvisionAsync(sandbox.AdminConnectionString, sandbox.Role);
        await using NpgsqlConnection admin = await sandbox.OpenAdminAsync();
        string policy = await ScalarAsync<string>(admin, "select policyname from pg_policies where tablename = 'budgets'");
        await using (NpgsqlCommand rebind = new($"alter policy {policy} on public.budgets to budgetoid_app", admin))
        {
            await rebind.ExecuteNonQueryAsync();
        }

        // Act
        RowLevelSecurityCoverageException? caught = await Assert.That(
                () => DeploymentDatabaseProvisioning.VerifyRowLevelSecurityCoverageAsync(sandbox.AdminConnectionString, sandbox.Role))
            .Throws<RowLevelSecurityCoverageException>();

        // Assert
        await Assert.That(caught!.Message).Contains($"does not bind {sandbox.Role.Name}");
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
