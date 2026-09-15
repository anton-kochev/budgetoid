using Domain.Security;
using Infrastructure.Persistence.Inventory;
using Microsoft.EntityFrameworkCore.Metadata;

namespace UnitTests;

/// <summary>
/// The FR-005 gate: every column the EF model maps is classified <i>narrative</i>,
/// <i>arithmetic</i> or <i>excluded</i>, and a column nobody classified is red.
/// </summary>
/// <remarks>
/// <para>
/// FR-005 is <i>Must; Test</i> — "the build shall fail when a column exists that the inventory does
/// not classify". Phase 1 built the two halves this rests on and proved they describe the same
/// schema: <see cref="MappedSchema" /> enumerates the model's columns keeping the table, and
/// <c>DataInventoryReconciliationTests</c> reconciles that enumeration against the live catalog in
/// both directions. This file is the classification laid over the enumeration. It needs no database,
/// deliberately: the reconciliation is what makes "the model" a statement about the schema, and once
/// that is held next door, coverage is a question about two lists.
/// </para>
/// <para>
/// <b>The three words classify a column by what the product owes the person for it, not by what the
/// column holds.</b> <i>Narrative</i> is user-authored free text: it must be ciphertext, may not
/// reach a log, and must be in the export. <i>Arithmetic</i> is server-readable and part of what the
/// person owns, so the export is a copy of it. <i>Excluded</i> is everything the export deliberately
/// does not carry, each entry with a written reason. The SRS Definitions table describes a
/// sensitivity split <i>inside a budget</i> and is not a rule for classifying
/// <c>session_tokens.token_hash</c>.
/// </para>
/// <para>
/// Two readings a reader will get wrong, and neither is a fact any assertion here can carry —
/// they are said in words because they are the two places the word "obvious" points the wrong way.
/// <c>credentials.created_at_utc</c> is a timestamp and is <b>excluded</b>, because a credential is
/// not content a person owns. <c>users.email</c> is <b>arithmetic</b> and <i>is</i> exported, even
/// though a separate requirement forbids it from a log — "not in a log" and "not in the export" are
/// different obligations and only the second is what <i>excluded</i> means.
/// </para>
/// <para>
/// <b><see cref="DataInventoryCoverage.Compare" /> takes the inventory as a parameter and must never
/// reach for <see cref="DataInventory.Entries" />.</b> A classifier hardwired to its own list can
/// only be trusted, never tested — the call
/// <see cref="Infrastructure.Persistence.Provisioning.RowLevelSecurityCoverage.Classify" /> already
/// made, and for the same reason: the two permanent controls below hand it lists the shipped
/// inventory is not, and a hardwired implementation answers both of them with the shipped one's
/// verdict.
/// </para>
/// <para>
/// <b>Offenders are asserted as collections and written to the console as text.</b> TUnit's string
/// assertions truncate — measured in this repository — so a census reporting through a single
/// <c>string.Join</c> comparison names the first offender and hides the rest, which on a gate over
/// ninety-three columns is the difference between a diff and a scavenger hunt. The console line
/// carries the whole list; the assertion carries the verdict.
/// </para>
/// <para>
/// <b>Deliberately absent: <c>Inventory_AssignsExactlyOneClassificationToEachColumn</c>.</b>
/// <see cref="ColumnClassificationEntry.Classification" /> is one non-nullable enum member per
/// entry, so an entry carrying two classifications is not a value that can be constructed;
/// <see cref="Inventory_NamesEachColumnAtMostOnce" /> closes the only remaining way to state two —
/// two entries for one column. Between them that is a type fact, and a test restating a type fact is
/// a decoration: it can never fail, so it teaches the next reader that the suite has an assertion
/// where it has none.
/// </para>
/// </remarks>
public sealed class DataInventoryCoverageTests
{
    /// <summary>
    /// The floor the mapped-column count must clear before an empty offender list means anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A floor and never an exact count</b>, which is the call <c>MappedSchemaTests</c> already
    /// made and made for a reason this file inherits whole. The model maps ninety-three columns over
    /// sixteen tables today; a ninety-three written here would go red on every legitimate column
    /// anybody adds, which trains the next reader to edit the number rather than read the diff, and
    /// this repository has drifted on a written-down count twice.
    /// </para>
    /// <para>
    /// What the floor is for is the failure the set assertions cannot see: an enumerator returning
    /// primary keys only, or one entity type only, or nothing at all because the design-time model
    /// came back bare. Sixteen tables' keys are about sixteen entries; eighty cannot be reached
    /// without walking most of the model's properties.
    /// </para>
    /// </remarks>
    private const int MappedColumnFloor = 80;

