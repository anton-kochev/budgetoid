using Infrastructure.Persistence.Provisioning;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Pins that the live schema offers nowhere for a row to survive the erasure that was supposed to
/// remove it. Erasure is a hard delete everywhere in this product, and the shape that quietly undoes
/// that is not a wrong handler — it is a column or a table whose name says the row is still here:
/// a <c>deleted_at</c>, a tombstone, a <c>deletions</c> log, an anonymized remnant, a
/// <c>users_archive</c>. None of them fail any behavioural test, because each one is written by code
/// that works.
/// </summary>
/// <remarks>
/// <para>
/// A sibling file of <c>DataMinimizationSchemaTests</c> rather than three more tests inside it, and
/// deliberately so. The two scans share a shape and share nothing else: that file refuses what a
/// schema keeps <i>about a person</i> and this one refuses what a schema keeps <i>after</i> a person
/// is gone. The remedies point in opposite directions — an analytics column is deleted because the
/// product measures nothing, while a <c>deleted_at</c> is deleted because the row should have been —
/// and the owning documents differ too. Filing a tombstone red under a heading named for data
/// minimization would hand the reviewer the wrong argument to have.
/// </para>
/// <para>
/// The scan comes with its own provable-fail controls, which are what make
/// <see cref="Schema_HoldsNoSoftDeleteFlagTombstoneOrArchivedCopy" /> more than decoration. A scan
/// that stopped reaching the catalog, or a vocabulary that stopped matching anything, would both
/// leave the assertion green over an empty result — and green is exactly what it looks like when the
/// rule holds.
/// </para>
/// <para>
/// There are two controls because there are two ways to name a remnant, and this vocabulary arrives
/// at table grain more often than the sibling's does — <c>deletions</c> and <c>users_archive</c> are
/// tables, not columns. So
/// <see cref="Schema_HoldsNoSoftDeleteFlagTombstoneOrArchivedCopy_ReportsATableThatGrowsSuchAColumn" />
/// proves the column path can fail and
/// <see cref="Schema_HoldsNoSoftDeleteFlagTombstoneOrArchivedCopy_ReportsATableWhoseOwnNameIsOne" />
/// proves the relation path can. One probe offending on both axes would let either assertion pass
/// for the other one's reason, which is why the two probes are deliberately innocent on the axis they
/// do not exercise. The relation probe goes further and carries <b>no columns at all</b>, which is
/// what holds the scan to discovering its relations in a pass of their own: a relation set derived
/// from column rows cannot see a relation that has none.
/// </para>
/// <para>
/// <see cref="Schema_CarriesNoNonSystemSchemaBesidePublic" /> is the odd one out here and belongs here
/// anyway. Every scan in this repository — this one, the sibling's, row-level security coverage, the
/// grant matrix, the column pins, the atomicity count — is confined to <c>public</c>, so a table in a
/// second schema is invisible to all of them at once. One assertion refusing the second schema closes
/// that for every one of them, which is cheaper and stricter than widening five queries and
/// remembering the sixth.
/// </para>
/// </remarks>
public sealed class ErasureRemnantSchemaTests
{
    /// <summary>
    /// The column half of the probe pair: a relation whose <b>own name is deliberately innocent</b>.
    /// It tokenizes as <c>erasure / remnant / probe / holder</c>, and none of those is a pattern —
    /// bare <c>erasure</c> is legal on purpose, because it is the word this product uses for the
    /// endpoint, and the <c>erasure_log</c> rule needs an adjacent <c>log</c> token that this name
    /// does not carry. So the forbidden column it grows is the only thing its control's assertion can
    /// be seeing.
    /// </summary>
    /// <remarks>
    /// A constant rather than a literal at the call site: a typo split across the create and the
    /// assertion would not fail, it would simply never find the probe, and a control that cannot be
    /// found always agrees.
    /// </remarks>
    private const string ColumnProbeTable = "erasure_remnant_probe_holder";

