using Infrastructure.Persistence.Inventory;
using Infrastructure.Persistence.Provisioning;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The second axis on the inventory: whose rows each mapped table holds, which is what the erasure
/// coverage gate reads to know which tables an erasure has to reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three words, and the third is a decision, not a gap.</b> <see cref="OwnedBy.Nobody" /> carries
/// a written reason; a table nobody decided about is <i>absent</i> from the list and comes back
/// <see cref="OwnershipCoverage.Undecided" />. That split is why this list does not reuse
/// <see cref="TableOwnership" />, whose <c>None</c> means "undecided".
/// </para>
/// <para>
/// <b><see cref="TableOwnerCoverage" /> takes both lists as parameters</b>, for the reason
/// <c>DataInventoryCoverageTests</c> gives: the controls below hand it lists the shipped one is not,
/// and an implementation reading <see cref="DataInventory.TableOwners" /> instead answers them with the
/// shipped verdict.
/// </para>
/// <para>
/// <b>Order is pinned where it is defined, and only there.</b> Both <see cref="OwnershipCoverage" />
/// lists are ordinal-sorted and distinct, asserted with <see cref="CollectionOrdering.Matching" />:
/// <c>IsEquivalentTo</c>'s default ignores order, and <c>IsEqualTo</c> over two lists compares
/// references. <see cref="TableOwnerCoverage.DisagreementsWith" /> lines are judged by count and by
/// the table they name, never by their wording.
/// </para>
/// <para>
/// Offenders are asserted as collections. TUnit's string assertions truncate.
/// </para>
/// </remarks>
public sealed class TableOwnerCoverageTests
{
    /// <summary>
    /// The floor the shipped list and the mapped-table count must clear before an empty offender
    /// list means anything. A floor, never an exact count, for the reason <c>MappedSchemaTests</c>
    /// gives: an exact number goes red on every legitimate table anybody adds.
    /// </summary>
    /// <remarks>
    /// Ten, half of today's twenty, so it catches an emptied list or a one-entity walk and never a
    /// single deleted entry — at twenty it went red first on a deletion and hid the assertion that
    /// names the table.
    /// </remarks>
    private const int TableFloor = 10;

    /// <summary>
    /// The shortest a reason for owning nobody may be — the same floor the inventory holds an
    /// exclusion to. It makes writing nothing impossible and judges nothing else.
    /// </summary>
    private const int MinimumNobodyReasonLength = 80;

    /// <summary>A table no configuration maps, for the stale-direction controls.</summary>
    private const string FictitiousTable = "table_owner_probe_no_such_table";

    /// <summary>The table the hand-built disagreement controls judge.</summary>
    /// <remarks>
    /// Shares no substring with <see cref="NeighbourTable" />, so a line that names one cannot be
    /// read as naming the other.
    /// </remarks>
    private const string ProbeTable = "owner_probe";

    /// <summary>An agreeing table beside the probe, which no line may name.</summary>
    private const string NeighbourTable = "bystander";

    /// <summary>A reason long enough that no future floor in a factory refuses it.</summary>
    private const string ProbeReason =
        "A probe table built by this test file. It holds no row of any account, and no column naming "
        + "one, and it exists only so the comparison has something to judge.";

    // Compare: the undecided direction