    /// <summary>
    /// The shortest a reason for excluding a column from the export may be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The limit is carried here rather than inferred, and it is a floor on writing rather than a
    /// judgement of the words.</b> The same call as
    /// <c>KeyMaterialSecrecyTests.Classifications_StateWhatEachColumnHoldsAndWhyItUnwrapsNothing</c>
    /// (KeyMaterialSecrecyTests.cs:363-390), whose remarks say it outright: no assertion can tell a
    /// real argument from a fluent one. All a floor can do is make writing <i>nothing</i>
    /// impossible; the rest is review, and pretending otherwise would be the more dangerous of the
    /// two mistakes because it reads like coverage.
    /// </para>
    /// <para>
    /// Eighty, matching that file's reason floor rather than its lower <c>Holds</c> floor, because
    /// this member answers the same shape of question: not "what is in this column" — a clause of
    /// fact — but "why does the person not get this back", which is an argument the next reader has
    /// to be able to disagree with. The sentences it has to beat are restatements of the verdict:
    /// <c>internal</c>, <c>not user data</c>, <c>a secret</c>, <c>bookkeeping</c> all fit in a
    /// clause. Saying what the person loses by its absence, and why that is right, does not.
    /// </para>
    /// </remarks>
    private const int MinimumExclusionReasonLength = 80;

    /// <summary>
    /// The fictitious column the stale-direction control carries, which no configuration maps.
    /// </summary>
    /// <remarks>
    /// A constant rather than a literal repeated between the entry and the assertion. A typo split
    /// across the two does not fail — the assertion simply never finds the probe, and a control that
    /// cannot find its own probe agrees with everything. The name says what it is on sight, because
    /// a plausible one would eventually be read as a column somebody forgot to remove.
    /// </remarks>
    private const string FictitiousColumn = "data_inventory_probe.no_such_column";

    /// <summary>
    /// A real mapped column the stale-direction control must <b>not</b> report, and the same column
    /// the narrative case pins from the other side.
    /// </summary>
    private const string RealNarrativeColumn = "transactions.description";

    /// <summary>A real mapped column on the far side of the schema from the one above.</summary>
    /// <remarks>
    /// The two are named together wherever a list has to be shown to reach more than one corner of
    /// the model. <c>transactions.description</c> is a sealed narrative column on the largest table;
    /// <c>wrapped_account_keys.wrapped_private_key</c> is key material on the newest one, mapped
    /// through a value converter. A walk that reached only the tables registered first, or only the
    /// properties with simple types, misses exactly one of the two.
    /// </remarks>
    private const string RealKeyMaterialColumn = "wrapped_account_keys.wrapped_private_key";

