namespace Infrastructure.Persistence.Inventory;

/// <summary>
/// What the product owes the person for one column, which is the question the inventory answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>These three words classify a column by the obligation it carries, never by the type it
/// holds.</b> A timestamp is <see cref="Arithmetic" /> on <c>transactions</c> and
/// <see cref="Excluded" /> on <c>credentials</c>, because the first is a day the person recorded and
/// the second is a fact about how an authenticator was enrolled. Reading the word off the column's
/// type is the mistake this remark exists to head off, and it is the one a reader makes first.
/// </para>
/// <para>
/// There is deliberately no fourth member and no <c>Unknown</c>. A column reaches this enum only by
/// somebody writing it down, and a member meaning "nobody decided" would be a way of writing down
/// that nothing was decided — which is exactly the state
/// <see cref="InventoryCoverage.Unclassified" /> reports and the build refuses.
/// </para>
/// </remarks>
public enum ColumnClassification
{
    /// <summary>
    /// User-authored free text: ciphertext in the column, absent from every log, present in the
    /// export.
    /// </summary>
    /// <remarks>
    /// The one classification that is <b>derived rather than chosen</b>. A narrative column is one the
    /// model types for <see cref="Domain.Security.NarrativeField" />, and the suite reads that set off
    /// the model, so an entry here can only agree with the model or go red.
    /// </remarks>
    Narrative,

    /// <summary>
    /// Server-readable and part of what the person owns, so the export carries a copy of it.
    /// </summary>
    /// <remarks>
    /// The word is about legibility and ownership together, not about arithmetic in the numeric
    /// sense: <c>users.email</c> and <c>transactions.date</c> are both of this kind, and neither is
    /// added up. What they share is that the server can read them and the person is entitled to have
    /// them back.
    /// </remarks>
    Arithmetic,

    /// <summary>
    /// Everything the export deliberately does not carry, each entry with a written reason.
    /// </summary>
    /// <remarks>
    /// The only one of the three that takes something away from the person, which is why
    /// <see cref="ColumnClassificationEntry.Excluded" /> is the only factory taking a reason and why
    /// its argument is not optional.
    /// </remarks>
    Excluded,
}

/// <summary>
/// One column of the schema with the classification somebody gave it, and — where that
/// classification withholds the column from the person — the argument for withholding it.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no public constructor, and that is the requirement rather than a style.</b> FR-006
/// asks that an excluded column record a reason. The cheapest way to satisfy a nullable
/// <c>Reason</c> member is to leave it null, so a type offering one would need a test demanding it be
/// non-null — and a test is a thing that can be deleted, skipped, or written after the entry it was
/// supposed to catch. Making the reason an argument that exists on
/// <see cref="Excluded" /> alone turns "an excluded column carries a reason" into a fact the compiler
/// holds, and turns "a classified column carries no stray reason" into a second one: there is no
/// overload through which <see cref="Narrative" /> or <see cref="Arithmetic" /> could be handed a
/// sentence, so a narrative column explained away in prose is not a value anybody can construct.
/// </para>
/// <para>
/// What the compiler cannot hold is whether the sentence is an argument or a noise. Nothing can — see
/// <c>DataInventoryCoverageTests.ExcludedColumns_StateAReasonSomebodyCanArgueWith</c>, whose length
/// floor makes writing <i>nothing</i> impossible and claims no more than that. So the reason gets a
/// null guard and no emptiness guard: a factory refusing the empty string would read as though the
/// content had been judged, which is the more dangerous of the two mistakes because it looks like
/// coverage.
/// </para>
/// <para>
/// The table travels with the column for the reason <see cref="MappedColumn" /> gives at its own
/// declaration: two tables here carry <c>name</c> and four carry <c>created_at_utc</c>, so an entry
/// keyed on the bare column name is an entry whose one written argument silently covers several
/// columns nobody argued about together.
/// </para>
/// </remarks>
public sealed record ColumnClassificationEntry
{
    /// <summary>
    /// The one constructor, private so that a classification can only be reached through the factory
    /// that knows whether a reason belongs with it.
    /// </summary>
    private ColumnClassificationEntry(
        string table,
        string column,
        ColumnClassification classification,
        string? excludedBecause)
    {
        Table = table;
        Column = column;
        Classification = classification;
        ExcludedBecause = excludedBecause;
    }

