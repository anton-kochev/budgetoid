using Infrastructure.Persistence.Provisioning;
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
/// <remarks>
/// The schema-wide scan comes in a pair, and
/// <see cref="Schema_HoldsNoAnalyticsOrTrackingColumn_ReportsATableThatGrowsOne" /> is what makes
/// <see cref="Schema_HoldsNoAnalyticsOrTrackingColumn" /> more than decoration. A scan that stopped
/// reaching the catalog, or a vocabulary that stopped matching anything, would both leave the
/// assertion green over an empty result — and green is exactly what it looks like when the rule
/// holds. The control grows the forbidden column on purpose and demands the scan name it, so the
/// pair goes red when the schema is wrong and also when the check is.
/// </remarks>
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

    [Test]
    public async Task Schema_HoldsNoAnalyticsOrTrackingColumn()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — every column of every row-bearing relation, classified by the production vocabulary.
        IReadOnlyList<string> offenders = await FindProhibitedColumnsAsync(admin);

        // Assert — the shipped schema stores what the product needs to answer "what did I spend" and
        // nothing that says who was asking, from where, or how often. The pinned user row above is
        // one table; this is the same rule everywhere, because a tracking column is no better on
        // transactions than it is on users.
        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task Schema_HoldsNoAnalyticsOrTrackingColumn_ReportsATableThatGrowsOne()
    {
        // Arrange — a throwaway relation carrying exactly the shape the rule forbids. It is created
        // on the admin connection and never dropped: the container goes away with the host, which is
        // also why the name carries a prefix saying what it is if one ever leaks into a shared
        // database. RlsCoverageTests keeps the same habit for the same reason.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await CreateProbeTableAsync(admin, TrackingProbeTable, "ip_address text not null");

        // Act — the same helper the test above runs, against the same database with one extra table.
        // Nothing about the probe is special-cased.
        IReadOnlyList<string> offenders = await FindProhibitedColumnsAsync(admin);

        // Assert — named as table.column rather than counted, so the failure it produces in anger
        // says which row grew the column instead of saying that one did.
        await Assert.That(offenders).Contains($"{TrackingProbeTable}.ip_address");
    }

    /// <summary>
    /// The throwaway relation the provable-fail control creates. A constant rather than a literal at
    /// the call site: a typo split across the create and the assertion would not fail, it would
    /// simply never find the probe, and a control that cannot be found always agrees.
    /// </summary>
    private const string TrackingProbeTable = "data_minimization_probe_tracking";

    /// <summary>
    /// Reports every column in <c>public</c> that the production vocabulary classifies as an
    /// analytics identifier, an advertising identifier, a device fingerprint or a behavioural event,
    /// as <c>table.column</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method taking a connection rather than an inlined query, because the assertion and its
    /// control have to run the same scan. A control that exercised a second, separately written
    /// query would prove that query can fail and say nothing about the one that ships.
    /// </para>
    /// <para>
    /// The relation kinds are the widened set <c>RowLevelSecurityCoverage</c> discovers over —
    /// ordinary table, partitioned parent, view, materialized view, foreign table — and for the same
    /// reason: each one of them is a place a column can hide. Narrowing to <c>'r'</c> would leave a
    /// view exposing a tracking column entirely unexamined.
    /// </para>
    /// <para>
    /// <c>attnum &gt; 0</c> drops the system columns, which belong to PostgreSQL rather than to
    /// anyone's argument about a table; <c>not attisdropped</c> drops the tombstones a dropped column
    /// leaves behind under a mangled name.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<string>> FindProhibitedColumnsAsync(
        NpgsqlConnection connection)
    {
        const string sql =
            """
            select c.relname::text, a.attname::text
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and c.relkind = any (array['r', 'p', 'v', 'm', 'f'])
              and a.attnum > 0
              and not a.attisdropped
            order by c.relname, a.attname
            """;

        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> offenders = [];

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);
            string column = reader.GetString(1);

            if (ProhibitedColumnVocabulary.Classify(column) is not null)
            {
                offenders.Add($"{table}.{column}");
            }
        }

        return offenders;
    }

    /// <summary>
    /// Creates the throwaway relation the provable-fail control scans.
    /// </summary>
    /// <remarks>
    /// Interpolated rather than parameterised because an identifier cannot be a parameter, and both
    /// arguments are literals written in this file. No policy, no grant and no foreign key: every one
    /// of those would be a claim about what the table is for, and the point is that the verdict comes
    /// from the column name alone.
    /// </remarks>
    private static Task CreateProbeTableAsync(
        NpgsqlConnection connection,
        string name,
        string trackingColumn) =>
        ExecuteAsync(connection, $"create table {name} (id uuid primary key, {trackingColumn})");

    /// <summary>
    /// Runs one DDL statement on the admin connection.
    /// </summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
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