    /// <summary>
    /// The gate itself: every column the model maps is classified, and every classification names a
    /// column the model still maps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion FR-005 asks for, and it is the one that goes red when somebody adds a
    /// column. The remedy for an entry in <see cref="InventoryCoverage.Unclassified" /> is to decide
    /// which of the three words the new column earns and write it down — never to widen anything —
    /// and the remedy for an entry in <see cref="InventoryCoverage.Stale" /> is to delete a
    /// classification whose column has gone, which is drift the reconciliation next door would
    /// otherwise be the only thing to notice.
    /// </para>
    /// <para>
    /// The floor comes before both, because both are set differences and two empty sets differ in
    /// neither direction. A gate that ran against an empty model would satisfy the two assertions
    /// below completely, and green is exactly what that looks like.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Inventory_ClassifiesEveryColumnTheModelMaps()
    {
        // Arrange — the design-time model, which is what the reconciliation proved describes the
        // same columns the shipped schema holds.
        IReadOnlyList<MappedColumn> mapped = MappedSchema.ColumnsOf(MappedSchema.DesignTimeModel());

        // Act
        InventoryCoverage coverage = DataInventoryCoverage.Compare(mapped, DataInventory.Entries);

        // Reported before the assertion, and in full: a failure here is a list of decisions somebody
        // has to make, and a truncated list is a decision somebody will miss.
        Console.WriteLine(
            $"Mapped columns: {mapped.Count}. Inventory entries: {DataInventory.Entries.Count}.");
        Console.WriteLine(
            $"Unclassified: {string.Join(", ", coverage.Unclassified.Order(StringComparer.Ordinal))}");
        Console.WriteLine(
            $"Stale: {string.Join(", ", coverage.Stale.Order(StringComparer.Ordinal))}");

        // Assert — the subject is non-empty first. Everything after this is a difference between two
        // sets, and an enumerator that returned nothing produces the same two empty lists a fully
        // classified schema does.
        await Assert.That(mapped.Count).IsGreaterThanOrEqualTo(MappedColumnFloor);
        await Assert.That(DataInventory.Entries).IsNotEmpty();

        // A mapped column nobody classified. This is FR-005 in one line: the requirement is not that
        // the inventory is long but that it is total, and the only way a column leaves the gate is by
        // being classified.
        await Assert.That(coverage.Unclassified).IsEmpty();

        // And a classification naming no mapped column. Reported rather than dropped, for the reason
        // SchemaClassification.ExemptionsNamingNoTable exists: such an entry lies in wait for
        // whatever is next called that, ready to hand it a decision written about something else.
        await Assert.That(coverage.Stale).IsEmpty();
    }

    /// <summary>
    /// The permanent control in the unclassified direction: an empty inventory classifies nothing,
    /// so every mapped column must come back unclassified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Permanent rather than a one-off measurement. An empty <see cref="InventoryCoverage.Unclassified" />
    /// is what a complete inventory produces and equally what a <see cref="DataInventoryCoverage.Compare" />
    /// that reports nothing produces, and green is what both look like — so the gate above cannot
    /// distinguish its own success from its own absence. This is the case that can.
    /// </para>
    /// <para>
    /// It is also the case that holds <b><see cref="DataInventoryCoverage.Compare" /> taking its
    /// inventory as a parameter</b>. An implementation reading <see cref="DataInventory.Entries" />
    /// instead of the argument answers this with an empty list, because the shipped inventory is
    /// complete — so a hardwired classifier reddens here and only here.
    /// </para>
    /// <para>
    /// <b>A floor, never ninety-three.</b> The exact count is a pin nothing else in this repository
    /// holds and this repository has drifted on one twice; what the control needs is that the
    /// comparison reported the <i>schema</i> rather than a handful of columns, and eighty is
    /// unreachable by any partial walk.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Inventory_AgainstAnEmptyClassification_ReportsEveryMappedColumn()
    {
        // Arrange — the real model against a classification of nothing.
        IReadOnlyList<MappedColumn> mapped = MappedSchema.ColumnsOf(MappedSchema.DesignTimeModel());
        IReadOnlyList<ColumnClassificationEntry> nothingClassified = [];

        // Act — the same comparison the gate runs, which is the point: a control exercising a second,
        // separately written comparison would prove that second one can fail and say nothing about
        // the one that ships.
        InventoryCoverage coverage = DataInventoryCoverage.Compare(mapped, nothingClassified);

        Console.WriteLine(
            $"Unclassified against an empty inventory: {coverage.Unclassified.Count} of "
            + $"{mapped.Count} mapped columns.");

        // Assert — the whole schema comes back, measured against a floor.
        await Assert.That(coverage.Unclassified.Count).IsGreaterThanOrEqualTo(MappedColumnFloor);

        // Two named columns from opposite corners of the model, so this cannot be passing on a
        // comparison that reached one entity type and stopped. A count alone would.
        await Assert.That(coverage.Unclassified).Contains(RealNarrativeColumn);
        await Assert.That(coverage.Unclassified).Contains(RealKeyMaterialColumn);

        // And the other direction stays clean: an inventory naming nothing has nothing to be stale.
        // Without this, a Compare that put every column in both lists would satisfy everything above.
        await Assert.That(coverage.Stale).IsEmpty();
    }