    /// <summary>
    /// The relation half of the probe pair: a name carrying the forbidden noun <c>archive</c> — it
    /// tokenizes as <c>erasure / remnant / probe / users / archive</c> — over <b>no columns at
    /// all</b>. A relation with nothing on it cannot be reported for anything it carries, so the
    /// relation name is the only thing its control's assertion can be seeing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Column-less rather than innocently-columned, and that is the whole strength of this
    /// control.</b> The scan used to read <c>pg_attribute</c> alone and derive its relation set from
    /// the rows that came back, so a relation reached the vocabulary only if it carried at least one
    /// column — and a zero-column relation contributed no rows and was therefore never classified.
    /// That is a hole in exactly the axis the vocabulary calls the dominant one, since a remnant
    /// arrives as <c>deletions</c> or <c>users_archive</c> more often than as a <c>deleted_at</c>. A
    /// probe carrying an innocent <c>user_id</c> could not see the hole: it was reported through the
    /// column rows it happened to produce, which is the relation path passing for the column path's
    /// reason. Emptying it is what forces the relations to be discovered in a pass of their own.
    /// </para>
    /// <para>
    /// A constant for the reason <see cref="ColumnProbeTable" /> is one, and the two are created on
    /// separate hosts so neither ever appears in the other's scan.
    /// </para>
    /// </remarks>
    private const string RelationProbeTable = "erasure_remnant_probe_users_archive";

    /// <summary>The one schema every catalog scan in this repository is confined to.</summary>
    private const string PublicSchema = "public";

    /// <summary>
    /// The information schema, which PostgreSQL creates in every database and nobody in this
    /// repository writes to.
    /// </summary>
    private const string InformationSchema = "information_schema";

    /// <summary>
    /// The prefix PostgreSQL reserves for its own schemas, and the whole of the rest of the
    /// exclusion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read off a live <c>postgres:17</c> — the image this suite runs — rather than assumed. A
    /// freshly created database carries exactly four schemas: <c>information_schema</c>,
    /// <c>pg_catalog</c>, <c>pg_toast</c> and <c>public</c>. The only two a session can add without
    /// anybody deciding to are <c>pg_temp_N</c> and <c>pg_toast_temp_N</c>, which appear the moment a
    /// temporary table is created and go away with the session. All four of those reserved names
    /// carry this prefix, so the prefix is the rule rather than a list of names somebody would have
    /// to keep in step with PostgreSQL.
    /// </para>
    /// <para>
    /// A prefix rule can only be excluded safely because PostgreSQL reserves it: <c>create schema
    /// pg_evil</c> is refused outright — "unacceptable schema name … the prefix pg_ is reserved for
    /// system schemas" — so nothing anybody adds can hide behind the exclusion.
    /// </para>
    /// </remarks>
    private const string SystemSchemaPrefix = "pg_";

    [Test]
    public async Task Schema_HoldsNoSoftDeleteFlagTombstoneOrArchivedCopy()
    {
        // Arrange — a real database, migrated, read on the container superuser connection so that
        // row-level security cannot decide what a catalog query is allowed to see.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — every row-bearing relation and every column it carries, both classified by the
        // shared vocabulary.
        ErasureRemnantScan scan = await FindErasureRemnantIdentifiersAsync(admin);

        // Assert — the non-vacuity guards first, per the house rule RlsCoverageTests states: a catalog
        // query that silently stopped matching anything would leave the real assertion below passing
        // over nothing, and an empty result is indistinguishable from a clean schema. The guards read
        // what the same scan examined rather than a second query of their own, so they cannot go on
        // agreeing after the queries they vouch for have changed.
        //
        // Both axes are guarded separately, because the scan now runs two queries and one of them
        // failing is invisible to a guard over their union. The relation pass is the one that has to
        // be vouched for by name: it exists precisely because a zero-column relation produces no
        // column rows at all, so a relation query that stopped matching would leave a combined guard
        // satisfied by the columns alone — and the axis it dropped is the one the vocabulary calls
        // the more likely.
        //
        // Then the rule itself: erasure in this product is a hard delete, and the schema offers
        // nowhere for a row to sit out one. A red here is a soft-delete flag, a tombstone, a table
        // kept about deletions, an anonymized remnant or a copy taken aside.
        //
        // The report is the reason each offender was refused, one per line, so the red hands the
        // reviewer the argument to weigh their column against rather than a bucket name they would
        // have to go and look up.
        await Assert.That(scan.ExaminedRelations).IsNotEmpty();
        await Assert.That(scan.ExaminedColumns).IsNotEmpty();
        await Assert.That(scan.Offenders)
            .IsEmpty()
            .Because("the live schema and the remnant vocabulary intersect."
                     + Environment.NewLine
                     + scan.Report
                     + Environment.NewLine
                     + "Exactly one of two things is true and this test cannot say which. Either a "
                     + "pattern is too wide and has swallowed an ordinary name, and the pattern "
                     + "narrows — or the table or column is a genuine remnant, in which case the "
                     + "vocabulary is right and it is the SCHEMA that has to change: the name goes, "
                     + "and with it whatever kept the row alive past its own erasure. The remedy for "
                     + "a real remnant is never a filter and never a narrower pattern, it is to "
                     + "delete the row for real.");
    }

