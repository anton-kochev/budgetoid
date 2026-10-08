using Infrastructure.Persistence.Provisioning;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// The reach rule for tablespaces, kept apart from <see cref="DeploymentProvisioningTests" /> because
/// its sabotage is the one that changes the whole server rather than one sandbox.
/// </summary>
/// <remarks>
/// <para>
/// A grant on <c>pg_default</c> is a grant on an object every database shares. Granted to PUBLIC it
/// reaches every role on the server, so while it stands every other sandbox's reach check reports it
/// too — measured: 168 of them, in one run, when this test ran beside the rest. Granted to the
/// sandbox's own role it is that role's alone, but it still rewrites the one ACL row of
/// <c>pg_default</c> that every other such write competes for. Both cases therefore run in an
/// exclusive sandbox, with no other sandbox alive.
/// </para>
/// <para>
/// The grant is taken back in <c>finally</c>, before the sandbox goes. The sandbox's own cleanup
/// would clear the role's grant through <c>DROP OWNED BY</c>, but nothing clears PUBLIC's, and a
/// PUBLIC grant left on a server the suite does not own would refuse every later deploy to it.
/// </para>
/// </remarks>
public sealed class TablespaceReachTests
{
    [Test]
    [Arguments("the app role")]
    [Arguments("PUBLIC")]
    public async Task VerifyAppRoleReachAsync_CreateOnATablespace_ThrowsNamingTheTablespace(string grantee)
    {
        // Arrange — CREATE on pg_default, the tablespace every table lands in. With it the role
        // could put a relation it owns there, and CREATE on a tablespace is a grant on a cluster
        // object no line of the grant script names. The PUBLIC row is the same grant reached
        // through inheritance. Neither tablespace grants anybody anything out of the box: both ACLs
        // are NULL, which is the owner alone.
        await using ProvisioningSandbox sandbox = await ProvisioningSandbox.CreateExclusiveAsync();
        string role = sandbox.Role.Name;
        string granteeSql = grantee == "PUBLIC" ? "public" : role;

        await DeploymentDatabaseProvisioning.ProvisionAsync(sandbox.AdminConnectionString, sandbox.Role);

        await using NpgsqlConnection admin = await sandbox.OpenAdminAsync();
        string probe = $"select has_tablespace_privilege('{role}', 'pg_default', 'CREATE')";
        bool heldBefore = await ScalarBoolAsync(admin, probe);
        InvalidOperationException? caught;
        bool heldAfter;
        try
        {
            await ExecuteAsync(admin, $"grant create on tablespace pg_default to {granteeSql}");
            heldAfter = await ScalarBoolAsync(admin, probe);

            // Act
            caught = await TryVerifyAppRoleReachAsync(sandbox);
        }
        finally
        {
            await ExecuteAsync(admin, $"revoke create on tablespace pg_default from {granteeSql}");
        }

        // Assert — "tablespace pg_default" is the rule's own clause; CREATE is the privilege; the
        // PUBLIC row also has to say who holds it, in the grantee's capitals.
        string[] tokens = grantee == "PUBLIC" ? ["CREATE", "PUBLIC"] : ["CREATE"];
        await Assert.That(heldBefore).IsFalse();
        await Assert.That(heldAfter).IsTrue();
        await Assert.That(caught).IsTypeOf<AppRoleReachException>();
        await Assert.That(
                (caught as AppRoleReachException)?.Problems
                    .Where(problem => problem.Contains("tablespace pg_default", StringComparison.OrdinalIgnoreCase)
                        && tokens.All(token => problem.Contains(token, StringComparison.Ordinal)))
                    .ToList() ?? [])
            .IsNotEmpty();
    }

    /// <summary>
    /// Calls the reach verifier and returns what it threw, or <see langword="null" /> if it accepted;
    /// caught as the base type so <c>IsTypeOf</c> proves the exact one.
    /// </summary>
    private static async Task<InvalidOperationException?> TryVerifyAppRoleReachAsync(ProvisioningSandbox sandbox)
    {
        try
        {
            await DeploymentDatabaseProvisioning.VerifyAppRoleReachAsync(sandbox.AdminConnectionString, sandbox.Role);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