    /// <summary>
    /// The permanent control in the stale direction: a classification naming a column the model does
    /// not map is reported, and its ninety-odd real neighbours are not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real event this direction exists to catch is a column removed from a configuration with
    /// its classification left behind — an inventory that reads as a complete account of a schema
    /// that has moved. The gate above cannot demonstrate that it would notice, because the shipped
    /// inventory has nothing stale in it.
    /// </para>
    /// <para>
    /// The synthetic inventory classifies every real column as <i>arithmetic</i>, which is wrong for
    /// the eight narrative ones and does not matter:
    /// <see cref="DataInventoryCoverage.Compare" /> is a set comparison over
    /// <see cref="ColumnClassificationEntry.Qualified" /> and reads no classification at all.
    /// <see cref="ColumnClassificationEntry.Arithmetic" /> is chosen over the excluded factory so
    /// that no invented exclusion reason appears in this file for somebody to copy into the real
    /// inventory later.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Inventory_ForAColumnTheModelNoLongerMaps_ReportsItStale()
    {
        // Arrange — every mapped column classified, plus one entry naming a column that has never
        // existed. Building the synthetic list from the model rather than by hand is what makes the
        // negative half of the assertion mean something: the ninety-three real entries are all
        // eligible to be reported and none of them may be.
        IReadOnlyList<MappedColumn> mapped = MappedSchema.ColumnsOf(MappedSchema.DesignTimeModel());
        string[] fictitious = FictitiousColumn.Split('.');
        ColumnClassificationEntry[] inventory =
        [
            .. mapped.Select(column =>
                ColumnClassificationEntry.Arithmetic(column.Table, column.Column)),
            ColumnClassificationEntry.Arithmetic(fictitious[0], fictitious[1]),
        ];

        // Act
        InventoryCoverage coverage = DataInventoryCoverage.Compare(mapped, inventory);

        Console.WriteLine($"Stale: {string.Join(", ", coverage.Stale.Order(StringComparer.Ordinal))}");

        // Assert — the orphaned classification is named, as table.column, so a failure in anger says
        // which entry to delete.
        await Assert.That(coverage.Stale).Contains(FictitiousColumn);

        // And the real columns are not reported in the same run, so this cannot be passing because
        // the comparison reports the whole inventory whenever anything disagrees. Two named, from
        // opposite corners, rather than a count: a count of ninety-three would also be satisfied by
        // reporting ninety-three of the wrong entries.
        await Assert.That(coverage.Stale).DoesNotContain(RealNarrativeColumn);
        await Assert.That(coverage.Stale).DoesNotContain(RealKeyMaterialColumn);

        // The unclassified direction is undisturbed: adding an entry removes nothing from the model.
        // A control that let both lists fill would prove neither direction in particular.
        await Assert.That(coverage.Unclassified).IsEmpty();
    }

    /// <summary>Every column the inventory names, it names once.</summary>
    /// <remarks>
    /// <para>
    /// A duplicate makes every count in this file lie, and a set comparison cannot see one: both
    /// entries name a column the model maps, so <see cref="InventoryCoverage.Unclassified" /> and
    /// <see cref="InventoryCoverage.Stale" /> are both empty and the gate is green.
    /// </para>
    /// <para>
    /// What it hides is worse than an inflated count. Two entries for one column can carry two
    /// different classifications, at which point the column is <i>narrative</i> to whoever reads
    /// <see cref="DataInventory.Of" /> first and <i>excluded</i> to whoever reads it second, and the
    /// answer depends on nothing a reader can see. This case is also the second half of the argument
    /// that "exactly one classification per column" is a type fact rather than a test: the enum
    /// member closes the one-entry case and this closes the two-entry one.
    /// </para>
    /// <para>
    /// Ordinal, because <c>pg_class</c> keeps the identifier as EF spells it and a loose comparison
    /// would let two entries that name different columns be reported as one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Inventory_NamesEachColumnAtMostOnce()
    {
        // Arrange
        IReadOnlyList<ColumnClassificationEntry> entries = DataInventory.Entries;

        // Act — grouped and reported by column, so a failure names the pair to reconcile rather than
        // a difference between two counts.
        string[] named =
        [
            .. entries
                .GroupBy(entry => entry.Qualified, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group =>
                    $"{group.Key} named {group.Count()} times, as "
                    + $"{string.Join(" and ", group.Select(entry => entry.Classification))}")
                .Order(StringComparer.Ordinal),
        ];

        Console.WriteLine($"Duplicated: {string.Join(" | ", named)}");

        // Assert — non-empty first: an empty inventory has no duplicate in it either, and would pass
        // the assertion below with nothing in it.
        await Assert.That(entries).IsNotEmpty();
        await Assert.That(named).IsEmpty();
    }