    /// <summary>The table name as the EF model maps it, which is how <c>pg_class</c> stores it.</summary>
    public string Table { get; }

    /// <summary>The column name as the EF model maps it.</summary>
    public string Column { get; }

    /// <summary>Which of the three obligations this column carries.</summary>
    public ColumnClassification Classification { get; }

    /// <summary>
    /// Why the export does not carry this column, or <see langword="null" /> when it does.
    /// </summary>
    /// <remarks>
    /// Null is not "no reason given". It is the shape of every entry that is not
    /// <see cref="ColumnClassification.Excluded" />, because the only factory that accepts a reason is
    /// the only classification that owes one.
    /// </remarks>
    public string? ExcludedBecause { get; }

    /// <summary>The pair as <c>table.column</c>, which is how the inventory names a column.</summary>
    /// <remarks>
    /// Computed rather than stored, matching <see cref="MappedColumn.Qualified" /> so the two sides of
    /// <see cref="DataInventoryCoverage.Compare" /> are rendered by the same rule. A stored third
    /// member could disagree with the two it claims to render, and every comparison downstream is made
    /// on this string.
    /// </remarks>
    public string Qualified => $"{Table}.{Column}";

    /// <summary>
    /// Classifies a column as user-authored free text the export carries as ciphertext.
    /// </summary>
    /// <param name="table">The table name as the model maps it.</param>
    /// <param name="column">The column name as the model maps it.</param>
    /// <returns>The entry.</returns>
    public static ColumnClassificationEntry Narrative(string table, string column) =>
        Create(table, column, ColumnClassification.Narrative, null);

    /// <summary>
    /// Classifies a column as server-readable content the person owns and the export copies.
    /// </summary>
    /// <param name="table">The table name as the model maps it.</param>
    /// <param name="column">The column name as the model maps it.</param>
    /// <returns>The entry.</returns>
    public static ColumnClassificationEntry Arithmetic(string table, string column) =>
        Create(table, column, ColumnClassification.Arithmetic, null);

    /// <summary>
    /// Classifies a column as one the export deliberately does not carry, with the argument for it.
    /// </summary>
    /// <remarks>
    /// <paramref name="because" /> has no default. An exclusion whose reason nobody stated is the
    /// state this signature exists to make unreachable, so adding one has to answer the question out
    /// loud — the call <see cref="Provisioning.TableExemption.ColumnsTheReasonCovers" /> already made
    /// one grain up.
    /// </remarks>
    /// <param name="table">The table name as the model maps it.</param>
    /// <param name="column">The column name as the model maps it.</param>
    /// <param name="because">
    /// What the person loses by this column's absence from the export, and why that is right.
    /// </param>
    /// <returns>The entry.</returns>
    public static ColumnClassificationEntry Excluded(string table, string column, string because)
    {
        ArgumentNullException.ThrowIfNull(because);

        return Create(table, column, ColumnClassification.Excluded, because);
    }

    /// <summary>
    /// Guards the pair the entry is identified by, which the three factories share.
    /// </summary>
    /// <remarks>
    /// Table and column are refused when blank because <see cref="Qualified" /> is what every
    /// comparison downstream is made on, and an entry rendering as <c>.</c> or <c>ers.</c> names no
    /// column while still filling a slot in the inventory. The reason is not guarded here; see the
    /// type's remarks for why judging it is review's job rather than a factory's.
    /// </remarks>
    private static ColumnClassificationEntry Create(
        string table,
        string column,
        ColumnClassification classification,
        string? excludedBecause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        return new ColumnClassificationEntry(table, column, classification, excludedBecause);
    }
}

