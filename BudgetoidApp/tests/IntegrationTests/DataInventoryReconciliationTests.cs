using Infrastructure.Persistence.Inventory;
using Infrastructure.Persistence.Provisioning;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Reconciles the columns the EF model maps against the columns the live catalog holds, in both
/// directions, and pins the one written-down list that is allowed to stand between them.
/// </summary>
/// <remarks>
/// <para>
/// FR-005 wants every schema column classified as <i>narrative</i>, <i>arithmetic</i> or
/// <i>excluded</i>, with the build red on a column nobody classified. That requirement has a premise
/// nobody has ever measured: that the EF model and the shipped schema describe the <b>same</b> set
/// of columns. If they do not, a classification built over the model is a classification of
/// something other than the schema — it would report itself complete while leaving real columns
/// unclassified, which is the exact failure FR-005 exists to make impossible.
/// </para>
/// <para>
/// So this file is the premise, checked, before anything is classified. The model side comes from
/// <see cref="MappedSchema" />, which is the walk two unit-test files already do with the table
/// thrown away. The catalog side is discovered rather than written down, over
/// <see cref="RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate" /> — shared rather than
/// copied, so this scan widens on the day that constant widens. Four spellings of those two
/// conditions have already had to be found by hand once; a fifth copy that missed the next widening
/// would report green over exactly the relation kinds the widening was for, here meaning a view or a
/// materialized view holding columns the model has never heard of.
/// </para>
/// <para>
/// <b>Set equality in both directions, and the two directions catch different things.</b> A catalog
/// column the model does not map is either a real defect in a configuration or a relation nobody's
/// model owns; a model column the catalog no longer holds is migration drift, which is invisible
/// until a query names it. Only one of them can be caught by the application failing at runtime, and
/// it is not the first.
/// </para>
/// <para>
/// <b>Offenders are asserted as collections and written to the console as text.</b> TUnit's string
/// assertions truncate, so a census that reported through a single <c>string.Join</c> comparison
/// names the first offender and hides the rest — which on a first run over an unmeasured premise is
/// the difference between a finding and a guess. The console line carries the whole list; the
/// assertion carries the verdict.
/// </para>
/// <para>
/// Two provable-fail controls sit beside the reconciliation, one per direction, <b>on hosts of their
/// own</b>. An empty offender set is what a scan that never reached the catalog also produces, and
/// what a comparison over two empty sets produces, and green is exactly what both look like. The
/// controls are permanent rather than a one-off measurement: each grows the disagreement it is about
/// on this host's own cloned database and demands the same comparison name it. They cannot share a
/// host, or each would be free to pass for the other's reason.
/// </para>
/// <para>
/// Neither control mutates an entity configuration, and that is a rule rather than a preference. A
/// model-only mutation desynchronises the frozen migration baseline, and
/// <c>PendingModelChangesWarning</c> then kills roughly twelve hundred tests <i>before</i> the
/// database is ever asked anything — a red from drift, produced by a test written to measure
/// behaviour, which is evidence of nothing.
/// </para>
/// </remarks>
public sealed class DataInventoryReconciliationTests
{
    /// <summary>
    /// The catalog-direction control's throwaway relation: a table the model has never heard of.
    /// </summary>
    /// <remarks>
    /// A constant rather than a literal repeated across the create and the assertion, because a typo
    /// split between the two does not fail — it simply never finds the probe, and a control that
    /// cannot find its own probe agrees with everything. The prefix says what it is if the name ever
    /// leaks into a database somebody shares; here it cannot, because every host owns a database
    /// cloned from the migrated template and dropped with it.
    /// </remarks>
    private const string UnmappedProbeTable = "data_inventory_probe_unmapped";

    /// <summary>
    /// The mapped column the model-direction control drops, and the sibling it must not report.
    /// </summary>
    /// <remarks>
    /// <c>wrapped_account_keys</c> carries two columns of the same kind, which is what makes it the
    /// right table to drop from: the surviving sibling is the negative half of the control, proving
    /// the comparison reports the column that went rather than the whole model list whenever
    /// anything moves. <c>cascade</c> because the width and version checks are defined on the column
    /// and go with it.
    /// </remarks>
    private const string DroppedColumn = "wrapped_account_keys.wrapped_index_key";

    /// <summary>The dropped column's surviving sibling, which the same run must not report.</summary>
    private const string SurvivingSiblingColumn = "wrapped_account_keys.wrapped_content_key";

