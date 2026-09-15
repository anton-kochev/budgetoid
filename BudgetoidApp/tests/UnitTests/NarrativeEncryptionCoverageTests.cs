using Domain.Security;
using Infrastructure.Persistence.Inventory;
using Microsoft.EntityFrameworkCore.Metadata;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The FR-057 gate: every column the inventory classifies <i>narrative</i> is stored as opaque,
/// version-pinned, length-bounded ciphertext, and a narrative column that is not goes red naming
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// FR-057 is <i>Must; Test</i> — "when a column classified as narrative is added to the schema, the
/// encryption coverage test shall fail unless that column is stored as ciphertext". NFR-022 makes
/// <see cref="DataInventory" /> the single source the gate reads, and NFR-023 asks that adding a
/// column edit its classification and nothing else. Both are properties of the <i>arrangement</i>
/// rather than of any one assertion: the narrative side of every case below comes from
/// <see cref="DataInventory.Of" /> or from a list built for a control, and no case in this file
/// names one of the eight columns in order to check it.
/// </para>
/// <para>
/// <b>The gate reads two lists and builds neither, which is what makes every control below
/// writable.</b> <see cref="NarrativeEncryptionCoverage.Compare" /> takes the schema side and the
/// inventory side as parameters — the call <see cref="DataInventoryCoverage.Compare" /> and
/// <see cref="Infrastructure.Persistence.Provisioning.RowLevelSecurityCoverage.Classify" /> both
/// already made, for the reason they state: a classifier reaching for the shipped inventory could
/// only be trusted, never tested, because the shipped inventory agrees with the shipped schema and
/// an implementation ignoring both arguments answers "nothing wrong" to every question. So every
/// wrong schema in this file is a <see cref="StoredColumn" /> built by hand, and no case asks
/// production to be mutated in order to demonstrate that the gate can fail.
/// </para>
/// <para>
/// <b>The single most important case here is the completeness pin, and it is not the gate.</b>
/// <see cref="MappedSchema.StoredColumnsOf" /> had no counterpart to the two-directional pin
/// <see cref="DataInventoryCoverage.Compare" /> puts on <see cref="MappedSchema.ColumnsOf" />, and
/// its own remarks say so and say what it costs: the gate reads only the columns the inventory calls
/// narrative, so a walk returning eight of ninety-three leaves FR-057 green while eighty-five
/// columns go unenumerated. <see cref="StoredColumns_DescribeTheSameSchemaTheEnumeratorDoes" /> is
/// what closes it, by holding the two walks equal as sets and as counts.
/// </para>
/// <para>
/// <b>Offenders are asserted as collections and written to the console as text.</b> TUnit's string
/// assertions truncate — measured in this repository — so a census reporting through a single
/// <c>string.Join</c> comparison names the first offender and hides the rest. The console line
/// carries the whole list; the assertion carries the verdict. For the same family of reasons nothing
/// here leans on <c>IsEquivalentTo</c>: its default collection ordering is <c>Any</c>, so it is not
/// the set claim it reads as, and every set claim below is spelled out as two explicit differences.
/// </para>
/// <para>
/// <b>The synthetic controls spell their predicates out as literals rather than rendering them from
/// the constants the gate renders from.</b> A control built by calling the same expression the
/// subject calls agrees with the subject however either is changed, including with a change to the
/// shape both sides share. Written out, this file is a second statement of what a sealed column's
/// two checks look like, and a rewrite of the rendering reddens here and asks to be re-approved. The
/// cost is real and is the trade: the day <see cref="CiphertextEnvelope.Version" /> becomes 2, the
/// literals below need editing alongside the configurations, and that edit is the point rather than
/// an inconvenience.
/// </para>
/// <para>
/// <b>Two limits the gate declares are not closed here and one of them is made executable.</b> The
/// cap a column earns is not checked at all — argued at
/// <see cref="NarrativeEncryptionCoverage" /> and demonstrated by
/// <see cref="Compare_AgainstColumnsCarryingTheOtherFieldClassesCap_AcceptsBoth" />. The constraint
/// <i>name</i> is not read, which is deliberately <b>not</b> given a case: an implementation that
/// started reading names is already refused by
/// <see cref="Compare_GivenOnlyTheBlindIndexConstraint_StillReportsTheNarrativeColumn" />, and the
/// names themselves are pinned in the integration tier by <c>SchemaConstraintSnapshotTests</c>,
/// which selects <c>conname</c> beside <c>pg_get_constraintdef</c>. A case asserting that a rename
/// changes nothing would restate an absence twice covered and could be reddened by no mutation the
/// trap case does not already redden.
/// </para>
/// <para>
/// <b>That pointer is true and was incomplete, and the missing half decides which tier catches a
/// rename.</b> <c>SchemaConstraintSnapshotTests</c> reads the <i>applied catalog</i> — it selects
/// from <c>pg_constraint</c> on a live database — so it sees a renamed constraint only once a
/// migration carrying the rename exists and has run. A rename made in the <i>configuration</i> alone
/// never reaches that query: what catches it is the drift guard,
/// <c>BudgetoidDbContextConstructionTests.Migrations_MatchTheModel</c>, which diffs the migrations
/// snapshot against the design-time model and refuses a model that has moved without one — and,
/// beneath it, <c>MigrateAsync</c> refusing a drifted model, which fails every container-backed test
/// at once. So a constraint name in this schema is held by two guards in sequence and by neither
/// alone, and the sentence above should not be read as "the snapshot sees a rename the moment
/// somebody makes one". Nothing in this tier sees one at all, which is the honest scope of the
/// paragraph it corrects.
/// </para>
/// </remarks>
public sealed class NarrativeEncryptionCoverageTests
{
    /// <summary>
    /// The floor the mapped-column count must clear before an empty difference means anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A floor and never an exact count</b>, the call <c>MappedSchemaTests</c> and
    /// <c>DataInventoryCoverageTests</c> both already made and made for a reason this file inherits
    /// whole: a ninety-three written here goes red on every legitimate column anybody adds, which
    /// trains the next reader to edit the number rather than read the diff, and this repository has
    /// drifted on a written-down count twice.
    /// </para>
    /// <para>
    /// Eighty, matching its two neighbours so that the three cannot disagree about how much of a
    /// walk counts as a walk. What it is for is the failure a set difference cannot see: an
    /// enumerator returning the primary keys only, or the first entity type only, or nothing at all.
    /// Sixteen tables' keys are about sixteen entries; eighty cannot be reached without walking most
    /// of the model's properties.
    /// </para>
    /// </remarks>
    private const int MappedColumnFloor = 80;

    /// <summary>
    /// The floor the number of narrative columns the gate actually examined must clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Six rather than eight, and the difference is what a floor is for.</b> The eight are
    /// spread over six tables and no table carries more than two of them —
    /// <c>categories</c> and <c>category_groups</c> carry a name and a description each, the other
    /// four carry one column apiece — so six is unreachable without the gate having examined at
    /// least three tables. An eight would be a second home for a count the inventory already owns,
    /// red on the ninth narrative column somebody legitimately adds.
    /// </para>
    /// <para>
    /// It is a non-vacuity floor and nothing more, which is worth being explicit about because six
    /// looks like tolerance for losing two columns and is not. A narrative column lost from the
    /// <i>model</i> lands in <see cref="EncryptionCoverage.Unmapped" />, which the gate asserts
    /// empty; one lost from the <i>inventory</i> reddens
    /// <c>DataInventoryCoverageTests.Narrative_IsExactlyTheColumnsTypedForASealedValue</c>, which
    /// derives the set from the model's own types in both directions. Neither loss can arrive here
    /// quietly, so this number never has to be the thing that catches it.
    /// </para>
    /// </remarks>
    private const int ExaminedNarrativeColumnFloor = 6;

