using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins the shape of the rows that describe a person, so that widening one has to be a decision
/// somebody makes rather than a column that arrives unnoticed. The rule is that an account row
/// carries an internal identifier, an email address and a creation timestamp, and nothing else:
/// everything an identity provider says about a person is read to answer "who is asking" and then
/// dropped. Nothing else in the suite fails when a fourth column lands on <c>users</c>, which is
/// the gap these tests close. More data-minimization schema pins belong in this file.
/// </summary>
public sealed class DataMinimizationSchemaTests
{
    [Test]
    public async Task Schema_PinsTheColumnsOfTheUserRow()
    {
        // Arrange — a real database, migrated, read on the container superuser connection so that
        // row-level security cannot decide what a catalog query is allowed to see.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        IReadOnlyList<string> columns = await ReadColumnNamesAsync(connection, "users");

        // Assert — joined rather than counted so a failure names the offending column. A count
        // would go red on a fourth column too, but it would say "3 != 4" and leave the reader to
        // find out which one arrived.
        string[] expected = ["created_at_utc", "email", "id"];
        await Assert.That(string.Join(", ", columns)).IsEqualTo(string.Join(", ", expected));
    }

    /// <summary>
    /// Reads the column names a relation carries today, straight from <c>pg_attribute</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not routed through EF's model or through any production classifier. The point
    /// is to check the schema against a pinned set, not to check the same code that would have had
    /// to notice the column in the first place — a mapping that quietly ignored a new column would
    /// otherwise let this test agree with itself. <c>RlsCoverageTests</c> keeps the same habit for
    /// the same reason.
    /// </para>
    /// <para>
    /// <c>attnum &gt; 0</c> drops the system columns, which belong to PostgreSQL and not to anyone's
    /// argument about a table; <c>not attisdropped</c> drops the tombstones a dropped column leaves
    /// behind, which are still rows in <c>pg_attribute</c> under mangled names. The latter is
    /// precisely why a <c>display_name</c> removed by a migration cannot make this test lie.
    /// </para>
    /// <para>
    /// The table name is a real parameter rather than an interpolation: it is a value in a
    /// <c>where</c> clause instead of an identifier, so nothing forces it into the statement text.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        NpgsqlConnection connection,
        string table)
    {
        const string sql =
            """
            select a.attname::text
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and c.relname = @table
              and a.attnum > 0
              and not a.attisdropped
            order by a.attname
            """;

        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("table", table);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> columns = [];

        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