    [Test]
    public async Task Model_AndTheLiveCatalog_DescribeTheSameColumns()
    {
        // Arrange — a real database, migrated, read on the container superuser connection so that
        // row-level security cannot decide what a catalog query is allowed to see.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — every column of every row-bearing relation in `public`, minus the columns of every
        // relation the inventory has written down as living outside the model, compared against
        // every column the design-time model maps.
        IReadOnlyList<string> catalogColumns = await ReadCatalogColumnsAsync(admin);
        Reconciliation reconciliation = Reconcile(catalogColumns);

        // Reported before the assertion, and in full. A truncated census names one offender; the
        // first run of this test is the only measurement anybody has of whether the model and the
        // schema agree, so the whole list is the finding.
        Console.WriteLine(
            $"Catalog columns (outside-the-model relations removed): {reconciliation.Catalog.Count}. "
            + $"Model columns: {reconciliation.Model.Count}. "
            + $"Relations outside the model: {string.Join(", ", DataInventory.RelationsOutsideTheModel)}.");
        Console.WriteLine(
            $"In the catalog, not in the model: {string.Join(", ", reconciliation.InCatalogOnly)}");
        Console.WriteLine(
            $"In the model, not in the catalog: {string.Join(", ", reconciliation.InModelOnly)}");

        // Assert — the non-empty checks first. Both directions are set differences, and two empty
        // sets differ in neither direction: a scan that reached no catalog and a model that
        // enumerated nothing would satisfy the two assertions below completely. This is the half
        // that stops the reconciliation agreeing with itself.
        await Assert.That(reconciliation.Catalog).IsNotEmpty();
        await Assert.That(reconciliation.Model).IsNotEmpty();

        // Both directions, as collections. A column here is one of exactly three things, and the
        // remedy differs for each: a column on a relation nobody's model owns means that RELATION
        // owes an entry in DataInventory.RelationsOutsideTheModel with a written reason; a column on
        // a mapped table means a configuration is wrong; and a model column the catalog lacks means
        // the migrations and the configurations have drifted apart. None of the three is fixed by
        // widening the exclusion list, and the first is fixed by naming a relation rather than a
        // column.
        await Assert.That(reconciliation.InCatalogOnly).IsEmpty();
        await Assert.That(reconciliation.InModelOnly).IsEmpty();
    }

    [Test]
    public async Task Model_AndTheLiveCatalog_ReportACatalogColumnTheModelDoesNotMap()
    {
        // Arrange — a throwaway relation on this host's own cloned database, carrying two columns no
        // configuration mentions. It is created on the admin connection and never dropped: the
        // database goes away with the host, which is also why the name carries a prefix saying what
        // it is if it ever leaks somewhere shared. Its own host, so the sibling control's dropped
        // column cannot appear in the result this assertion reads.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(
            admin,
            $"create table {UnmappedProbeTable} (id uuid primary key, payload text not null)");

        // Act — the same reader and the same comparison the reconciliation runs, knowing nothing
        // about the probe. A control exercising a second, separately written query would prove that
        // query can fail and say nothing about the one that ships.
        IReadOnlyList<string> catalogColumns = await ReadCatalogColumnsAsync(admin);
        Reconciliation reconciliation = Reconcile(catalogColumns);

        // Assert — fail-closed proven in the catalog direction: an unmapped column is named, as
        // table.column, so a failure in anger says which relation grew it.
        await Assert.That(reconciliation.InCatalogOnly)
            .Contains($"{UnmappedProbeTable}.payload");

        // And a column the model does map is not reported in the same run, so this cannot be passing
        // because the comparison reports every column it sees.
        await Assert.That(reconciliation.InCatalogOnly).DoesNotContain(SurvivingSiblingColumn);