    /// <summary>A narrative column on the largest table, named where a corner of the model is needed.</summary>
    /// <remarks>
    /// Paired with <see cref="SecondRealNarrativeColumn" /> wherever a list has to be shown to reach
    /// more than one entity type. <c>transactions</c> is the leaf nothing references and
    /// <c>budgets</c> is the tenancy root, so a walk that reached the tables registered first, or
    /// stopped at the first entity type carrying a sealed property, misses exactly one of the two.
    /// </remarks>
    private const string RealNarrativeColumn = "transactions.description";

    /// <summary>The narrative column on the far side of the schema from the one above.</summary>
    /// <remarks>
    /// <c>budgets.name</c> is also the one narrative <c>name</c> column with no blind index beside
    /// it — the three <c>description</c> columns carry none either — so a list containing it cannot
    /// be a list of the four <c>name</c>/<c>name_key</c> pairs by accident.
    /// </remarks>
    private const string SecondRealNarrativeColumn = "budgets.name";

    /// <summary>
    /// A real mapped column that is arithmetic, server-readable, and stored as nothing like an
    /// envelope.
    /// </summary>
    /// <remarks>
    /// The subject of the hardwiring control. It has to be a column the shipped inventory does
    /// <b>not</b> classify narrative and that would fail all four checks if it were: a
    /// <see langword="decimal" /> in a <c>numeric</c> column on a table whose only narrative checks
    /// are written about <c>description</c>.
    /// </remarks>
    private const string PlaintextColumn = "transactions.amount";

    /// <summary>The length band a sealed <c>name</c> column's check declares, written out.</summary>
    /// <remarks>
    /// Twenty-nine is <see cref="CiphertextEnvelope.MinimumLength" /> — a version, a nonce and a tag
    /// over an empty plaintext — and 1024 is <see cref="NarrativeFieldLimits.NameBytes" />. Spelled
    /// rather than rendered, for the reason the type's remarks give: a control that computes its
    /// input the way the subject computes its expectation agrees with both sides of any change.
    /// </remarks>
    private const string NameLengthCheck = "length(name) between 29 and 1024";

    /// <summary>The version pin a sealed <c>name</c> column's check declares, written out.</summary>
    /// <remarks>
    /// <c>substring</c> and two hex digits, which is the one spelling the configurations use and
    /// deliberately not <c>get_byte</c>; the difference and why it is unreachable through any
    /// <c>INSERT</c> this schema admits are argued at <c>AccountConfiguration</c>'s version check.
    /// </remarks>
    private const string NameVersionCheck = @"substring(name from 1 for 1) = '\x01'::bytea";

