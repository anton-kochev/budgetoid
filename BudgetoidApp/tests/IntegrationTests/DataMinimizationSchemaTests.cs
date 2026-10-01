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
/// <para>
/// The schema-wide scan comes with its own provable-fail controls, and they are what make
/// <see cref="Schema_HoldsNoAnalyticsOrTrackingIdentifier" /> more than decoration. A scan that
/// stopped reaching the catalog, or a vocabulary that stopped matching anything, would both leave
/// the assertion green over an empty result — and green is exactly what it looks like when the rule
/// holds. The controls grow the forbidden thing on purpose and demand the scan name it, so the set
/// goes red when the schema is wrong and also when the check is.
/// </para>
/// <para>
/// There are two controls because there are two ways to name a forbidden idea. A behavioural
/// feature is modelled as a <i>table</i> far more often than as a column, so
/// <see cref="Schema_HoldsNoAnalyticsOrTrackingIdentifier_ReportsATableThatGrowsSuchAColumn" />
/// proves the column path can fail and
/// <see cref="Schema_HoldsNoAnalyticsOrTrackingIdentifier_ReportsATableWhoseOwnNameIsOne" /> proves
/// the relation path can. One probe offending on both axes would let either assertion pass for the
/// other one's reason, which is why the two probes are deliberately innocent on the axis they do
/// not exercise.
/// </para>
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
    public async Task Schema_PinsTheColumnsOfThePasskeyPublicKeyRow()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        IReadOnlyList<string> columns = await ReadColumnNamesAsync(connection, "passkey_public_keys");

        // Assert — a red here is an `aaguid`, a `transports` list, a `last_used_at_utc`, a
        // `backup_eligible` flag or an attestation blob, and every one of them is data nothing reads.
        // The row deliberately holds none of them: an AAGUID names the make and model of somebody's
        // authenticator, transports and a backup flag say what kind of device they carry and how they
        // sync it, a last-used timestamp is a record of when a person signed in, and attestation is a
        // certificate chain identifying the hardware. A registration ceremony is handed all of it and
        // this table keeps exactly what verifying a later assertion needs — the handle, the key and
        // the algorithm — plus the three columns that say whose it is.
        //
        // The cost of a column arriving here is higher than on most tables, and that is the second
        // reason for the pin. passkey_public_keys is exempt from row-level security, so anything
        // stored here is readable by every application session regardless of who that session names.
        string[] expected =
        [
            "cose_algorithm", "credential_id", "credential_type", "public_key_cose", "user_id",
            "webauthn_credential_id",
        ];
        await Assert.That(string.Join(", ", columns)).IsEqualTo(string.Join(", ", expected));
    }

    [Test]
    public async Task Schema_PinsTheColumnsOfThePasskeySignatureCounterRow()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        IReadOnlyList<string> columns =
            await ReadColumnNamesAsync(connection, "passkey_signature_counters");

        // Assert — the whole table is one number and the three columns saying whose it is. A red here
        // is most likely a `last_used_at_utc` or a `last_seen_ip`, which is precisely the shape a
        // sign-in history takes when it arrives one column at a time: this row is written on every
        // assertion, so it is the cheapest place in the schema to accumulate a record of when and
        // from where a person signs in. The counter is compared and overwritten, and it keeps no
        // history by design.
        string[] expected =
            ["credential_id", "credential_type", "signature_counter", "user_id"];
        await Assert.That(string.Join(", ", columns)).IsEqualTo(string.Join(", ", expected));
    }

    [Test]
    public async Task Schema_HoldsNoAnalyticsOrTrackingIdentifier()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — every row-bearing relation and every column it carries, both classified by the
        // production vocabulary.
        IReadOnlyList<string> offenders = await FindProhibitedIdentifiersAsync(admin);

        // Assert — the shipped schema stores what the product needs to answer "what did I spend" and
        // nothing that says who was asking, from where, or how often. The pinned user row above is
        // one table; this is the same rule everywhere, because a tracking column is no better on
        // transactions than it is on users, and a table whose own name says it tracks somebody is
        // the same offence spelled one level up.
        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task Schema_HoldsNoAnalyticsOrTrackingIdentifier_ReportsATableThatGrowsSuchAColumn()
    {
        // Arrange — a throwaway relation carrying exactly the shape the rule forbids. It is created
        // on the admin connection and never dropped: the container goes away with the host, which is
        // also why the name carries a prefix saying what it is if one ever leaks into a shared
        // database. RlsCoverageTests keeps the same habit for the same reason. Its own host, so the
        // relation probe cannot end up in the result this assertion reads.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await CreateProbeTableAsync(admin, ColumnProbeTable, "ip_address text not null");

        // Act — the same helper the test above runs, against the same database with one extra table.
        // Nothing about the probe is special-cased.
        IReadOnlyList<string> offenders = await FindProhibitedIdentifiersAsync(admin);

        // Assert — named as table.column rather than counted, so the failure it produces in anger
        // says which row grew the column instead of saying that one did. The table name matches
        // nothing, so the column is the only thing this can be seeing.
        await Assert.That(offenders).Contains($"{ColumnProbeTable}.ip_address");
    }

    [Test]
    public async Task Schema_HoldsNoAnalyticsOrTrackingIdentifier_ReportsATableWhoseOwnNameIsOne()
    {
        // Arrange — a throwaway relation whose columns are both ordinary and whose own name is not.
        // Its own host, for the reason its sibling has one: a database carrying both probes would
        // let each assertion pass on the other's offence.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await CreateProbeTableAsync(admin, RelationProbeTable, "user_id uuid not null");

        // Act — the same helper again, unchanged and knowing nothing about the probe.
        IReadOnlyList<string> offenders = await FindProhibitedIdentifiersAsync(admin);

        // Assert — bare, with no dot: the offence is the relation itself rather than anything it
        // carries, and reporting it as a column would name a column that does not exist.
        await Assert.That(offenders).Contains(RelationProbeTable);
    }

    /// <summary>
    /// The column half of the probe pair: a relation whose <b>own name is deliberately innocent</b>
    /// — it tokenizes as <c>data / minimization / probe / holder</c>, which matches no pattern — so
    /// that the forbidden column it carries is the only thing its control's assertion can be seeing.
    /// Renaming it to something that reads better would make both halves of the pair vacuous, since
    /// each would then have a second reason to be reported.
    /// </summary>
    /// <remarks>
    /// A constant rather than a literal at the call site: a typo split across the create and the
    /// assertion would not fail, it would simply never find the probe, and a control that cannot be
    /// found always agrees.
    /// </remarks>
    private const string ColumnProbeTable = "data_minimization_probe_holder";

    /// <summary>
    /// The relation half of the probe pair: a name carrying the forbidden token <c>analytics</c>,
    /// over <b>deliberately innocent columns</b> — <c>id</c> and <c>user_id</c>, both of which the
    /// vocabulary is known to allow — so that the relation name is the only thing its control's
    /// assertion can be seeing. Giving it a column with anything to hide would let the assertion
    /// pass with the relation path removed entirely.
    /// </summary>
    /// <remarks>
    /// A constant for the reason <see cref="ColumnProbeTable" /> is one, and the two are created on
    /// separate hosts so neither ever appears in the other's scan.
    /// </remarks>
    private const string RelationProbeTable = "data_minimization_probe_user_analytics";

    /// <summary>
    /// Reports every relation and every column in <c>public</c> that the production vocabulary
    /// classifies as an analytics identifier, an advertising identifier, a device fingerprint or a
    /// behavioural event — a relation as a bare name, a column as <c>table.column</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method taking a connection rather than an inlined query, because the assertion and its
    /// control have to run the same scan. A control that exercised a second, separately written
    /// query would prove that query can fail and say nothing about the one that ships.
    /// </para>
    /// <para>
    /// The schema and the relation kinds are not spelled out here: both come from
    /// <see cref="RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate" />, so this scan
    /// discovers over exactly what the coverage verifier discovers over. Sharing rather than copying
    /// is the whole point of that constant — the relation-kind half has already been widened once,
    /// from <c>'r'</c> alone, and the copies had to be found by hand. A copy that misses a widening
    /// reports green over exactly the relation kinds the widening was for: here, the view exposing a
    /// tracking column that <c>'r'</c> alone would leave entirely unexamined.
    /// </para>
    /// <para>
    /// A relation name is classified by the same call as a column name, because it is the same
    /// question: no pattern here is legitimate in one position and forbidden in the other, and a
    /// behavioural feature reaches a schema as a table at least as often as it reaches one as a
    /// column. Relation offenders are de-duplicated because <c>pg_attribute</c> yields one row per
    /// column, so an offending table would otherwise be reported once for every column it carries.
    /// </para>
    /// <para>
    /// <c>attnum &gt; 0</c> drops the system columns, which belong to PostgreSQL rather than to
    /// anyone's argument about a table; <c>not attisdropped</c> drops the tombstones a dropped column
    /// leaves behind under a mangled name.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<string>> FindProhibitedIdentifiersAsync(
        NpgsqlConnection connection)
    {
        const string sql =
            $"""
            select c.relname::text, a.attname::text
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where {RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate}
              and a.attnum > 0
              and not a.attisdropped
            order by c.relname, a.attname
            """;

        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> offenders = [];
        HashSet<string> reportedRelations = new(StringComparer.Ordinal);

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);
            string column = reader.GetString(1);

            if (ProhibitedColumnVocabulary.Classify(table) is not null
                && reportedRelations.Add(table))
            {
                offenders.Add(table);
            }

            if (ProhibitedColumnVocabulary.Classify(column) is not null)
            {
                offenders.Add($"{table}.{column}");
            }
        }

        return offenders;
    }

    /// <summary>
    /// Creates a throwaway relation the provable-fail controls scan: a clean <c>id</c> key plus the
    /// one column the caller names.
    /// </summary>
    /// <remarks>
    /// One helper rather than two, because both probes want the same statement and differ only in
    /// which of the two arguments carries the offence — the column probe passes a forbidden column
    /// under an innocent name, the relation probe an innocent column under a forbidden name.
    /// Interpolated rather than parameterised because an identifier cannot be a parameter, and every
    /// argument is a literal written in this file. No policy, no grant and no foreign key: every one
    /// of those would be a claim about what the table is for, and the point is that the verdict comes
    /// from the names alone.
    /// </remarks>
    private static Task CreateProbeTableAsync(
        NpgsqlConnection connection,
        string name,
        string column) =>
        ExecuteAsync(connection, $"create table {name} (id uuid primary key, {column})");

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