    [Test]
    public async Task Compare_AgainstAnEmptyOwnerList_ReportsEveryMappedTableUndecided()
    {
        // Arrange — the real model against an owner list naming nothing.
        IReadOnlyList<string> mapped = MappedSchema.TablesOf(MappedSchema.DesignTimeModel());
        IReadOnlyList<TableOwnerEntry> nobodyDecided = [];
        string[] expected = [.. mapped.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // Act
        OwnershipCoverage coverage = TableOwnerCoverage.Compare(mapped, nobodyDecided);

        Console.WriteLine($"Undecided: {string.Join(", ", coverage.Undecided)}");

        // Assert — the exact set, ordinal-sorted. An implementation falling back to the shipped list
        // reports nothing here; one reaching part of the model reports too little.
        await Assert.That(mapped.Count).IsGreaterThanOrEqualTo(TableFloor);
        await Assert.That(coverage.Undecided)
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(coverage.Stale).IsEmpty();
    }

    [Test]
    public async Task Compare_WithMappedTablesOutOfOrder_ReportsUndecidedOrdinalSorted()
    {
        // Arrange — capitals sort before lower case ordinally, and not culturally.
        IReadOnlyList<string> mapped = ["transactions", "accounts", "Zeta", "budgets"];

        // Act
        OwnershipCoverage coverage = TableOwnerCoverage.Compare(mapped, []);

        // Assert
        await Assert.That(coverage.Undecided)
            .IsEquivalentTo(["Zeta", "accounts", "budgets", "transactions"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Compare_WithATableMappedTwice_ReportsItUndecidedOnce()
    {
        // Arrange
        IReadOnlyList<string> mapped = ["payees", "accounts", "payees"];

        // Act
        OwnershipCoverage coverage = TableOwnerCoverage.Compare(mapped, []);

        // Assert
        await Assert.That(coverage.Undecided)
            .IsEquivalentTo(["accounts", "payees"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Compare_WithEveryTableDecidedButOne_ReportsOnlyThatOne()
    {
        // Arrange — the decided tables are real ones, so a Compare that reported the whole schema
        // whenever anything disagreed is caught as surely as one that reported nothing.
        IReadOnlyList<string> mapped = MappedSchema.TablesOf(MappedSchema.DesignTimeModel());
        string undecided = "transactions";
        TableOwnerEntry[] owners =
        [
            .. mapped
                .Where(table => !string.Equals(table, undecided, StringComparison.Ordinal))
                .Select(table => TableOwnerEntry.User(table, "user_id")),
        ];

        // Act
        OwnershipCoverage coverage = TableOwnerCoverage.Compare(mapped, owners);

        // Assert
        await Assert.That(mapped).Contains(undecided);
        await Assert.That(coverage.Undecided)
            .IsEquivalentTo([undecided], CollectionOrdering.Matching);
        await Assert.That(coverage.Stale).IsEmpty();
    }

    // Compare: the stale direction

    [Test]
    public async Task Compare_ForAnEntryNamingNoMappedTable_ReportsItStale()
    {
        // Arrange — every mapped table decided, plus one entry naming a table that never existed.
        IReadOnlyList<string> mapped = MappedSchema.TablesOf(MappedSchema.DesignTimeModel());
        TableOwnerEntry[] owners =
        [
            .. mapped.Select(table => TableOwnerEntry.User(table, "user_id")),
            TableOwnerEntry.Nobody(FictitiousTable, ProbeReason),
        ];

        // Act
        OwnershipCoverage coverage = TableOwnerCoverage.Compare(mapped, owners);

        Console.WriteLine($"Stale: {string.Join(", ", coverage.Stale)}");

        // Assert — exactly the orphan, and the undecided direction undisturbed.
        await Assert.That(coverage.Stale)
            .IsEquivalentTo([FictitiousTable], CollectionOrdering.Matching);
        await Assert.That(coverage.Undecided).IsEmpty();
    }

    [Test]
    public async Task Compare_WithSeveralStaleEntriesOutOfOrder_ReportsThemOrdinalSorted()
    {
        // Arrange
        IReadOnlyList<string> mapped = ["accounts"];
        TableOwnerEntry[] owners =
        [
            TableOwnerEntry.Budget("accounts", "budget_id"),
            TableOwnerEntry.User("zz_gone", "user_id"),
            TableOwnerEntry.Budget("aa_gone", "budget_id"),
            TableOwnerEntry.Nobody("Mm_gone", ProbeReason),
        ];

        // Act
        OwnershipCoverage coverage = TableOwnerCoverage.Compare(mapped, owners);

        // Assert
        await Assert.That(coverage.Stale)
            .IsEquivalentTo(["Mm_gone", "aa_gone", "zz_gone"], CollectionOrdering.Matching);
        await Assert.That(coverage.Undecided).IsEmpty();
    }

    [Test]
    public async Task Compare_WithAnEntryDifferingOnlyInCase_ReportsBothDirections()
    {
        // Arrange — pg_class keeps the identifier exactly as EF spells it, so `Users` is not `users`.
        IReadOnlyList<string> mapped = ["users"];
        TableOwnerEntry[] owners = [TableOwnerEntry.User("Users", "id")];

        // Act
        OwnershipCoverage coverage = TableOwnerCoverage.Compare(mapped, owners);

        // Assert
        await Assert.That(coverage.Undecided).IsEquivalentTo(["users"], CollectionOrdering.Matching);
        await Assert.That(coverage.Stale).IsEquivalentTo(["Users"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Compare_WithATableDecidedTwice_ReportsNeitherDirection()
    {
        // Arrange — a duplicate is a set-comparison blind spot by nature; the shipped list's own
        // duplicate test below is what holds it. This pins that Compare does not invent a verdict.
        IReadOnlyList<string> mapped = ["accounts", "users"];
        TableOwnerEntry[] owners =
        [
            TableOwnerEntry.Budget("accounts", "budget_id"),
            TableOwnerEntry.User("users", "id"),
            TableOwnerEntry.Budget("accounts", "budget_id"),
        ];

        // Act
        OwnershipCoverage coverage = TableOwnerCoverage.Compare(mapped, owners);

        // Assert
        await Assert.That(coverage.Undecided).IsEmpty();
        await Assert.That(coverage.Stale).IsEmpty();
    }

    // The shipped list

    /// <summary>
    /// The gate: every table the model maps has a written-down owner, and every owner names a table
    /// the model still maps.
    /// </summary>
    /// <remarks>
    /// The floor comes first, because both checks are set differences and an emptied list against an
    /// inert comparison is green in both.
    /// </remarks>
    [Test]
    public async Task TableOwners_DecideEveryTableTheModelMaps()
    {
        // Arrange
        IReadOnlyList<string> mapped = MappedSchema.TablesOf(MappedSchema.DesignTimeModel());

        // Act
        OwnershipCoverage coverage = TableOwnerCoverage.Compare(mapped, DataInventory.TableOwners);

        Console.WriteLine(
            $"Mapped tables: {mapped.Count}. Owner entries: {DataInventory.TableOwners.Count}.");
        Console.WriteLine($"Undecided: {string.Join(", ", coverage.Undecided)}");
        Console.WriteLine($"Stale: {string.Join(", ", coverage.Stale)}");

        // Assert
        await Assert.That(mapped.Count).IsGreaterThanOrEqualTo(TableFloor);
        await Assert.That(DataInventory.TableOwners.Count).IsGreaterThanOrEqualTo(TableFloor);
        await Assert.That(coverage.Undecided).IsEmpty();
        await Assert.That(coverage.Stale).IsEmpty();
    }

    [Test]
    public async Task TableOwners_NameEachTableAtMostOnce()
    {
        // Arrange
        IReadOnlyList<TableOwnerEntry> owners = DataInventory.TableOwners;

        // Act — ordinal, so `Users` and `users` would be two names, as they are to pg_class.
        string[] duplicated =
        [
            .. owners
                .GroupBy(entry => entry.Table, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group =>
                    $"{group.Key} named {group.Count()} times, as "
                    + $"{string.Join(" and ", group.Select(entry => entry.Owner))}")
                .Order(StringComparer.Ordinal),
        ];

        // Assert — non-empty first: an empty list duplicates nothing.
        await Assert.That(owners.Count).IsGreaterThanOrEqualTo(TableFloor);
        await Assert.That(duplicated).IsEmpty();
    }

    [Test]
    public async Task TableOwners_EachOwnerColumnIsAMappedColumnOfItsOwnTable()
    {
        // Arrange — qualified, so `budget_id` on a table that does not carry it is not rescued by
        // another table that does.
        HashSet<string> mappedColumns = new(
            MappedSchema.ColumnsOf(MappedSchema.DesignTimeModel()).Select(column => column.Qualified),
            StringComparer.Ordinal);
        TableOwnerEntry[] owned =
        [
            .. DataInventory.TableOwners.Where(entry => entry.Owner is not OwnedBy.Nobody),
        ];

        // Act
        string[] unmapped =
        [
            .. owned
                .Select(entry => $"{entry.Table}.{entry.OwnerColumn}")
                .Where(qualified => !mappedColumns.Contains(qualified))
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(owned).IsNotEmpty();
        await Assert.That(unmapped).IsEmpty();
    }

    [Test]
    public async Task TableOwners_EachOwnerColumnIsTheColumnItsKindNames()
    {
        // Arrange — pinned by kind, not only "is a mapped column", because the erasure gate scopes
        // its row counts by this column: a mapped-but-wrong one like transactions.account_id passes
        // every other check here and the catalog comparison, and the gate then counts the wrong rows.
        IReadOnlyList<TableOwnerEntry> owners = DataInventory.TableOwners;

        // Act
        string[] wrongColumn =
        [
            .. owners
                .Where(entry => entry.Owner is not OwnedBy.Nobody)
                .Where(entry => !string.Equals(
                    entry.OwnerColumn,
                    ExpectedOwnerColumn(entry),
                    StringComparison.Ordinal))
                .Select(entry =>
                    $"{entry.Table}.{entry.OwnerColumn} ({entry.Owner}, expected "
                    + $"{ExpectedOwnerColumn(entry)})")
                .Order(StringComparer.Ordinal),
        ];

        Console.WriteLine($"Wrong owner column: {string.Join(" | ", wrongColumn)}");

        // Assert — non-empty first: an empty list names no wrong column either.
        await Assert.That(owners.Count).IsGreaterThanOrEqualTo(TableFloor);
        await Assert.That(wrongColumn).IsEmpty();
    }

    [Test]
    public async Task TableOwners_EachNobodyStatesAReasonSomebodyCanArgueWith()
    {
        // Arrange
        TableOwnerEntry[] ownedByNobody =
        [
            .. DataInventory.TableOwners.Where(entry => entry.Owner is OwnedBy.Nobody),
        ];

        // Act
        string[] tooShort =
        [
            .. ownedByNobody
                .Where(entry => string.IsNullOrWhiteSpace(entry.OwnedByNobodyBecause)
                                || entry.OwnedByNobodyBecause.Length < MinimumNobodyReasonLength)
                .Select(entry => entry.Table)
                .Order(StringComparer.Ordinal),
        ];

        // Assert — non-empty first: two tables own nobody by decision today, and a list that
        // dropped both would pass the floor below with nothing to judge.
        await Assert.That(ownedByNobody).IsNotEmpty();
        await Assert.That(tooShort).IsEmpty();
    }

    [Test]
    public async Task TableOwners_OwnedEntriesCarryAColumnAndNobodyEntriesAReason()
    {
        // Arrange
        IReadOnlyList<TableOwnerEntry> owners = DataInventory.TableOwners;

        // Act — the two shapes the factories promise, read back off every entry.
        string[] misshapen =
        [
            .. owners
                .Where(entry => entry.Owner switch
                {
                    OwnedBy.User or OwnedBy.Budget =>
                        entry.OwnerColumn is null || entry.OwnedByNobodyBecause is not null,
                    OwnedBy.Nobody =>
                        entry.OwnerColumn is not null || entry.OwnedByNobodyBecause is null,
                    // A word added to OwnedBy has no shape here yet, so it stops the test loudly
                    // rather than being judged by a rule written for the other three.
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(entry), entry.Owner, $"{entry.Table} carries an owner this test does not know."),
                })
                .Select(entry => $"{entry.Table} ({entry.Owner})")
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(owners.Count).IsGreaterThanOrEqualTo(TableFloor);
        await Assert.That(misshapen).IsEmpty();
    }

    // Factory guards

    [Test]
    [Arguments(nameof(TableOwnerEntry.User), "table", null)]
    [Arguments(nameof(TableOwnerEntry.User), "table", "")]
    [Arguments(nameof(TableOwnerEntry.User), "table", "   ")]
    [Arguments(nameof(TableOwnerEntry.User), "ownerColumn", null)]
    [Arguments(nameof(TableOwnerEntry.User), "ownerColumn", "")]
    [Arguments(nameof(TableOwnerEntry.User), "ownerColumn", "   ")]
    [Arguments(nameof(TableOwnerEntry.Budget), "table", null)]
    [Arguments(nameof(TableOwnerEntry.Budget), "table", "")]
    [Arguments(nameof(TableOwnerEntry.Budget), "table", "   ")]
    [Arguments(nameof(TableOwnerEntry.Budget), "ownerColumn", null)]
    [Arguments(nameof(TableOwnerEntry.Budget), "ownerColumn", "")]
    [Arguments(nameof(TableOwnerEntry.Budget), "ownerColumn", "   ")]
    [Arguments(nameof(TableOwnerEntry.Nobody), "table", null)]
    [Arguments(nameof(TableOwnerEntry.Nobody), "table", "")]
    [Arguments(nameof(TableOwnerEntry.Nobody), "table", "   ")]
    public async Task Factory_WithABlankArgument_ThrowsArgumentException(
        string factory,
        string blankParameter,
        string? blank)
    {
        // Arrange — every other argument valid, so the throw can only be about the blank one.
        string table = blankParameter == "table" ? blank! : "accounts";
        string second = blankParameter == "table" ? Valid(factory) : blank!;

        // Act
        Func<TableOwnerEntry> act = factory switch
        {
            nameof(TableOwnerEntry.User) => () => TableOwnerEntry.User(table, second),
            nameof(TableOwnerEntry.Budget) => () => TableOwnerEntry.Budget(table, second),
            nameof(TableOwnerEntry.Nobody) => () => TableOwnerEntry.Nobody(table, second),
            _ => throw new ArgumentOutOfRangeException(nameof(factory), factory, null),
        };

        // Assert — the ArgumentException family: a null may surface as ArgumentNullException.
        await Assert.That(act).Throws<ArgumentException>();
    }

    [Test]
    public async Task Nobody_WithANullReason_ThrowsArgumentNullException()
    {
        // Arrange
        string? because = null;

        // Act
        Func<TableOwnerEntry> act = () => TableOwnerEntry.Nobody(ProbeTable, because!);

        // Assert
        await Assert.That(act).Throws<ArgumentNullException>();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Nobody_WithABlankReason_DoesNotThrow(string because)
    {
        // Arrange — guarded for null only, as ColumnClassificationEntry.Excluded argues: refusing a
        // blank here would read as though the reason had been judged; the shipped-list floor judges it.
        string table = ProbeTable;

        // Act
        Func<TableOwnerEntry> act = () => TableOwnerEntry.Nobody(table, because);

        // Assert
        await Assert.That(act).ThrowsNothing();
    }

    // DisagreementsWith

    [Test]
    public async Task DisagreementsWith_WhenEveryPairAgrees_ReportsNothing()
    {
        // Arrange — one table per word, each discovered with the ownership its word means.
        TableOwnerEntry[] owners =
        [
            TableOwnerEntry.User("people", "user_id"),
            TableOwnerEntry.Budget("ledgers", "budget_id"),
            TableOwnerEntry.Nobody("rates", ProbeReason),
        ];
        DiscoveredTable[] discovered =
        [
            Discovered("people", TableOwnership.UserOwned),
            Discovered("ledgers", TableOwnership.BudgetOwned),
            Discovered("rates", TableOwnership.None),
        ];

        // Act
        IReadOnlyList<string> disagreements = TableOwnerCoverage.DisagreementsWith(owners, discovered);

        // Assert
        await Assert.That(disagreements).IsEmpty();
    }

    [Test]
    [Arguments(OwnedBy.User, TableOwnership.BudgetOwned)]
    [Arguments(OwnedBy.User, TableOwnership.None)]
    [Arguments(OwnedBy.Budget, TableOwnership.UserOwned)]
    [Arguments(OwnedBy.Budget, TableOwnership.None)]
    [Arguments(OwnedBy.Nobody, TableOwnership.UserOwned)]
    [Arguments(OwnedBy.Nobody, TableOwnership.BudgetOwned)]
    public async Task DisagreementsWith_ForAWrongPairing_ReportsOneLineNamingTheTable(
        OwnedBy written,
        TableOwnership discoveredOwnership)
    {
        // Arrange — the probe disagrees; an agreeing neighbour sits beside it so a comparison that
        // reported every table whenever anything disagreed is caught.
        TableOwnerEntry[] owners =
        [
            Entry(ProbeTable, written),
            TableOwnerEntry.Budget(NeighbourTable, "budget_id"),
        ];
        DiscoveredTable[] discovered =
        [
            Discovered(ProbeTable, discoveredOwnership),
            Discovered(NeighbourTable, TableOwnership.BudgetOwned),
        ];

        // Act
        IReadOnlyList<string> disagreements = TableOwnerCoverage.DisagreementsWith(owners, discovered);

        Console.WriteLine($"Disagreements: {string.Join(" | ", disagreements)}");

        // Assert
        await Assert.That(disagreements.Count).IsEqualTo(1);
        await Assert.That(disagreements[0]).Contains(ProbeTable);
        await Assert.That(disagreements[0]).DoesNotContain(NeighbourTable);
    }

    [Test]
    public async Task DisagreementsWith_ForAnOwnedTableTheCatalogDoesNotHold_ReportsIt()
    {
        // Arrange
        TableOwnerEntry[] owners =
        [
            TableOwnerEntry.User(ProbeTable, "user_id"),
            TableOwnerEntry.Budget(NeighbourTable, "budget_id"),
        ];
        DiscoveredTable[] discovered = [Discovered(NeighbourTable, TableOwnership.BudgetOwned)];

        // Act
        IReadOnlyList<string> disagreements = TableOwnerCoverage.DisagreementsWith(owners, discovered);

        // Assert
        await Assert.That(disagreements.Count).IsEqualTo(1);
        await Assert.That(disagreements[0]).Contains(ProbeTable);
    }

    [Test]
    public async Task DisagreementsWith_ForADiscoveredTableTheListDoesNotName_ReportsIt()
    {
        // Arrange — undecided is not owned-by-nobody: a discovered table with no entry is reported
        // even when the catalog reads no owner column on it.
        TableOwnerEntry[] owners = [TableOwnerEntry.Budget(NeighbourTable, "budget_id")];
        DiscoveredTable[] discovered =
        [
            Discovered(NeighbourTable, TableOwnership.BudgetOwned),
            Discovered(ProbeTable, TableOwnership.None),
        ];

        // Act
        IReadOnlyList<string> disagreements = TableOwnerCoverage.DisagreementsWith(owners, discovered);

        // Assert
        await Assert.That(disagreements.Count).IsEqualTo(1);
        await Assert.That(disagreements[0]).Contains(ProbeTable);
    }

    /// <summary>
    /// DisagreementsWith judges every relation kind it is handed, not only ordinary tables.
    /// </summary>
    /// <remarks>
    /// A view, a partitioned parent or a materialized view holds or exposes rows as surely as a table
    /// does, and one skipped by kind would be a relation nobody decided about that reports nothing.
    /// </remarks>
    [Test]
    [Arguments(RelationKind.PartitionedTable)]
    [Arguments(RelationKind.View)]
    [Arguments(RelationKind.MaterializedView)]
    [Arguments(RelationKind.ForeignTable)]
    public async Task DisagreementsWith_ForAnUnlistedRelationOfAnyKind_ReportsIt(RelationKind kind)
    {
        // Arrange
        TableOwnerEntry[] owners = [TableOwnerEntry.Budget(NeighbourTable, "budget_id")];
        DiscoveredTable[] discovered =
        [
            Discovered(NeighbourTable, TableOwnership.BudgetOwned),
            new(ProbeTable, kind, RowSecurityEnabled: false, TableOwnership.None, [], []),
        ];

        // Act
        IReadOnlyList<string> disagreements = TableOwnerCoverage.DisagreementsWith(owners, discovered);

        // Assert
        await Assert.That(disagreements.Count).IsEqualTo(1);
        await Assert.That(disagreements[0]).Contains(ProbeTable);
    }

    [Test]
    public async Task DisagreementsWith_ComparesTableNamesOrdinally()
    {
        // Arrange — `Owner_Probe` in the list names nothing the catalog holds as `owner_probe`.
        TableOwnerEntry[] owners = [TableOwnerEntry.User("Owner_Probe", "user_id")];
        DiscoveredTable[] discovered = [Discovered(ProbeTable, TableOwnership.UserOwned)];

        // Act
        IReadOnlyList<string> disagreements = TableOwnerCoverage.DisagreementsWith(owners, discovered);

        // Assert — both spellings reported, one line each.
        string[] namingTheEntry = [.. disagreements.Where(line => line.Contains("Owner_Probe", StringComparison.Ordinal))];
        string[] namingTheTable = [.. disagreements.Where(line => line.Contains(ProbeTable, StringComparison.Ordinal))];
        await Assert.That(disagreements.Count).IsEqualTo(2);
        await Assert.That(namingTheEntry.Length).IsEqualTo(1);
        await Assert.That(namingTheTable.Length).IsEqualTo(1);
    }

    /// <summary>
    /// The owner column an owned entry's kind names: <c>budget_id</c>, <c>user_id</c>, or <c>id</c>
    /// on <c>users</c>, which is the person.
    /// </summary>
    private static string ExpectedOwnerColumn(TableOwnerEntry entry) => entry switch
    {
        { Owner: OwnedBy.Budget } => "budget_id",
        { Owner: OwnedBy.User, Table: "users" } => "id",
        { Owner: OwnedBy.User } => "user_id",
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Owner, null),
    };

    /// <summary>A valid second argument for <paramref name="factory" />.</summary>
    private static string Valid(string factory) =>
        factory == nameof(TableOwnerEntry.Nobody) ? ProbeReason : "user_id";

    /// <summary>An entry of the given word, with a valid column or reason.</summary>
    private static TableOwnerEntry Entry(string table, OwnedBy owner) => owner switch
    {
        OwnedBy.User => TableOwnerEntry.User(table, "user_id"),
        OwnedBy.Budget => TableOwnerEntry.Budget(table, "budget_id"),
        OwnedBy.Nobody => TableOwnerEntry.Nobody(table, ProbeReason),
        _ => throw new ArgumentOutOfRangeException(nameof(owner), owner, null),
    };

    /// <summary>An ordinary policed table with the given ownership, as discovery would report it.</summary>
    private static DiscoveredTable Discovered(string name, TableOwnership ownership) =>
        new(name, RelationKind.OrdinaryTable, RowSecurityEnabled: true, ownership, [], []);
}