    /// <summary>
    /// The two walks over the model describe the same schema — every column, in both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The hole this closes was measured by the enumerator's own author and written into its
    /// remarks.</b> <see cref="MappedSchema.StoredColumnsOf" /> has one consumer,
    /// <see cref="NarrativeEncryptionCoverage.Compare" />, and that consumer looks only at the
    /// columns the inventory calls narrative. So a walk returning the eight narrative columns and
    /// dropping the other eighty-five leaves the whole FR-057 gate green — measured, and stated at
    /// <see cref="MappedSchema.StoredColumnsOf" />. <see cref="MappedSchema.ColumnsOf" /> has no
    /// such gap: <see cref="DataInventoryCoverage.Compare" /> pins it against the inventory in both
    /// directions, and <c>DataInventoryReconciliationTests</c> pins the inventory against the live
    /// catalog. Holding the second walk equal to the first is what puts it inside that anchor.
    /// </para>
    /// <para>
    /// <b>Non-emptiness comes before the comparison, and that ordering is the point of the case
    /// rather than a formality.</b> Two empty sets differ in neither direction, so an enumerator
    /// that returned nothing satisfies both differences below completely and green is exactly what
    /// that looks like. It is the failure mode the whole inventory file is built around and the one
    /// <c>DataInventoryCoverageTests</c> states in its own remarks.
    /// </para>
    /// <para>
    /// <b>Counts are held equal beside the sets, because a set comparison is blind to a
    /// duplicate.</b> A <see cref="MappedSchema.StoredColumnsOf" /> that emitted a column twice —
    /// a nested loop over the entity's properties, a table walked once per index — agrees with
    /// <see cref="MappedSchema.ColumnsOf" /> on every qualified name while returning a list that is
    /// not the schema. <see cref="NarrativeEncryptionCoverage.Compare" /> de-duplicates internally
    /// and would not notice; the next reader of this enumerator has no such guarantee.
    /// </para>
    /// <para>
    /// <b>Two columns' facts are pinned outright, and that half was added because the set comparison
    /// alone was measured to admit the defect it most needed to refuse.</b> A
    /// <see cref="MappedSchema.StoredColumnsOf" /> handing <i>every</i> column the union of
    /// <i>every</i> table's check constraints agrees with <see cref="MappedSchema.ColumnsOf" /> on
    /// every name, in both directions, at the same count — and left this gate green. Worse and also
    /// measured: that mutation <b>combined with deleting <c>CK_accounts_name_length</c></b> was
    /// still green in the unit tier, because <c>length(name) between 29 and 1024</c> stands on
    /// <c>payees</c>, <c>categories</c> and <c>category_groups</c> too, so the predicate matcher
    /// finds it on the union and reports <c>accounts.name</c> sealed. The binding of a constraint to <i>its own</i>
    /// table is therefore the only thing holding
    /// <see cref="Compare_GivenOnlyTheBlindIndexConstraint_StillReportsTheNarrativeColumn" /> — the
    /// <c>name_key</c> trap, which is what makes FR-057's second criterion honest — and until now
    /// nothing pinned it.
    /// </para>
    /// <para>
    /// <b>The constraint set is exact, ordered and ordinal, and each of those three words is part of
    /// the claim.</b> Exact, because a subset assertion is satisfied by the union mutation and an
    /// emptiness assertion is satisfied by a walk that lost the constraints altogether — one
    /// comparison refutes both. Ordered, through <c>CollectionOrdering.Matching</c>, because TUnit's
    /// <c>IsEquivalentTo</c> defaults to <c>CollectionOrdering.Any</c> — measured in this repository,
    /// and <c>AccountTests</c> says so at its own assertions — so the bare overload is not the set
    /// claim it reads as; both sides are sorted ordinally first so the ordering is a property of the
    /// assertion rather than of the model's declaration order. Ordinal, because <c>string</c>
    /// equality is, and because <c>pg_class</c> keeps a name exactly as EF spells it.
    /// </para>
    /// <para>
    /// <b>This makes the case sensitive to anybody adding a constraint to <c>accounts</c>, and that
    /// is the intent rather than a cost to be engineered away.</b> A red here is a decision to
    /// re-approve, not a bug: the five names below are the whole of what stands over the table
    /// carrying the first column whose uniqueness survived sealing, and a sixth arriving unannounced
    /// is exactly the event somebody should have to look at. The remedy is to add the name and to
    /// know why — never to relax the comparison into a <c>Contains</c>, which would restore the hole
    /// the paragraph above describes.
    /// </para>
    /// <para>
    /// <b>A non-narrative column is pinned beside it on purpose.</b> <c>transactions.amount</c> is a
    /// <see langword="decimal" /> in <c>numeric(14,4)</c>, so the facts are pinned somewhere the
    /// sealed-column shape cannot be standing in for them — a walk that answered <c>byte[]</c> and
    /// <c>bytea</c> for everything would pass a pin over <c>accounts.name</c> alone. It also closes
    /// the limit this case's own remarks used to declare and no longer can in the same words: the
    /// three members that earn <see cref="StoredColumn" /> its existence are now held on two columns
    /// rather than on none.
    /// </para>
    /// <para>
    /// <b>What this still cannot see, said out loud.</b> The pin is two columns deep, not
    /// ninety-three: the provider type, the store type and the table's checks remain unasserted on
    /// every other column, so a walk handing <c>users.email</c> the wrong table's constraints passes
    /// this case and every other in the file. Two named columns are what a floor is worth here — the
    /// alternative is a second copy of the schema in a test file, which drifts and which
    /// <c>DataInventoryReconciliationTests</c> already keeps against the live catalog for the names.
    /// The honest scope of the title stands with one word added: the two walks describe the same
    /// schema, and agree about the facts of two columns of it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task StoredColumns_DescribeTheSameSchemaTheEnumeratorDoes()
    {
        // Arrange — the design-time model, read once and handed to both walks, so a difference
        // between them cannot be a difference between two models.
        IModel model = MappedSchema.DesignTimeModel();

        // Act
        IReadOnlyList<MappedColumn> mapped = MappedSchema.ColumnsOf(model);
        IReadOnlyList<StoredColumn> stored = MappedSchema.StoredColumnsOf(model);

        HashSet<string> mappedNames = new(
            mapped.Select(column => column.Qualified), StringComparer.Ordinal);
        HashSet<string> storedNames = new(
            stored.Select(column => column.Qualified), StringComparer.Ordinal);

        string[] enumeratedButNotStored =
        [
            .. mappedNames.Except(storedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal),
        ];
        string[] storedButNotEnumerated =
        [
            .. storedNames.Except(mappedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal),
        ];

        // Reported in full before the assertions: a failure here is a list of columns one of the two
        // walks stopped reaching, and a truncated list is a column somebody will miss.
        Console.WriteLine($"ColumnsOf: {mapped.Count} columns. StoredColumnsOf: {stored.Count}.");
        Console.WriteLine(
            $"Enumerated but not stored: {string.Join(", ", enumeratedButNotStored)}");
        Console.WriteLine(
            $"Stored but not enumerated: {string.Join(", ", storedButNotEnumerated)}");

        // Assert — the subject is non-empty and substantial first. Everything after this is a set
        // difference, and two empty walks agree with each other perfectly.
        await Assert.That(mapped.Count).IsGreaterThanOrEqualTo(MappedColumnFloor);
        await Assert.That(stored.Count).IsGreaterThanOrEqualTo(MappedColumnFloor);

        // Two named columns from opposite corners, so the floor cannot be met by a walk that reached
        // one large entity type. wrapped_account_keys is the newest table and maps through a value
        // converter; transactions is the largest and the leaf nothing references.
        await Assert.That(storedNames).Contains(RealNarrativeColumn);
        await Assert.That(storedNames).Contains("wrapped_account_keys.wrapped_private_key");

        // The columns the pinned walk reaches that the storage walk does not — the eighty-five-column
        // hole, in the direction that leaves FR-057 green while enumerating almost nothing.
        await Assert.That(enumeratedButNotStored).IsEmpty();

        // And the other direction: a storage walk claiming a column the pinned one does not reach is
        // a column outside everything the reconciliation against the live catalog covers.
        await Assert.That(storedButNotEnumerated).IsEmpty();

        // Counts next, because a duplicate is invisible to both differences above and is a defect
        // neither of them was written to catch.
        await Assert.That(stored.Count).IsEqualTo(mapped.Count);

        // And the facts of two columns, because everything above this line is about names and a walk
        // returning the right names with the wrong facts satisfies all of it. accounts.name is the
        // sealed one: byte[] to the provider, bytea in the store, and the five constraints that stand
        // over accounts and no others.
        StoredColumn accountsName = stored.Single(column => column.Qualified == "accounts.name");
        string[] accountsChecks =
        [
            .. accountsName.TableCheckConstraints
                .Select(check => check.Name)
                .Order(StringComparer.Ordinal),
        ];

        Console.WriteLine($"accounts.name checks: {string.Join(", ", accountsChecks)}");

        await Assert.That(accountsName.ProviderClrType).IsEqualTo(typeof(byte[]));
        await Assert.That(accountsName.StoreType).IsEqualTo("bytea");

        // Exact, ordered and ordinal — all three argued in the remarks. CollectionOrdering.Matching
        // is part of the assertion: IsEquivalentTo's default is CollectionOrdering.Any, so the bare
        // overload passes on every permutation and is not the set claim it reads as. Both sides are
        // sorted ordinally above, so what is compared is a set rendered in one agreed order.
        string[] expectedAccountsChecks =
        [
            "CK_accounts_name_key_length",
            "CK_accounts_name_length",
            "CK_accounts_name_version",
            "CK_accounts_opening_balance",
            "CK_accounts_type",
        ];
        await Assert.That(accountsChecks)
            .IsEquivalentTo(expectedAccountsChecks, CollectionOrdering.Matching);

        // A non-narrative column beside it, so the pin cannot be met by a walk that answers byte[]
        // and bytea to everything. What makes it a clean control is the column and not the table:
        // transactions carries three checks and two of them are about description, which is itself a
        // narrative column — so this is a plaintext column sitting on a table that does hold sealed
        // ones, which is the harder and the more useful control of the two.
        StoredColumn amount = stored.Single(column => column.Qualified == PlaintextColumn);
        string[] transactionChecks =
        [
            .. amount.TableCheckConstraints.Select(check => check.Name).Order(StringComparer.Ordinal),
        ];

        Console.WriteLine($"transactions.amount checks: {string.Join(", ", transactionChecks)}");

        await Assert.That(amount.ProviderClrType).IsEqualTo(typeof(decimal));
        await Assert.That(amount.StoreType).IsEqualTo("numeric(14,4)");
        await Assert.That(transactionChecks)
            .IsEquivalentTo(
                new[]
                {
                    "CK_transactions_amount",
                    "CK_transactions_description_length",
                    "CK_transactions_description_version",
                },
                CollectionOrdering.Matching);
    }