        // Nor is the other direction disturbed by a relation the catalog gained. A control that let
        // both lists fill would prove neither direction in particular.
        await Assert.That(reconciliation.InModelOnly).IsEmpty();
    }

    [Test]
    public async Task Model_AndTheLiveCatalog_ReportAModelColumnTheCatalogNoLongerHolds()
    {
        // Arrange — the second direction, proven over the LIVE CATALOG rather than over a literal
        // handed to the comparison. This is the real event the direction exists to catch: a
        // migration removing a column and leaving the configuration that maps it behind, which
        // nothing notices until a query names it. Its own host, for the reason its sibling has one.
        //
        // The schema is what moves, never the model. A model-only mutation would desynchronise the
        // frozen baseline and kill the assembly on PendingModelChangesWarning before the database
        // saw anything, which is a red about drift rather than about this comparison.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(
            admin,
            "alter table wrapped_account_keys drop column wrapped_index_key cascade");

        // Act
        IReadOnlyList<string> catalogColumns = await ReadCatalogColumnsAsync(admin);
        Reconciliation reconciliation = Reconcile(catalogColumns);

        // Assert — the orphaned mapping is named.
        await Assert.That(reconciliation.InModelOnly).Contains(DroppedColumn);

        // And its surviving sibling on the same table is not, so this cannot be passing because the
        // comparison reports the whole model list whenever anything moves.
        await Assert.That(reconciliation.InModelOnly).DoesNotContain(SurvivingSiblingColumn);

        // The catalog direction stays clean: dropping a column adds nothing to the schema.
        await Assert.That(reconciliation.InCatalogOnly).IsEmpty();
    }

    [Test]
    public async Task RelationsOutsideTheModel_NameARelationThatExists()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — every row-bearing relation in `public`, against every name the inventory excuses.
        IReadOnlyList<string> relations = await ReadCatalogRelationsAsync(admin);
        HashSet<string> discovered = new(relations, StringComparer.Ordinal);
        string[] namingNothing =
        [
            .. DataInventory.RelationsOutsideTheModel
                .Where(relation => !discovered.Contains(relation))
                .Order(StringComparer.Ordinal),
        ];

        Console.WriteLine($"Relations in public: {string.Join(", ", relations)}");

        // Assert — non-empty first, in both lists. An empty exclusion list names nothing that is
        // missing and would pass this test while removing the reconciliation's only exclusion; an
        // empty discovery would pass nothing at all but is worth failing here rather than as an
        // unexplained list of absent names.
        await Assert.That(DataInventory.RelationsOutsideTheModel).IsNotEmpty();
        await Assert.That(relations).IsNotEmpty();

        // An entry naming no relation is reported rather than dropped: such a name lies in wait for
        // whatever is next called that, ready to excuse it from the reconciliation on a reason
        // written about something else entirely. SchemaClassification.ExemptionsNamingNoTable makes
        // the identical point about the row-level-security exemptions, and it makes it because the
        // hazard is the list rather than the subject.
        //
        // Ordinal comparison, deliberately: pg_class stores __EFMigrationsHistory exactly as EF
        // quotes it, and a loose comparison would let an entry claim a relation it does not name.
        await Assert.That(namingNothing).IsEmpty();
    }

    /// <summary>Both directions of the comparison, with the two sets they were computed from.</summary>
    /// <remarks>
    /// The inputs travel with the answer because the answer alone cannot be read: two empty
    /// differences are what agreement looks like and also what a comparison over two empty sets
    /// looks like, so every caller needs the counts to tell those apart.
    /// </remarks>
    /// <param name="Catalog">Every catalog column considered, after the excluded relations were removed.</param>
    /// <param name="Model">Every column the design-time model maps.</param>
    /// <param name="InCatalogOnly">Catalog columns the model does not map.</param>
    /// <param name="InModelOnly">Mapped columns the catalog does not hold.</param>
    private sealed record Reconciliation(
        IReadOnlyList<string> Catalog,
        IReadOnlyList<string> Model,
        string[] InCatalogOnly,
        string[] InModelOnly);

    /// <summary>
    /// Compares what the catalog holds against what the model maps, in both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure function over what was read, so the reconciliation and both of its controls run
    /// byte-identical logic over databases that differ only in what was done to them. A control
    /// exercising a separately written comparison would prove that one can fail.
    /// </para>
    /// <para>
    /// The exclusion is applied to the <b>relation</b> and never to a column, which is the shape of
    /// the decision rather than a convenience: a relation is outside the model or it is not, and a
    /// per-column exclusion list would let a mapped table quietly drop one column out of the
    /// inventory with a reason nobody would think to question.
    /// </para>
    /// </remarks>
    private static Reconciliation Reconcile(IReadOnlyList<string> catalogColumns)
    {
        HashSet<string> outsideTheModel =
            new(DataInventory.RelationsOutsideTheModel, StringComparer.Ordinal);

        string[] considered =
        [
            .. catalogColumns.Where(column =>
                !outsideTheModel.Contains(column[..column.LastIndexOf('.')])),
        ];

        string[] mapped =
        [
            .. MappedSchema.ColumnsOf(MappedSchema.DesignTimeModel())
                .Select(column => column.Qualified),
        ];

        HashSet<string> catalog = new(considered, StringComparer.Ordinal);
        HashSet<string> model = new(mapped, StringComparer.Ordinal);

        return new Reconciliation(
            considered,
            mapped,
            [.. catalog.Except(model, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. model.Except(catalog, StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Every column of every row-bearing relation in <c>public</c>, as <c>table.column</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read straight from <c>pg_attribute</c> and deliberately not through EF's model or any
    /// production classifier. The point is to check the model against the schema, and a reader that
    /// asked EF what the schema holds would let the model agree with itself.
    /// </para>
    /// <para>
    /// The schema and the relation kinds come from
    /// <see cref="RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate" /> rather than being
    /// spelled here, for the reason its neighbours give: a copy that misses a widening reports green
    /// over exactly the relation kinds the widening was for.
    /// </para>
    /// <para>
    /// <c>attnum &gt; 0</c> drops the system columns, which belong to PostgreSQL rather than to
    /// anyone's inventory; <c>not attisdropped</c> drops the tombstones a dropped column leaves
    /// behind under a mangled name — which is exactly what makes the model-direction control a
    /// statement about the column being gone rather than about the row in the catalog being renamed.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ReadCatalogColumnsAsync(
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
        List<string> columns = [];

        while (await reader.ReadAsync())
        {
            columns.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        }

        return columns;
    }

    /// <summary>Every row-bearing relation in <c>public</c>, by name and once each.</summary>
    /// <remarks>
    /// A separate read rather than a projection of the column list, because a relation carrying no
    /// columns at all is still a relation an entry could be naming, and folding it out of the column
    /// query would make it invisible to the one test written to notice a name that matches nothing.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ReadCatalogRelationsAsync(
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

        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> relations = [];

        while (await reader.ReadAsync())
        {
            relations.Add(reader.GetString(0));
        }

        return relations;
    }

    /// <summary>Runs one DDL statement on the admin connection.</summary>
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