    /// <summary>
    /// The narrative eight are <b>derived</b> from the model's own types, never trusted to the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything else in this file is a coverage question — is each column classified as
    /// <i>something</i> — and coverage is exactly what a mis-classification survives. A narrative
    /// column entered as <i>arithmetic</i> is covered, the gate is green, and the column has quietly
    /// been declared server-readable content the export copies. This case is what makes that
    /// impossible in one direction: the set of narrative columns is read off the model, so the
    /// inventory can only agree with it.
    /// </para>
    /// <para>
    /// The evidence is <see cref="NarrativeField" /> itself, which is the type a narrative column
    /// accepts and has no constructor, factory or conversion taking a <see langword="string" />. So
    /// "this column holds user-authored free text that must be ciphertext" is a fact the model
    /// already states, and restating it in a hand-written list is where the two can disagree. It also
    /// closes the door in the other direction: nobody can re-classify
    /// <c>transactions.description</c> as arithmetic to green a coverage test.
    /// </para>
    /// <para>
    /// <b>The comparison is <c>ClrType == typeof(NarrativeField)</c>, with no nullable arm, and that
    /// is measured rather than assumed.</b> Two of the eight — <c>categories.description</c> and
    /// <c>category_groups.description</c> — are nullable narrative columns, and EF strips
    /// reference-type nullability from <c>IProperty.ClrType</c>: all eight report
    /// <c>Domain.Security.NarrativeField</c> exactly. There is no second form to test for.
    /// <see cref="NarrativeField" /> being a sealed <b>class</b> and deliberately not a
    /// <see langword="readonly" /> <see langword="struct" /> is what makes that so — a nullable
    /// struct would report <c>Nullable&lt;NarrativeField&gt;</c> for those two and this comparison
    /// would silently find six.
    /// </para>
    /// <para>
    /// Both directions are reported, and they mean different things. A narrative-typed column the
    /// inventory does not call narrative is a mis-classification. A column the inventory calls
    /// narrative that the model does not type for a sealed value is the opposite defect — a claim
    /// that plaintext is ciphertext — and is the more dangerous of the two, because everything
    /// downstream will believe it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Narrative_IsExactlyTheColumnsTypedForASealedValue()
    {
        // Arrange
        IReadOnlyList<MappedColumn> mapped = MappedSchema.ColumnsOf(MappedSchema.DesignTimeModel());

        // Act — the model's own answer, and the inventory's.
        string[] typedForASealedValue =
        [
            .. mapped
                .Where(column => column.ClrType == typeof(NarrativeField))
                .Select(column => column.Qualified)
                .Order(StringComparer.Ordinal),
        ];
        string[] classifiedNarrative =
        [
            .. DataInventory.Of(ColumnClassification.Narrative)
                .Select(entry => entry.Qualified)
                .Order(StringComparer.Ordinal),
        ];

        HashSet<string> typed = new(typedForASealedValue, StringComparer.Ordinal);
        HashSet<string> classified = new(classifiedNarrative, StringComparer.Ordinal);

        string[] sealedButNotClassifiedNarrative =
        [
            .. typed.Except(classified, StringComparer.Ordinal).Order(StringComparer.Ordinal),
        ];
        string[] classifiedNarrativeButNotSealed =
        [
            .. classified.Except(typed, StringComparer.Ordinal).Order(StringComparer.Ordinal),
        ];

        Console.WriteLine($"Typed for a sealed value: {string.Join(", ", typedForASealedValue)}");
        Console.WriteLine($"Classified narrative: {string.Join(", ", classifiedNarrative)}");
        Console.WriteLine(
            "Typed for a sealed value, not classified narrative: "
            + string.Join(", ", sealedButNotClassifiedNarrative));
        Console.WriteLine(
            "Classified narrative, not typed for a sealed value: "
            + string.Join(", ", classifiedNarrativeButNotSealed));

        // Assert — the derived side is non-empty first. Both differences below are set differences,
        // and a model walk that found no NarrativeField at all agrees with an inventory that
        // classifies nothing as narrative. That is the failure a green here would otherwise be.
        await Assert.That(typedForASealedValue).IsNotEmpty();

        // Named, so the derived side is shown to have found a real column rather than an accident of
        // an empty comparison. transactions.description is the narrative column on the largest table
        // and the one a later reader is most likely to want to call arithmetic.
        await Assert.That(typedForASealedValue).Contains(RealNarrativeColumn);

        // A narrative column classified as something else: the mis-classification the coverage gate
        // cannot see, because such a column is covered.
        await Assert.That(sealedButNotClassifiedNarrative).IsEmpty();

        // And a column claimed narrative that the model does not type for a sealed value — a claim
        // that plaintext is ciphertext, which is the worse direction.
        await Assert.That(classifiedNarrativeButNotSealed).IsEmpty();
    }