/// <summary>
/// The two ways an inventory and a schema can disagree, reported together because a caller has to
/// see both to know the inventory is a description of this schema.
/// </summary>
/// <remarks>
/// Both are lists of <c>table.column</c> rather than of entries or of columns, because the two sides
/// have nothing else in common: an unclassified name exists only on the schema side and a stale one
/// only on the inventory side, and rendering them as the same kind of string is what lets one failure
/// message hold both.
/// </remarks>
/// <param name="Unclassified">
/// Columns the model maps that the inventory does not name. This is FR-005's failure: the remedy is
/// to decide which of the three words the column earns, never to widen anything.
/// </param>
/// <param name="Stale">
/// Columns the inventory names that the model no longer maps. Reported rather than dropped, for the
/// reason <see cref="Provisioning.SchemaClassification.ExemptionsNamingNoTable" /> exists: such an
/// entry lies in wait for whatever is next called that, ready to hand it a decision written about
/// something else.
/// </param>
public sealed record InventoryCoverage(
    IReadOnlyList<string> Unclassified,
    IReadOnlyList<string> Stale);

/// <summary>
/// Compares the columns a schema has against the columns an inventory classifies, in both
/// directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both arguments are parameters, and neither is <see cref="DataInventory.Entries" />.</b> A
/// classifier that reached for its own list could only be trusted, never tested: every control over
/// it would be measuring the shipped inventory rather than the comparison, and the shipped inventory
/// is complete, so an implementation ignoring its argument answers "nothing unclassified" to every
/// question anybody could ask it. The call
/// <see cref="Provisioning.RowLevelSecurityCoverage.Classify" /> already made, for the same reason and
/// against the same hazard.
/// </para>
/// <para>
/// It reads no <see cref="ColumnClassificationEntry.Classification" /> at all. Coverage is the
/// question of whether every column was decided about, and which way it was decided is a different
/// axis held by different cases — the narrative set against the model's own types, the exclusion
/// reasons against a floor. Folding either into this comparison would make one failure report two
/// unrelated kinds of problem.
/// </para>
/// <para>
/// Ordinal throughout: <c>pg_class</c> keeps an identifier exactly as EF spells it, and a loose
/// comparison would let an entry claim a column it does not name.
/// </para>
/// </remarks>
public static class DataInventoryCoverage
{
    /// <summary>
    /// Reports every mapped column no entry names, and every entry naming no mapped column.
    /// </summary>
    /// <remarks>
    /// Pure, and it returns data rather than throwing or logging: a build gate wants an exception
    /// naming the offenders and a test wants an assertion listing them, and neither shape belongs in
    /// the code that does the comparing — the same split
    /// <see cref="Provisioning.RowLevelSecurityCoverage" /> draws.
    /// <para>
    /// Each side is reported in its own input order and once each. A duplicate on either side would
    /// otherwise repeat in the output and make a failure message read as more disagreements than there
    /// are; that the inventory holds no duplicate at all is a separate claim, held next door because
    /// this comparison cannot see one.
    /// </para>
    /// </remarks>
    /// <param name="mapped">The schema's columns, ordinarily from <see cref="MappedSchema.ColumnsOf" />.</param>
    /// <param name="inventory">The written-down classifications to check that schema against.</param>
    /// <returns>The disagreement in both directions, empty in both when the two describe one schema.</returns>
    public static InventoryCoverage Compare(
        IReadOnlyList<MappedColumn> mapped,
        IReadOnlyList<ColumnClassificationEntry> inventory)
    {
        ArgumentNullException.ThrowIfNull(mapped);
        ArgumentNullException.ThrowIfNull(inventory);

        HashSet<string> classified = new(
            inventory.Select(entry => entry.Qualified), StringComparer.Ordinal);
        HashSet<string> present = new(
            mapped.Select(column => column.Qualified), StringComparer.Ordinal);

        List<string> unclassified = [];
        HashSet<string> reportedUnclassified = new(StringComparer.Ordinal);
        foreach (MappedColumn column in mapped)
        {
            if (!classified.Contains(column.Qualified)
                && reportedUnclassified.Add(column.Qualified))
            {
                unclassified.Add(column.Qualified);
            }
        }

        List<string> stale = [];
        HashSet<string> reportedStale = new(StringComparer.Ordinal);
        foreach (ColumnClassificationEntry entry in inventory)
        {
            if (!present.Contains(entry.Qualified) && reportedStale.Add(entry.Qualified))
            {
                stale.Add(entry.Qualified);
            }
        }

        return new InventoryCoverage(unclassified, stale);
    }
}