    [Test]
    public async Task Schema_HoldsNoSoftDeleteFlagTombstoneOrArchivedCopy_ReportsATableThatGrowsSuchAColumn()
    {
        // Arrange — a throwaway relation carrying exactly the shape the rule forbids. It is created
        // on the admin connection and never dropped: the database goes away with the host. Its own
        // host, so the relation probe cannot end up in the result this assertion reads.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await CreateProbeTableAsync(admin, ColumnProbeTable, "deleted_at timestamptz");

        // Act — the same helper the real assertion runs, against the same database with one extra
        // table. Nothing about the probe is special-cased.
        ErasureRemnantScan scan = await FindErasureRemnantIdentifiersAsync(admin);

        // Assert — named as table.column rather than counted, so the failure it produces in anger
        // says which row grew the flag instead of saying that one did. The table name matches
        // nothing, so the column is the only thing this can be seeing.
        await Assert.That(scan.Offenders).Contains($"{ColumnProbeTable}.deleted_at");
    }

    [Test]
    public async Task Schema_HoldsNoSoftDeleteFlagTombstoneOrArchivedCopy_ReportsATableWhoseOwnNameIsOne()
    {
        // Arrange — a throwaway relation with no columns on it at all and a forbidden name. Empty
        // rather than innocently columned, because an innocent column is still a pg_attribute row and
        // that row is what a column-derived relation set is built from: the probe would be reported
        // for the column pass's reason while the relation pass could be deleted outright. Its own
        // host, for the reason its sibling has one: a database carrying both probes would let each
        // assertion pass on the other's offence.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await CreateColumnlessProbeRelationAsync(admin, RelationProbeTable);

        // Act — the same helper again, unchanged and knowing nothing about the probe.
        ErasureRemnantScan scan = await FindErasureRemnantIdentifiersAsync(admin);

        // Assert — bare, with no dot: the offence is the relation itself rather than anything it
        // carries, and reporting it as a column would name a column that does not exist. The probe
        // has no columns at all, so this can only be green while relations are discovered in a pass
        // of their own.
        await Assert.That(scan.Offenders).Contains(RelationProbeTable);
    }

    [Test]
    public async Task Schema_CarriesNoNonSystemSchemaBesidePublic()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — every schema the database carries, read unfiltered, and the exclusion applied here
        // rather than in the query. A `where` clause would leave the non-vacuity guard with nothing
        // to be about: "the filtered query returned nothing" and "the query reached pg_namespace at
        // all" would be the same observation, which is the shape of green a broken scan produces.
        IReadOnlyList<string> schemas = await ReadSchemaNamesAsync(admin);
        string[] beyondPublic = schemas
            .Where(schema => !IsSystemSchema(schema))
            .Where(schema => !string.Equals(schema, PublicSchema, StringComparison.Ordinal))
            .ToArray();