    /// <summary>
    /// Every excluded column carries a reason somebody can argue with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coverage gate compares <see cref="ColumnClassificationEntry.Qualified" /> and nothing
    /// else, so <see cref="ColumnClassificationEntry.ExcludedBecause" /> is read by no assertion in
    /// this file at all: a column entered as <c>Excluded("t", "c", "")</c> satisfies the gate
    /// completely. That is the requirement gone. <i>Excluded</i> is the one of the three words that
    /// takes something away from the person — it is what the export deliberately does not carry — so
    /// the reason is not documentation of the decision, it is the decision.
    /// </para>
    /// <para>
    /// <b>A length floor, and <see cref="MinimumExclusionReasonLength" /> carries its own argument at
    /// its declaration.</b> The short version, because it belongs at the assertion too: no assertion
    /// can tell a real argument from a fluent one — the call
    /// <c>KeyMaterialSecrecyTests.Classifications_StateWhatEachColumnHoldsAndWhyItUnwrapsNothing</c>
    /// (KeyMaterialSecrecyTests.cs:363-390) makes and states in the same words. The floor only makes
    /// writing nothing impossible, and the rest is review. A file that pretended otherwise would be
    /// worse than one with no check here, because it would read as though the reasons had been
    /// judged.
    /// </para>
    /// <para>
    /// Reported per column rather than counted, so a failure names the entry to argue about.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ExcludedColumns_StateAReasonSomebodyCanArgueWith()
    {
        // Arrange
        IReadOnlyList<ColumnClassificationEntry> excluded =
            DataInventory.Of(ColumnClassification.Excluded);

        // Act
        string[] unargued =
        [
            .. excluded
                .Where(entry => string.IsNullOrWhiteSpace(entry.ExcludedBecause)
                                || entry.ExcludedBecause.Length < MinimumExclusionReasonLength)
                .Select(entry => $"{entry.Qualified} excluded because: <unargued>")
                .Order(StringComparer.Ordinal),
        ];

        Console.WriteLine(
            $"Excluded columns: {excluded.Count}. Unargued: {string.Join(" | ", unargued)}");

        // Assert — non-empty first. An inventory with no excluded column has no unargued one either,
        // and would pass the assertion below with nothing in it; and an Of() that filtered everything
        // away would look identical. This schema holds secrets, bookkeeping and credential metadata,
        // so an empty excluded set is itself the finding.
        await Assert.That(excluded).IsNotEmpty();
        await Assert.That(unargued).IsEmpty();
    }
}
