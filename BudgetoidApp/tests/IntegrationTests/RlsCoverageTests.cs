using Infrastructure.Persistence.Provisioning;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the one thing no isolation test can: that <b>every</b> budget-owned table is policed, not
/// just the five that existed when the policies were written. The grant matrix and row-level
/// security fail in opposite directions, and that asymmetry is the whole reason this file exists. A
/// new table nobody grants is simply invisible to the application role — fail-closed, and the first
/// feature that touches it fails loudly with <c>42501</c>. A new table nobody writes a policy for is
/// fully readable and writable by the role across every tenant — fail-open, silent, and
/// indistinguishable from working. So a budget-owned table needs both, and only a test that derives
/// its subject from the live schema can notice the second was forgotten.
/// </summary>
/// <remarks>
/// <para>
/// The table list is therefore <b>discovered</b>, not written down: every table in <c>public</c>
/// carrying a <c>budget_id</c> column. Hardcoding the five known names would defeat the entire
/// point — the test would keep passing on the day someone adds the sixth, which is the only day it
/// matters.
/// </para>
/// <para>
/// Both tests run on the superuser connection. That is not a privilege question: <c>pg_class</c> and
/// <c>pg_policies</c> describe the schema, and the schema is the same whoever reads it.
/// </para>
/// <para>
/// A discovery query that returned nothing would make both assertions below pass vacuously, so each
/// test asserts the list is non-empty before it asserts anything about the list's contents.
/// </para>
/// </remarks>
public sealed class RlsCoverageTests
{
    [Test]
    public async Task Database_EnablesRowLevelSecurityOnEveryBudgetOwnedTable()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — relrowsecurity is read alongside the discovery, because "is this table budget-owned"
        // and "is it protected" are one row of pg_class and splitting them would only invite the two
        // lists to drift.
        IReadOnlyList<(string Table, bool RowSecurityEnabled)> tables =
            await DiscoverBudgetOwnedTablesAsync(admin);
        List<string> unprotected = tables
            .Where(entry => !entry.RowSecurityEnabled)
            .Select(entry => entry.Table)
            .ToList();

        // Assert — the non-empty check first: a discovery query that silently stopped matching
        // anything would make the real assertion below pass with nothing in it.
        await Assert.That(tables).IsNotEmpty();
        await Assert.That(unprotected).IsEmpty();
    }

    [Test]
    public async Task Database_GivesEveryBudgetOwnedTableExactlyOneIsolationPolicy()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — exactly one, not at least one. These isolation policies are permissive, and
        // permissive policies OR together, so a second permissive one can only widen what the first
        // allows; anything meant to narrow has to be written AS RESTRICTIVE. A table that grew a
        // stray policy has quietly stopped meaning what the first policy says.
        IReadOnlyList<(string Table, bool RowSecurityEnabled)> tables =
            await DiscoverBudgetOwnedTablesAsync(admin);
        List<string> wrongly = [];
        foreach ((string table, _) in tables)
        {
            IReadOnlyList<(string Name, string[] Roles)> policies =
                await ReadPoliciesAsync(admin, table);

            if (policies is not [(string name, string[] roles)])
            {
                wrongly.Add($"{table}: {policies.Count} policies, wanted 1");
                continue;
            }

            // The role check is deliberately "binds the application role" rather than "names
            // budgetoid_app and nothing else". A policy written TO PUBLIC also binds the app role —
            // it binds every non-owner role — so it is broader, not weaker, and failing it would be
            // the assertion being brittle about spelling rather than about protection. Anything
            // else means the app role is unpoliced, which is the whole failure this test is for.
            if (!roles.Contains(DatabaseProvisioning.AppRoleName) && !roles.Contains("public"))
            {
                wrongly.Add(
                    $"{table}: policy '{name}' binds [{string.Join(", ", roles)}], " +
                    $"which does not include {DatabaseProvisioning.AppRoleName}");
            }
        }

        // Assert
        await Assert.That(tables).IsNotEmpty();
        await Assert.That(wrongly).IsEmpty();
    }

    /// <summary>
    /// Returns every ordinary table in <c>public</c> that carries a <c>budget_id</c> column, with
    /// whether row-level security is switched on for it.
    /// </summary>
    /// <remarks>
    /// The <c>budget_id</c> column <i>is</i> the definition of budget-owned, which is why it and not
    /// a name list is the filter. <c>budgets</c>, <c>users</c>, <c>currencies</c> and
    /// <c>__EFMigrationsHistory</c> fall out of scope for free: a budget is the tenant rather than a
    /// tenant's row, and none of the other three belongs to one. <c>relkind = 'r'</c> keeps views
    /// and sequences out; a view has no row-level security of its own.
    /// </remarks>
    private static async Task<IReadOnlyList<(string Table, bool RowSecurityEnabled)>>
        DiscoverBudgetOwnedTablesAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            select c.relname, c.relrowsecurity
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and c.relkind = 'r'
              and exists (
                  select 1
                  from pg_attribute a
                  where a.attrelid = c.oid
                    and a.attname = 'budget_id'
                    and a.attnum > 0
                    and not a.attisdropped)
            order by c.relname
            """,
            connection);

        List<(string, bool)> tables = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add((reader.GetString(0), reader.GetBoolean(1)));
        }

        return tables;
    }

    /// <summary>
    /// Returns the policies defined on one table, with the roles each binds. <c>roles</c> is cast to
    /// <c>text[]</c> because <c>pg_policies</c> exposes it as <c>name[]</c>, which is a catalog type
    /// rather than a thing a client reads back as strings.
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
}