    /// <summary>
    /// The gate itself: every narrative column of the shipped schema is stored as ciphertext.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion FR-057 asks for, and it is the one that goes red when somebody adds a
    /// narrative column without sealing it, deletes one of its two check constraints, or drops the
    /// value converter that turns a <see cref="NarrativeField" /> into bytes. The remedy for a
    /// <see cref="EncryptionDefect" /> is named by its <see cref="EncryptionDefect.Kind" /> and is
    /// never to widen anything here: a provider type that is not <c>byte[]</c> is a lost converter,
    /// a store type that is not <c>bytea</c> is a <c>HasColumnType</c> call disagreeing with it, and
    /// a missing check is a deleted <c>HasCheckConstraint</c>.
    /// </para>
    /// <para>
    /// <b>Neither side is named in this file.</b> The schema comes from the model and the narrative
    /// set comes from <see cref="DataInventory" />, which
    /// <c>DataInventoryCoverageTests.Narrative_IsExactlyTheColumnsTypedForASealedValue</c> holds
    /// equal to the model's own <see cref="NarrativeField" />-typed properties in both directions.
    /// That is NFR-023 as a property of the arrangement rather than as a promise: a ninth narrative
    /// column is examined by this case the day it is typed, with nothing in this file edited.
    /// </para>
    /// <para>
    /// <b>The non-vacuity floor is a line in this case and used to be a case of its own, and folding
    /// it in was a demotion rather than a move.</b> <c>Gate_IsNonVacuous</c> recomputed the examined
    /// set beside the gate — narrative entries intersected with the schema side's names — so it said
    /// nothing whatever about <see cref="NarrativeEncryptionCoverage.Compare" />, and it survived the
    /// mutation its own file names as central, a <see cref="MappedSchema.StoredColumnsOf" /> dropping
    /// eighty-five of ninety-three columns. That mutation is
    /// <see cref="StoredColumns_DescribeTheSameSchemaTheEnumeratorDoes" />'s to catch and it now does.
    /// What is left here is read off <see cref="EncryptionCoverage.Unmapped" /> — the narrative
    /// entries the comparison did <i>not</i> reach — so the examined set is the gate's own answer
    /// rather than a second opinion about it.
    /// </para>
    /// <para>
    /// <b>The honest size of that claim, so nobody counts it twice.</b> Beside an empty
    /// <see cref="EncryptionCoverage.Unmapped" /> the floor reduces to a statement about the shipped
    /// inventory — that it still classifies at least six columns narrative and still names those two
    /// — and the narrative set is derived from the model's own types by
    /// <c>DataInventoryCoverageTests.Narrative_IsExactlyTheColumnsTypedForASealedValue</c> in both
    /// directions. It is a floor under the gate, not coverage of it, and it earns its line by living
    /// where the vacuity it is about would happen.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Narrative_ColumnsAreStoredAsCiphertext()
    {
        // Arrange — the shipped schema as the store sees it, against the shipped classifications.
        IReadOnlyList<StoredColumn> stored =
            MappedSchema.StoredColumnsOf(MappedSchema.DesignTimeModel());

        // Act
        EncryptionCoverage coverage =
            NarrativeEncryptionCoverage.Compare(stored, DataInventory.Entries);

        // Reported in full, and each defect carries both sides: a failure in anger has to say which
        // column, what was expected and what stood there instead, or it is a scavenger hunt across
        // eight configurations.
        Console.WriteLine(
            $"Narrative entries: {DataInventory.Of(ColumnClassification.Narrative).Count}. "
            + $"Stored columns: {stored.Count}.");
        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect =>
                    $"{defect.Qualified} {defect.Kind}: expected [{defect.Expected}], "
                    + $"found [{defect.Found}]")));
        Console.WriteLine($"Unmapped: {string.Join(", ", coverage.Unmapped)}");

        // The set the comparison ran its four checks over, read off its own output: a narrative entry
        // that is not Unmapped is one it reached. Computed from the result rather than recomputed
        // beside it, which is the difference between a floor under this gate and a second opinion.
        string[] examined =
        [
            .. DataInventory.Of(ColumnClassification.Narrative)
                .Select(entry => entry.Qualified)
                .Except(coverage.Unmapped, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Console.WriteLine($"Examined: {string.Join(", ", examined)}");

        // Assert — both inputs non-empty before anything is read off an empty comparison.
        await Assert.That(stored).IsNotEmpty();
        await Assert.That(DataInventory.Of(ColumnClassification.Narrative)).IsNotEmpty();

        // Enough narrative columns reached that the run cannot have covered one entity type, and two
        // of them named from opposite corners: transactions.description on the leaf nothing
        // references, budgets.name on the tenancy root and the one narrative name column with no
        // blind index beside it. A count alone is met by six columns of one region.
        await Assert.That(examined.Length).IsGreaterThanOrEqualTo(ExaminedNarrativeColumnFloor);
        await Assert.That(examined).Contains(RealNarrativeColumn);
        await Assert.That(examined).Contains(SecondRealNarrativeColumn);

        // FR-057 in one line: no narrative column of this schema is stored as anything but opaque,
        // version-pinned, length-bounded ciphertext.
        await Assert.That(coverage.Defects).IsEmpty();

        // And the gate's own blind spot is empty: a narrative entry naming no column in the schema
        // side is a column this comparison did not examine, which is how a green answer comes to
        // mean "found nothing to look at".
        await Assert.That(coverage.Unmapped).IsEmpty();
    }

    /// <summary>
    /// A narrative column the store would hand back as text is reported, by name, twice over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The permanent control the gate's success cannot be told from its absence without.</b>
    /// Two empty lists are what a complete schema produces and equally what a
    /// <see cref="NarrativeEncryptionCoverage.Compare" /> that reports nothing produces. This is the
    /// case that tells them apart, and it is where FR-057's second acceptance criterion lives: the
    /// report <b>names the column</b>.
    /// </para>
    /// <para>
    /// <b>The synthetic column carries both correct check constraints</b>, which is what makes the
    /// assertion an exact set rather than a <c>Contains</c>. Two defects and no more means the two
    /// predicates were matched — the positive half of the association rule, over an input this file
    /// wrote — so a matcher that had stopped recognising a correct predicate would redden here as
    /// well as in the three cases below it.
    /// </para>
    /// <para>
    /// A <see cref="string" /> provider type over a <c>text</c> column is the shape a lost value
    /// converter produces, and it is the defect that matters most: it is the only one of the four
    /// under which the server can read what somebody wrote.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Compare_AgainstAColumnStoredAsText_ReportsItByName()
    {
        // Arrange — a narrative column whose converter has gone: the provider is handed a string and
        // the column is declared text, with both of the sealed column's checks still standing.
        const string qualified = "probe.name";
        StoredColumn[] stored =
        [
            new StoredColumn(
                "probe",
                "name",
                typeof(string),
                typeof(string),
                "text",
                [
                    new StoredCheckConstraint("CK_probe_name_length", NameLengthCheck),
                    new StoredCheckConstraint("CK_probe_name_version", NameVersionCheck),
                ]),
        ];
        ColumnClassificationEntry[] inventory = [ColumnClassificationEntry.Narrative("probe", "name")];

        // Act
        EncryptionCoverage coverage = NarrativeEncryptionCoverage.Compare(stored, inventory);

        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect =>
                    $"{defect.Qualified} {defect.Kind}: expected [{defect.Expected}], "
                    + $"found [{defect.Found}]")));

        // Assert — the column is named, as table.column, which is FR-057's second criterion and the
        // whole of what a failure in anger has to tell somebody. Spelled as a Contains beside a
        // count rather than through IsEquivalentTo, whose default collection ordering is Any and
        // which is therefore not the set claim it reads as.
        string[] named =
        [
            .. coverage.Defects.Select(defect => defect.Qualified).Distinct(StringComparer.Ordinal),
        ];
        await Assert.That(named).Contains(qualified);
        await Assert.That(named.Length).IsEqualTo(1);

        // Both type defects, and only those. The two check kinds being absent is the positive half:
        // the two correct predicates written above were recognised.
        await Assert.That(coverage.Defects.Select(defect => defect.Kind))
            .Contains(EncryptionDefectKind.ProviderTypeIsNotBytes);
        await Assert.That(coverage.Defects.Select(defect => defect.Kind))
            .Contains(EncryptionDefectKind.StoreTypeIsNotOpaque);
        await Assert.That(coverage.Defects.Count).IsEqualTo(2);

        // And nothing landed in the other direction: the entry names a column the schema side holds.
        await Assert.That(coverage.Unmapped).IsEmpty();
    }

    /// <summary>
    /// A narrative column the model states no store type for is reported with an absence somebody can
    /// read, never a blank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This case replaces a line next door that could not fail, and the difference between the two
    /// is the whole of why it exists.</b>
    /// <see cref="Compare_AgainstAColumnStoredAsText_ReportsItByName" /> carried an assertion that no
    /// defect had a blank <see cref="EncryptionDefect.Expected" /> or
    /// <see cref="EncryptionDefect.Found" />. Under that case's fixture neither side can be blank
    /// under any implementation that has not already reddened the count and kind assertions above it
    /// — every string in play is either a rendered constant or a member the fixture filled with
    /// <c>text</c> — so the line was decoration counted as coverage. Measured.
    /// </para>
    /// <para>
    /// <b>The one shape where a blank is genuinely available is a <see langword="null" /> store
    /// type</b>, which <see cref="MappedSchema.StoredColumnsOf" /> calls "a branch waiting for a case"
    /// at its own declaration: every one of the ninety-three mapped columns answers a store type, so
    /// nothing in the model reaches it and it has only ever been exercised by a value built by hand.
    /// This is that value. A <see cref="NarrativeEncryptionCoverage" /> rendering
    /// <c>column.StoreType ?? string.Empty</c> reports <c>found []</c> and reddens here, which is the
    /// mutation the deleted line was reaching for and could not reach.
    /// </para>
    /// <para>
    /// The version and length checks are left standing, so the assertion is an exact set of one and
    /// the case is about the store-type arm alone.
    /// </para>
    /// <para>
    /// <b>The same defect was then found inside this case and the line is gone.</b> Of the two
    /// readability guards it shipped with, only the one over
    /// <see cref="EncryptionDefect.Found" /> can fail: <see cref="EncryptionDefect.Expected" /> is
    /// <see cref="NarrativeEncryptionCoverage.OpaqueStoreType" /> under this fixture and the exact
    /// equality three lines below says so, so no implementation reddens the blank-check while leaving
    /// the equality green. Removed rather than given a fixture, because a fixture that could make
    /// <see cref="EncryptionDefect.Expected" /> blank would be a case about the constant and not
    /// about this branch. The remedy for a decoration assertion is deletion or a different case, and
    /// the paragraph above had already said so about a line next door.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Compare_AgainstANarrativeColumnWithNoStoreType_ReportsTheAbsenceRatherThanABlank()
    {
        // Arrange — sealed bytes and both checks standing, and a model that states no store type.
        StoredColumn[] stored =
        [
            new StoredColumn(
                "probe",
                "name",
                typeof(NarrativeField),
                typeof(byte[]),
                null,
                [
                    new StoredCheckConstraint("CK_probe_name_length", NameLengthCheck),
                    new StoredCheckConstraint("CK_probe_name_version", NameVersionCheck),
                ]),
        ];
        ColumnClassificationEntry[] inventory = [ColumnClassificationEntry.Narrative("probe", "name")];

        // Act
        EncryptionCoverage coverage = NarrativeEncryptionCoverage.Compare(stored, inventory);

        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect =>
                    $"{defect.Qualified} {defect.Kind}: expected [{defect.Expected}], "
                    + $"found [{defect.Found}]")));

        // Assert — one defect, of the store-type kind, naming the column.
        await Assert.That(coverage.Defects.Count).IsEqualTo(1);
        await Assert.That(coverage.Defects[0].Qualified).IsEqualTo("probe.name");
        await Assert.That(coverage.Defects[0].Kind)
            .IsEqualTo(EncryptionDefectKind.StoreTypeIsNotOpaque);

        // The found side is readable. A defect reporting `found []` is a defect nobody can act on,
        // and a null store type is the one input under which the type's construction admits one.
        //
        // The expected side carried the same guard and it is deleted: under this fixture Expected is
        // the constant asserted three lines down, so no implementation could make the guard fail
        // while leaving the equality green — the exact "decoration counted as coverage" this case's
        // own remarks were written to argue against, sitting inside the case that argues it.
        await Assert.That(string.IsNullOrWhiteSpace(coverage.Defects[0].Found)).IsFalse();
        await Assert.That(coverage.Defects[0].Expected)
            .IsEqualTo(NarrativeEncryptionCoverage.OpaqueStoreType);
    }

    /// <summary>
    /// A narrative column whose version pin has been deleted is reported, by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real event is a <c>HasCheckConstraint</c> line removed from a configuration during a
    /// tidy-up, leaving a column that still holds bytes and no longer refuses a foreign framing. The
    /// bytes are correct here — <c>byte[]</c> over <c>bytea</c> — so the type pair passes and this
    /// case isolates the check half, which is what the enum's remarks say distinguishes a sealed
    /// column from a hashed one.
    /// </para>
    /// <para>
    /// The length check is left standing, so the assertion is an exact set of one. A control that
    /// removed both checks would be satisfied by an implementation that reported
    /// <see cref="EncryptionDefectKind.LengthCheckMissing" /> for every column with any check
    /// missing, and would say nothing about which of the two the gate can see.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Compare_AgainstAColumnMissingItsVersionCheck_ReportsItByName()
    {
        // Arrange — sealed bytes, the length band standing, the version pin gone.
        StoredColumn[] stored =
        [
            new StoredColumn(
                "probe",
                "name",
                typeof(NarrativeField),
                typeof(byte[]),
                "bytea",
                [new StoredCheckConstraint("CK_probe_name_length", NameLengthCheck)]),
        ];
        ColumnClassificationEntry[] inventory = [ColumnClassificationEntry.Narrative("probe", "name")];

        // Act
        EncryptionCoverage coverage = NarrativeEncryptionCoverage.Compare(stored, inventory);

        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect =>
                    $"{defect.Qualified} {defect.Kind}: expected [{defect.Expected}], "
                    + $"found [{defect.Found}]")));

        // Assert — one defect, of the one kind, naming the column.
        await Assert.That(coverage.Defects.Count).IsEqualTo(1);
        await Assert.That(coverage.Defects[0].Qualified).IsEqualTo("probe.name");
        await Assert.That(coverage.Defects[0].Kind)
            .IsEqualTo(EncryptionDefectKind.VersionCheckMissing);

        // The expectation names the predicate somebody has to restore, spelled the way the
        // configurations spell it. Without this the report says a check is missing and leaves the
        // reader to work out which of a column's two it meant.
        await Assert.That(coverage.Defects[0].Expected).IsEqualTo(NameVersionCheck);

        // And what stood there instead: the table's whole check list, which is how somebody sees
        // that the length band is present and the version pin is not.
        await Assert.That(coverage.Defects[0].Found).Contains("CK_probe_name_length");
    }

    /// <summary>
    /// A narrative column whose length band has been deleted is reported, by name.
    /// </summary>
    /// <remarks>
    /// The mirror of the case above, and the reason the two are separate: the length band is the
    /// check that stops a column accepting a three-byte value no version of this system can
    /// interpret, and the version pin is the one that stops it accepting a foreign framing of legal
    /// length. A single "a check is missing" report would send a reader to the wrong line.
    /// </remarks>
    [Test]
    public async Task Compare_AgainstAColumnMissingItsLengthBand_ReportsItByName()
    {
        // Arrange — sealed bytes, the version pin standing, the length band gone.
        StoredColumn[] stored =
        [
            new StoredColumn(
                "probe",
                "name",
                typeof(NarrativeField),
                typeof(byte[]),
                "bytea",
                [new StoredCheckConstraint("CK_probe_name_version", NameVersionCheck)]),
        ];
        ColumnClassificationEntry[] inventory = [ColumnClassificationEntry.Narrative("probe", "name")];

        // Act
        EncryptionCoverage coverage = NarrativeEncryptionCoverage.Compare(stored, inventory);

        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect =>
                    $"{defect.Qualified} {defect.Kind}: expected [{defect.Expected}], "
                    + $"found [{defect.Found}]")));

        // Assert — one defect, of the one kind, naming the column.
        await Assert.That(coverage.Defects.Count).IsEqualTo(1);
        await Assert.That(coverage.Defects[0].Qualified).IsEqualTo("probe.name");
        await Assert.That(coverage.Defects[0].Kind)
            .IsEqualTo(EncryptionDefectKind.LengthCheckMissing);

        // Both caps are offered, because which of the two a column earns is a judgement about field
        // class that the model states nothing a derivation could read it off — the limit the gate
        // declares about itself and the case below makes executable.
        await Assert.That(coverage.Defects[0].Expected).Contains("1024");
        await Assert.That(coverage.Defects[0].Expected).Contains("2560");
    }

    /// <summary>
    /// The <c>name_key</c> trap, executable: a table offering only the blind index's constraint
    /// still owes its narrative column a length band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case the association rule exists to survive, and it is the one a later
    /// simplification will fail.</b> Four tables carry a <c>name_key</c> beside their narrative
    /// <c>name</c>. <c>CK_accounts_name_key_length</c> is <c>length(name_key) = 32</c>: its
    /// predicate mentions <c>name</c>, and its <i>name</i> begins with <c>CK_accounts_name</c>. So
    /// both of the association rules a reader reaches for first — SQL substring, constraint-name
    /// prefix — hand the digest's constraint to the narrative column, and under either of them
    /// deleting the real <c>CK_accounts_name_length</c> leaves a constraint "about name" standing
    /// and the gate green. That is the exact failure FR-057 exists to make impossible.
    /// </para>
    /// <para>
    /// The arrangement is the real one with one constraint removed: the true names, the true
    /// predicate for the digest, the version pin left in place. A substring or prefix matcher passes
    /// this case only by being wrong, and the version pin standing is what proves the run is not
    /// simply reporting everything.
    /// </para>
    /// <para>
    /// It is also, incidentally, the only place a <i>nearly</i> right predicate has to be
    /// <b>refused</b>. The three cases above hand the matcher predicates that are either exactly
    /// right or absent, which a substring matcher survives, and
    /// <see cref="Compare_AgainstColumnsCarryingTheOtherFieldClassesCap_AcceptsBoth" /> below hands
    /// it two more that are nearly right — a swapped cap — to demonstrate the opposite verdict, that
    /// they are accepted.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Compare_GivenOnlyTheBlindIndexConstraint_StillReportsTheNarrativeColumn()
    {
        // Arrange — accounts as it would be with CK_accounts_name_length deleted: sealed bytes, the
        // version pin standing, and the blind index's own length constraint left beside it.
        StoredColumn[] stored =
        [
            new StoredColumn(
                "accounts",
                "name",
                typeof(NarrativeField),
                typeof(byte[]),
                "bytea",
                [
                    new StoredCheckConstraint("CK_accounts_name_version", NameVersionCheck),
                    new StoredCheckConstraint(
                        "CK_accounts_name_key_length",
                        "length(name_key) = 32"),
                ]),
        ];
        ColumnClassificationEntry[] inventory =
            [ColumnClassificationEntry.Narrative("accounts", "name")];

        // Act
        EncryptionCoverage coverage = NarrativeEncryptionCoverage.Compare(stored, inventory);

        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect =>
                    $"{defect.Qualified} {defect.Kind}: expected [{defect.Expected}], "
                    + $"found [{defect.Found}]")));

        // Assert — the deleted band is reported. A matcher keying on "a constraint whose SQL mentions
        // name" or "a constraint whose name starts with CK_accounts_name" answers that one is present
        // and this assertion is what notices. Those two coarse matchers are caught by
        // Compare_AgainstAColumnMissingItsLengthBand_ReportsItByName as well — its surviving version
        // check mentions name and is named CK_probe_name_version. What is caught here and nowhere
        // else is a matcher keying on the substring "length(name", which the constraint left standing
        // on this table answers and that case's does not.
        await Assert.That(coverage.Defects.Count).IsEqualTo(1);
        await Assert.That(coverage.Defects[0].Qualified).IsEqualTo("accounts.name");
        await Assert.That(coverage.Defects[0].Kind)
            .IsEqualTo(EncryptionDefectKind.LengthCheckMissing);

        // The report shows the reader what stood in the deleted constraint's place, which is the
        // whole of what makes the failure diagnosable rather than confusing: the table does carry a
        // constraint with "name" and "length" in it, and it is the wrong one.
        await Assert.That(coverage.Defects[0].Found).Contains("CK_accounts_name_key_length");
    }

    /// <summary>
    /// An inventory that classifies nothing narrative asks nothing of a schema, however wrong the
    /// schema is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FR-057 is a rule about narrative columns and about no others. This case hands the comparison
    /// the whole real schema with every column classified <i>arithmetic</i>, and demands silence:
    /// eighty-five plaintext columns, no defects, no unmapped entries.
    /// </para>
    /// <para>
    /// <b>What it catches is the classification filter going away.</b> An implementation that read
    /// the inventory as a list of columns rather than as classifications would demand ciphertext of
    /// <c>transactions.amount</c> and of ninety-one others — loud, but loud in a way that teaches
    /// the next reader to widen the gate, which is why
    /// <see cref="NarrativeEncryptionCoverage.Compare" /> filters inside rather than trusting a
    /// caller to pre-filter.
    /// </para>
    /// <para>
    /// <b>An <i>empty</i> inventory was the obvious shape here and is strictly weaker, so it is not
    /// written.</b> Against an empty list the honest implementation and one hardwired to
    /// <see cref="DataInventory.Entries" /> both answer with two empty lists, and so does one with
    /// the classification filter removed — an empty narrative side has nothing to check under any of
    /// the three. The arithmetic version reddens on the filter and carries the same statement about
    /// vacuity. The hardwiring control is a separate case, below, because no argument that has the
    /// gate agreeing with the shipped inventory can be the one that catches it reading it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Compare_AgainstAnInventoryClassifyingNothingNarrative_ReportsNothing()
    {
        // Arrange — the real schema, every column of it classified arithmetic. Built from the model
        // rather than by hand, so all ninety-odd columns are eligible to be reported and none may be.
        // Arithmetic rather than Excluded so that no invented exclusion reason appears in this file
        // for somebody to copy into the real inventory later.
        IReadOnlyList<StoredColumn> stored =
            MappedSchema.StoredColumnsOf(MappedSchema.DesignTimeModel());
        ColumnClassificationEntry[] nothingNarrative =
        [
            .. stored.Select(column =>
                ColumnClassificationEntry.Arithmetic(column.Table, column.Column)),
        ];

        // Act
        EncryptionCoverage coverage =
            NarrativeEncryptionCoverage.Compare(stored, nothingNarrative);

        Console.WriteLine(
            $"Classified arithmetic: {nothingNarrative.Length}. Defects: {coverage.Defects.Count}. "
            + $"Unmapped: {coverage.Unmapped.Count}.");
        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect => $"{defect.Qualified} {defect.Kind}")));

        // Assert — the subject is substantial first, or "it asked nothing of nothing" is the reading.
        await Assert.That(nothingNarrative.Length).IsGreaterThanOrEqualTo(MappedColumnFloor);

        // Nothing is demanded of a column nobody called narrative, including of the eight that are
        // narrative in the shipped inventory and are not in this one.
        await Assert.That(coverage.Defects).IsEmpty();

        // And no entry is unmapped: every one of them names a column the schema side holds, so the
        // silence above is silence about columns that were there rather than about columns that
        // were not.
        await Assert.That(coverage.Unmapped).IsEmpty();
    }

    /// <summary>
    /// The comparison reads the inventory it was handed, never the shipped one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case a hardwired implementation reddens on, and working out its shape took
    /// some care.</b> The precedent next door,
    /// <c>DataInventoryCoverageTests.Inventory_AgainstAnEmptyClassification_ReportsEveryMappedColumn</c>,
    /// works because the honest comparison reports ninety-three columns where a hardwired one
    /// reports none. The same trick does not transfer: an empty inventory produces two empty lists
    /// under the honest implementation too, because there is nothing narrative to check. The
    /// distinguishing input has to be an inventory that <b>disagrees with the shipped one about a
    /// real column</b> — and it has to disagree in the direction that produces defects, because the
    /// shipped inventory's own verdict on this schema is silence.
    /// </para>
    /// <para>
    /// So: <c>transactions.amount</c>, a real mapped column the shipped inventory classifies
    /// arithmetic, classified narrative here and nowhere else. The honest comparison reports it four
    /// times over — a <see langword="decimal" /> in a <c>numeric</c> column on a table whose only
    /// narrative checks are written about <c>description</c>. An implementation reaching for
    /// <see cref="DataInventory.Entries" /> reports nothing at all, because by that list this schema
    /// is perfect.
    /// </para>
    /// <para>
    /// <b>The negative half is the sharper of the two.</b> The synthetic inventory classifies
    /// <i>only</i> that one column narrative, so the eight real narrative columns must not be
    /// examined at all — and if a defect ever appeared against one of them it could only have come
    /// from a list this case did not pass in. Nothing here asserts they are clean; that is the
    /// gate's job, and repeating it would make this case pass for the gate's reason.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Compare_AgainstAnInventoryNamingAPlaintextColumnNarrative_ReportsIt()
    {
        // Arrange — the real schema against one classification the shipped inventory does not make.
        IReadOnlyList<StoredColumn> stored =
            MappedSchema.StoredColumnsOf(MappedSchema.DesignTimeModel());
        ColumnClassificationEntry[] inventory =
            [ColumnClassificationEntry.Narrative("transactions", "amount")];

        // Act
        EncryptionCoverage coverage = NarrativeEncryptionCoverage.Compare(stored, inventory);

        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect =>
                    $"{defect.Qualified} {defect.Kind}: expected [{defect.Expected}], "
                    + $"found [{defect.Found}]")));

        // Assert — an ordinary numeric column held to FR-057 fails all four checks, and the report
        // names it. A hardwired comparison answers this with an empty list.
        EncryptionDefectKind[] kinds =
        [
            .. coverage.Defects
                .Where(defect => defect.Qualified == PlaintextColumn)
                .Select(defect => defect.Kind),
        ];
        await Assert.That(kinds).Contains(EncryptionDefectKind.ProviderTypeIsNotBytes);
        await Assert.That(kinds).Contains(EncryptionDefectKind.StoreTypeIsNotOpaque);
        await Assert.That(kinds).Contains(EncryptionDefectKind.VersionCheckMissing);
        await Assert.That(kinds).Contains(EncryptionDefectKind.LengthCheckMissing);

        // And the columns the shipped inventory calls narrative were not examined, because this
        // inventory does not name them. A defect against one of them could only have come from a
        // list that was never passed in.
        await Assert.That(coverage.Defects.Select(defect => defect.Qualified).Distinct())
            .DoesNotContain(RealNarrativeColumn);
        await Assert.That(coverage.Defects.Select(defect => defect.Qualified).Distinct())
            .DoesNotContain(SecondRealNarrativeColumn);

        // Nothing unmapped: the one entry names a column the schema side holds, so the four defects
        // above are about a column that was examined rather than about one that was missed.
        await Assert.That(coverage.Unmapped).IsEmpty();
    }

    /// <summary>
    /// A narrative entry naming a column the schema side does not hold is reported as unmapped, not
    /// dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other direction, and it is the direction under which a green gate means "found nothing to
    /// look at". A column removed from a configuration with its classification left behind — or an
    /// enumerator that quietly stopped reaching a table — takes its own check with it, and
    /// <see cref="EncryptionCoverage.Defects" /> stays empty while the column goes unexamined.
    /// <see cref="EncryptionCoverage.Unmapped" /> is what puts that in the gate's own output rather
    /// than depending on somebody having run the coverage comparison first.
    /// </para>
    /// <para>
    /// <b>It doubles as the schema-side hardwiring control, which is why the schema is the real one
    /// with a column removed rather than an empty list.</b> An empty schema side would report all
    /// eight and prove the direction works; it would not prove the comparison read the list it was
    /// given, because a <see cref="NarrativeEncryptionCoverage.Compare" /> that built its own model
    /// from <see cref="MappedSchema" /> and ignored its argument reports <i>nothing</i> here — the
    /// removed column is present in the model it would build. The single removal is what makes the
    /// two answers differ.
    /// </para>
    /// <para>
    /// One column removed and not eight, so the negative half means something: the seven survivors
    /// must not be reported, which a comparison that emptied its report into
    /// <see cref="EncryptionCoverage.Unmapped" /> whenever anything disagreed would fail.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Compare_ForANarrativeEntryTheModelNoLongerMaps_ReportsItUnmapped()
    {
        // Arrange — the shipped inventory against the real schema with one narrative column taken
        // out of the walk, which is what a configuration losing that property would look like here.
        IReadOnlyList<StoredColumn> stored =
        [
            .. MappedSchema.StoredColumnsOf(MappedSchema.DesignTimeModel())
                .Where(column => column.Qualified != RealNarrativeColumn),
        ];

        // Act
        EncryptionCoverage coverage =
            NarrativeEncryptionCoverage.Compare(stored, DataInventory.Entries);

        Console.WriteLine($"Stored columns after the removal: {stored.Count}.");
        Console.WriteLine($"Unmapped: {string.Join(", ", coverage.Unmapped)}");
        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect => $"{defect.Qualified} {defect.Kind}")));

        // Assert — the subject is still the schema, so this is a schema missing one column rather
        // than an empty list agreeing with everything.
        await Assert.That(stored.Count).IsGreaterThanOrEqualTo(MappedColumnFloor);

        // The entry naming no column is reported, by name, so a failure in anger says which
        // classification to reconcile.
        await Assert.That(coverage.Unmapped).Contains(RealNarrativeColumn);

        // And its seven neighbours are not, so this cannot be passing on a comparison that reports
        // the whole narrative set whenever anything is missing. Named rather than counted, from the
        // far corner of the schema.
        await Assert.That(coverage.Unmapped).DoesNotContain(SecondRealNarrativeColumn);
        await Assert.That(coverage.Unmapped.Count).IsEqualTo(1);

        // The defect direction stays clean: removing a column from the walk removes nothing from the
        // seven that are still there and still sealed.
        await Assert.That(coverage.Defects).IsEmpty();
    }

    /// <summary>
    /// A written-down limit, made executable: the gate accepts either cap on either field class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This case exists to make a stated gap visible rather than to bless it</b>, which is the
    /// call <c>EnvelopeBudgetingIsolationTests.Detector_IsBlindToATypeOutsideTheNamespace</c> makes
    /// and argues in the same terms. <see cref="NarrativeEncryptionCoverage" /> says of itself that
    /// a column's cap is not checked at all — not its value, not its direction, not its agreement
    /// with the other columns of its class — because which of the two
    /// <see cref="NarrativeFieldLimits" /> numbers a column earns is a judgement about field class
    /// and the model states nothing a derivation could read it off. A length check is therefore
    /// accepted when it names <i>either</i>.
    /// </para>
    /// <para>
    /// <b>Both directions, because they are two different accidents.</b> A name column widened to
    /// <see cref="NarrativeFieldLimits.DescriptionBytes" /> is a silent loosening: the column
    /// quietly accepts two and a half times what its class allows. A description narrowed to
    /// <see cref="NarrativeFieldLimits.NameBytes" /> is a silent refusal, which is the worse of the
    /// two — a note that stored yesterday answers <c>23514</c> today and nothing here says a word.
    /// Writing only one of them would leave a reader thinking the gate had an opinion about
    /// direction.
    /// </para>
    /// <para>
    /// <b>What a red on the accepting half means is that somebody closed the limit, and the remedy
    /// is then to delete this case.</b> That is the one mutation that half responds to, and it is a
    /// fix rather than a regression — which is unusual enough to say out loud. The negative half
    /// beneath it answers a different mutation and asks for the opposite remedy: a matcher that
    /// stopped recognising a length band at all reddens there, and that is a defect to repair. It
    /// is kept because the alternative is a limit that
    /// exists only in a remark: the two swaps below are the ones a later reader will make while
    /// tidying a configuration, and finding them accepted here, with the remedy written beside them,
    /// is cheaper than finding out from a <c>23514</c> in production. The remedy is never to widen
    /// anything: review holds which cap a column earns, in the same place it holds which of the
    /// three words a column earns.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Compare_AgainstColumnsCarryingTheOtherFieldClassesCap_AcceptsBoth()
    {
        // Arrange — two correctly sealed columns whose length bands have been swapped between the
        // two field classes. Everything else about them is right.
        StoredColumn[] stored =
        [
            new StoredColumn(
                "probe",
                "name",
                typeof(NarrativeField),
                typeof(byte[]),
                "bytea",
                [
                    new StoredCheckConstraint(
                        "CK_probe_name_length",
                        "length(name) between 29 and 2560"),
                    new StoredCheckConstraint("CK_probe_name_version", NameVersionCheck),
                ]),
            new StoredColumn(
                "probe",
                "description",
                typeof(NarrativeField),
                typeof(byte[]),
                "bytea",
                [
                    new StoredCheckConstraint(
                        "CK_probe_description_length",
                        "length(description) between 29 and 1024"),
                    new StoredCheckConstraint(
                        "CK_probe_description_version",
                        @"substring(description from 1 for 1) = '\x01'::bytea"),
                ]),
        ];
        ColumnClassificationEntry[] inventory =
        [
            ColumnClassificationEntry.Narrative("probe", "name"),
            ColumnClassificationEntry.Narrative("probe", "description"),
        ];

        // Act
        EncryptionCoverage coverage = NarrativeEncryptionCoverage.Compare(stored, inventory);

        Console.WriteLine(
            "Defects: "
            + string.Join(
                " | ",
                coverage.Defects.Select(defect =>
                    $"{defect.Qualified} {defect.Kind}: expected [{defect.Expected}], "
                    + $"found [{defect.Found}]")));

        // Assert — both swaps are accepted. This is the limit, not the requirement: a name column
        // admitting 2560 bytes and a description column refusing anything over 1024 are both green
        // here, and nothing in this tier can tell either of them from a correct configuration. If
        // this case is red, the gate has learned to read field class and this case is what to delete.
        await Assert.That(coverage.Defects).IsEmpty();

        // Pointed at the same two columns with no length constraint at all, the gate does object —
        // so what is demonstrated above is a blind spot about the cap and not a matcher that stopped
        // recognising a length band altogether.
        StoredColumn[] withNoBand =
        [
            .. stored.Select(column => column with
            {
                TableCheckConstraints =
                [
                    .. column.TableCheckConstraints.Where(check =>
                        !check.Name.EndsWith("_length", StringComparison.Ordinal)),
                ],
            }),
        ];
        EncryptionCoverage withoutTheBand =
            NarrativeEncryptionCoverage.Compare(withNoBand, inventory);

        Console.WriteLine(
            "Without any length band: "
            + string.Join(
                " | ",
                withoutTheBand.Defects.Select(defect => $"{defect.Qualified} {defect.Kind}")));

        await Assert.That(withoutTheBand.Defects.Select(defect => defect.Kind))
            .Contains(EncryptionDefectKind.LengthCheckMissing);
        await Assert.That(withoutTheBand.Defects.Count).IsEqualTo(2);
    }
}