        // Assert — the guard first, naming public rather than merely demanding a non-empty list: a
        // query that came back with the system schemas and nothing else would satisfy "non-empty"
        // while proving the read cannot see an ordinary schema at all.
        //
        // Then the rule, and the consequence it carries is not about this file. Every catalog scan in
        // this repository is confined to `public` — this one, DataMinimizationSchemaTests, the
        // row-level security coverage discovery, the grant matrix, the pinned column sets, the
        // erasure atomicity count — so `create schema archive; create table archive.users (...)` is
        // invisible to all of them at once, and every one of them reports green over it. Closing that
        // by widening each query would mean widening five and remembering the sixth; refusing the
        // second schema closes it for every scan that exists and for every scan written afterwards.
        await Assert.That(schemas).Contains(PublicSchema);
        await Assert.That(beyondPublic)
            .IsEmpty()
            .Because("the database carries a schema beside public: "
                     + string.Join(", ", beyondPublic)
                     + ". Every catalog scan in this suite is confined to public, so a relation in "
                     + "another schema is examined by NONE of them — not the remnant scan, not the "
                     + "data-minimization scan, not row-level security coverage, not the grant "
                     + "matrix, not the pinned column sets. It is unpoliced, unpinned and reported "
                     + "as clean by every check that exists. Either the schema goes, or every one of "
                     + "those scans has to be widened together and this assertion rewritten to say "
                     + "which schemas they now cover");
    }

    /// <summary>
    /// Reports every relation and every column in <c>public</c> that the shared vocabulary classifies
    /// as a soft-delete flag, a tombstone, a deletion record, an anonymized remnant or an archived
    /// copy — a relation as a bare name, a column as <c>table.column</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method taking a connection rather than an inlined query, because the assertion and its
    /// controls have to run the same scan. A control that exercised a second, separately written
    /// query would prove that query can fail and say nothing about the one that ships.
    /// </para>
    /// <para>
    /// <b>Two passes, because the two axes are two questions and only one of them is about a
    /// column.</b> The relations come from <c>pg_class</c> and the columns from <c>pg_attribute</c>,
    /// each read on its own. Deriving the relation set from the column rows — which is what a single
    /// <c>pg_attribute</c> query does — means a relation is classified only if it carries at least
    /// one column, so a <b>zero-column relation is never classified at all</b>. That is not a corner
    /// case invented for a test — <c>create table users_archive ()</c> is a statement PostgreSQL
    /// accepts — and the axis it hides in is the one this vocabulary calls the dominant one:
    /// <c>deletions</c> and <c>users_archive</c> are tables, not columns. The de-duplication the old
    /// shape needed disappears with the two passes, because <c>pg_class</c> yields one row per
    /// relation rather than one per column.
    /// </para>
    /// <para>
    /// Neither pass spells out the schema or the relation kinds: both take them from
    /// <see cref="RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate" />, so both discover
    /// over exactly what the coverage verifier discovers over. Sharing rather than copying is the
    /// whole point of that constant — the relation-kind half has already been widened once, from
    /// <c>'r'</c> alone, and the copies had to be found by hand. A copy that misses a widening
    /// reports green over exactly the relation kinds the widening was for: here, the view named
    /// <c>users_archive</c> over rows nobody deleted, which is exactly the shape the rule is about
    /// and which <c>'r'</c> alone would leave unexamined. What keeps the <c>public</c> confinement
    /// from being a blind spot in turn is <see cref="Schema_CarriesNoNonSystemSchemaBesidePublic" />,
    /// which refuses the second schema outright rather than asking every scan in the suite to widen.
    /// </para>
    /// <para>
    /// A relation name is classified by the same call as a column name, because it is the same
    /// question, and here the relation half carries more weight than it does on the sibling scan:
    /// a remnant reaches a schema as <c>deletions</c> or <c>users_archive</c> at least as often as it
    /// reaches one as a <c>deleted_at</c>.
    /// </para>
    /// <para>
    /// <c>attnum &gt; 0</c> drops the system columns, which belong to PostgreSQL rather than to
    /// anyone's argument about a table; <c>not attisdropped</c> drops the tombstones a dropped column
    /// leaves behind under a mangled name — and those are the one kind of tombstone this file does
    /// not mean, since they are PostgreSQL's own bookkeeping and hold no row.
    /// </para>
    /// </remarks>
    private static async Task<ErasureRemnantScan> FindErasureRemnantIdentifiersAsync(
        NpgsqlConnection connection)
    {
        IReadOnlyList<string> relations = await ReadRelationNamesAsync(connection);
        IReadOnlyList<(string Table, string Column)> columns = await ReadColumnsAsync(connection);
        List<string> offenders = [];
        List<string> report = [];

        // Relations first, so an offending table is named before the columns it carries — a reader
        // meeting both wants the table it must not build ahead of a column it must not add to it.
        foreach (string relation in relations)
        {
            if (ErasureRemnantVocabulary.Classify(relation) is { } rule)
            {
                offenders.Add(relation);
                report.Add(OffenceLine(relation, rule));
            }
        }

        // The pair is carried rather than a pre-joined 'table.column' string, because only the column
        // half is classified and splitting a joined name back apart would be guessing at where the
        // dot was: a quoted PostgreSQL identifier may contain one.
        foreach ((string table, string column) in columns)
        {
            if (ErasureRemnantVocabulary.Classify(column) is { } rule)
            {
                offenders.Add($"{table}.{column}");
                report.Add(OffenceLine($"{table}.{column}", rule));
            }
        }

        return new ErasureRemnantScan(
            relations,
            columns.Select(entry => $"{entry.Table}.{entry.Column}").ToArray(),
            offenders,
            string.Join(Environment.NewLine, report));
    }

    /// <summary>
    /// Every row-bearing relation in <c>public</c>, read from <c>pg_class</c> in a pass of its own.
    /// </summary>
    /// <remarks>
    /// The pass that closes the zero-column hole. A relation has a <c>pg_class</c> row whether or not
    /// it carries a single column, which is exactly what the <c>pg_attribute</c> pass cannot say.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ReadRelationNamesAsync(
        NpgsqlConnection connection)
    {
        const string sql =
            $"""
            select c.relname::text
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where {RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate}
            order by c.relname
            """;

        return await ReadRowsAsync(connection, sql, reader => reader.GetString(0));
    }

    /// <summary>
    /// Every column on every row-bearing relation in <c>public</c>, paired with the relation it sits
    /// on.
    /// </summary>
    /// <remarks>
    /// The relation name comes back only so an offending column can be reported under it. Nothing on
    /// this pass classifies it — that is what <see cref="ReadRelationNamesAsync" /> is for, and it is
    /// the whole reason there are two passes.
    /// </remarks>
    private static async Task<IReadOnlyList<(string Table, string Column)>> ReadColumnsAsync(
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

        return await ReadRowsAsync(
            connection, sql, reader => (reader.GetString(0), reader.GetString(1)));
    }

    /// <summary>
    /// Runs one catalog query and projects each row.
    /// </summary>
    /// <remarks>
    /// Shared by all three queries here so the reader plumbing cannot drift between them. The
    /// projection is the only difference, which is what keeps "two passes" a claim about the catalog
    /// rather than about two hand-written readers that might not agree on how a row is read.
    /// </remarks>
    private static async Task<IReadOnlyList<T>> ReadRowsAsync<T>(
        NpgsqlConnection connection,
        string sql,
        Func<NpgsqlDataReader, T> project)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<T> rows = [];

        while (await reader.ReadAsync())
        {
            rows.Add(project(reader));
        }

        return rows;
    }

    /// <summary>
    /// Every schema the database carries, unfiltered.
    /// </summary>
    /// <remarks>
    /// No <c>where</c> clause at all, deliberately. The whole point of the assertion this feeds is
    /// that something turned up outside the set anybody expected, and a query that already knows
    /// which schemas are acceptable would hide the one it had not been told about — which is the
    /// discovery-filter mistake <c>RowLevelSecurityCoverage</c> spends four paragraphs on, one object
    /// grain up.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ReadSchemaNamesAsync(
        NpgsqlConnection connection) =>
        await ReadRowsAsync(
            connection,
            """
            select n.nspname::text
            from pg_namespace n
            order by n.nspname
            """,
            reader => reader.GetString(0));

    /// <summary>
    /// Whether a schema is one PostgreSQL creates and owns rather than one somebody in this
    /// repository could have made.
    /// </summary>
    /// <remarks>
    /// Ordinal comparison and an ordinal prefix test: schema names come out of the catalog as
    /// PostgreSQL stores them, and a culture-sensitive comparison would make the verdict depend on
    /// the machine the build ran on.
    /// </remarks>
    private static bool IsSystemSchema(string schema) =>
        string.Equals(schema, InformationSchema, StringComparison.Ordinal)
        || schema.StartsWith(SystemSchemaPrefix, StringComparison.Ordinal);

    /// <summary>
    /// One offender, written as the sentence a reviewer meeting the red has to argue with: the name,
    /// the remnant it is an instance of, and the vocabulary's own reason for refusing the pattern it
    /// tripped.
    /// </summary>
    /// <remarks>
    /// The reason rather than the category alone, because the category is a bucket name and the
    /// argument for the bucket is what the reviewer needs — otherwise it waits in a file they have to
    /// know to open. One offender per line, because the reasons are long prose and several to a line
    /// would be unreadable at exactly the moment the message is being read in anger.
    /// </remarks>
    private static string OffenceLine(string name, ErasureRemnantRule rule) =>
        $"{name} is a {rule.Category}: the pattern '{rule.Pattern}' {rule.Reason}.";

    /// <summary>
    /// One run of the scan: everything it looked at, the subset it refused, and why each was refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four travel together so the non-vacuity guards, the assertion they vouch for and the
    /// message that assertion fails with all read the same execution. A guard that ran a query of its
    /// own would answer a question about that query, and the two could drift apart in exactly the
    /// direction that leaves an empty offender list looking like a clean schema.
    /// </para>
    /// <para>
    /// <b>The two examined lists are separate members rather than one.</b> The scan runs two queries
    /// and either can stop matching on its own, so a single combined list would let the columns vouch
    /// for a relation pass that returned nothing — and the relation pass is the one that exists
    /// because a zero-column relation is invisible to the other.
    /// </para>
    /// <para>
    /// The names and the report are separate members rather than one list of sentences, because the
    /// two probes assert on a name and a reason is prose that will be reworded. A control pinned to
    /// the wording would go red on an edit that changed no behaviour, which is the shape of red that
    /// teaches an author to delete the control.
    /// </para>
    /// </remarks>
    /// <param name="ExaminedRelations">
    /// Every relation the <c>pg_class</c> pass reached, in catalog order.
    /// </param>
    /// <param name="ExaminedColumns">
    /// Every <c>table.column</c> the <c>pg_attribute</c> pass reached, in catalog order.
    /// </param>
    /// <param name="Offenders">
    /// The names the vocabulary refused — a relation as a bare name, a column as
    /// <c>table.column</c>.
    /// </param>
    /// <param name="Report">
    /// The same offenders, one per line, each with the rule it tripped and that rule's argument for
    /// refusing it. Empty when nothing was refused.
    /// </param>
    private readonly record struct ErasureRemnantScan(
        IReadOnlyList<string> ExaminedRelations,
        IReadOnlyList<string> ExaminedColumns,
        IReadOnlyList<string> Offenders,
        string Report);

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
    /// Creates a throwaway relation carrying <b>no columns at all</b>, for the control that proves the
    /// relation path can fail.
    /// </summary>
    private static Task CreateColumnlessProbeRelationAsync(
        NpgsqlConnection connection,
        string name) =>
        ExecuteAsync(connection, $"create table {name} ()");

    /// <summary>
    /// Runs one DDL statement on the admin connection.
    /// </summary>
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
