using Infrastructure.Persistence.Provisioning;

namespace Infrastructure.Persistence.Inventory;

/// <summary>
/// Whose rows a table holds, which is the question the erasure has to answer for every table.
/// </summary>
/// <remarks>
/// Not <see cref="TableOwnership" />: there <see cref="TableOwnership.None" /> means nobody decided,
/// and here <see cref="Nobody" /> is a decision that has to be written out with its reason.
/// </remarks>
public enum OwnedBy
{
    /// <summary>The rows belong to one person, keyed on the owner column.</summary>
    User,

    /// <summary>The rows belong to one budget, keyed on the owner column.</summary>
    Budget,

    /// <summary>The rows belong to no account, by a decision recorded with a reason.</summary>
    Nobody,
}

/// <summary>
/// One mapped table with whose rows it holds, and the column that names the owner.
/// </summary>
/// <remarks>
/// <para>
/// There is no public constructor, for the reason <see cref="ColumnClassificationEntry" /> gives at
/// its own: a table owned by nobody is meant to be reachable only through the factory that takes a
/// reason, and an owned table only through one that takes its owner column.
/// </para>
/// <para>
/// The reason is guarded the way <see cref="ColumnClassificationEntry.Excluded" /> guards its own:
/// against null and nothing else. A factory refusing a blank reason would read as though the
/// reason's content had been judged, and nothing here judges it. What holds it is the 80-character
/// floor the unit tier applies to every <see cref="OwnedBy.Nobody" /> entry of the shipped list,
/// the same floor an excluded column's reason meets.
/// </para>
/// </remarks>
public sealed record TableOwnerEntry
{
    /// <summary>
    /// The one constructor, private so that an entry can only be reached through a factory.
    /// </summary>
    private TableOwnerEntry(
        string table,
        OwnedBy owner,
        string? ownerColumn,
        string? ownedByNobodyBecause)
    {
        Table = table;
        Owner = owner;
        OwnerColumn = ownerColumn;
        OwnedByNobodyBecause = ownedByNobodyBecause;
    }

    /// <summary>The table name as the EF model maps it, which is how <c>pg_class</c> stores it.</summary>
    public string Table { get; }

    /// <summary>Whose rows the table holds.</summary>
    public OwnedBy Owner { get; }

    /// <summary>
    /// The column naming the owner, written out rather than derived, or <see langword="null" /> for a
    /// table owned by nobody.
    /// </summary>
    public string? OwnerColumn { get; }

    /// <summary>
    /// Why the table holds no account's rows, or <see langword="null" /> for an owned table.
    /// </summary>
    public string? OwnedByNobodyBecause { get; }

    /// <summary>Names a table whose rows belong to one person.</summary>
    /// <param name="table">The table name as the model maps it.</param>
    /// <param name="ownerColumn">The column holding the person's id.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentException">Either argument is null, empty or whitespace.</exception>
    public static TableOwnerEntry User(string table, string ownerColumn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerColumn);

        return new(table, OwnedBy.User, ownerColumn, null);
    }

    /// <summary>Names a table whose rows belong to one budget.</summary>
    /// <param name="table">The table name as the model maps it.</param>
    /// <param name="ownerColumn">The column holding the budget's id.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentException">Either argument is null, empty or whitespace.</exception>
    public static TableOwnerEntry Budget(string table, string ownerColumn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerColumn);

        return new(table, OwnedBy.Budget, ownerColumn, null);
    }

    /// <summary>Names a table whose rows belong to no account, with the argument for it.</summary>
    /// <param name="table">The table name as the model maps it.</param>
    /// <param name="because">
    /// Why no account owns what the table holds; checked for null only, as the type's remarks argue.
    /// </param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentException"><paramref name="table" /> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="because" /> is null.</exception>
    public static TableOwnerEntry Nobody(string table, string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(because);

        return new(table, OwnedBy.Nobody, null, because);
    }
}

/// <summary>
/// The two ways an owner list and a schema's tables can disagree, reported together.
/// </summary>
/// <param name="Undecided">Tables the model maps that the owner list does not name.</param>
/// <param name="Stale">Tables the owner list names that the model no longer maps.</param>
public sealed record OwnershipCoverage(
    IReadOnlyList<string> Undecided,
    IReadOnlyList<string> Stale);

/// <summary>
/// Compares an owner list against the tables a schema maps, and against the ownership the live
/// catalog reads off each table's columns.
/// </summary>
/// <remarks>
/// Both inputs are parameters, never <see cref="DataInventory.TableOwners" />, for the reason
/// <see cref="DataInventoryCoverage" /> gives at its own.
/// </remarks>
public static class TableOwnerCoverage
{
    /// <summary>
    /// Reports every mapped table no entry names, and every entry naming no mapped table.
    /// </summary>
    /// <remarks>
    /// Names are compared ordinally, because <c>pg_class</c> keeps a quoted identifier exactly as EF
    /// spells it. Both lists come back distinct and ordinal-sorted. A table named twice in
    /// <paramref name="owners" /> is invisible here by nature, since both directions are set
    /// differences; the shipped list's own duplicate test is what holds that.
    /// </remarks>
    /// <param name="mappedTables">The schema's tables, ordinarily from <see cref="MappedSchema.TablesOf" />.</param>
    /// <param name="owners">The written-down owners to check that schema against.</param>
    /// <returns>The disagreement in both directions.</returns>
    public static OwnershipCoverage Compare(
        IReadOnlyList<string> mappedTables,
        IReadOnlyList<TableOwnerEntry> owners)
    {
        ArgumentNullException.ThrowIfNull(mappedTables);
        ArgumentNullException.ThrowIfNull(owners);

        HashSet<string> mapped = new(mappedTables, StringComparer.Ordinal);
        HashSet<string> decided = new(owners.Select(entry => entry.Table), StringComparer.Ordinal);

        return new OwnershipCoverage(
            [.. mapped.Except(decided, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. decided.Except(mapped, StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Names every table on which the written-down owner and the ownership the catalog read off the
    /// table's own columns disagree, including a table only one side holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <see cref="DiscoveredTable.Ownership" /> is read — a column fact — never the policies or
    /// the row-level-security exemptions, because an exempt table can still hold one person's rows.
    /// <see cref="OwnedBy.User" /> agrees with <see cref="TableOwnership.UserOwned" />,
    /// <see cref="OwnedBy.Budget" /> with <see cref="TableOwnership.BudgetOwned" />, and
    /// <see cref="OwnedBy.Nobody" /> with <see cref="TableOwnership.None" />.
    /// </para>
    /// <para>
    /// Every relation kind is judged, and nothing is excused: the caller removes
    /// <see cref="DataInventory.RelationsOutsideTheModel" /> from <paramref name="discovered" /> first.
    /// Names are compared ordinally.
    /// </para>
    /// </remarks>
    /// <param name="owners">The written-down owners.</param>
    /// <param name="discovered">The relations as the live catalog reports them.</param>
    /// <returns>One line per disagreeing entry or relation, ordinal-sorted; empty when they agree.</returns>
    public static IReadOnlyList<string> DisagreementsWith(
        IReadOnlyList<TableOwnerEntry> owners,
        IReadOnlyList<DiscoveredTable> discovered)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(discovered);

        // Grouped rather than keyed with ToDictionary, so a repeated name is not thrown on. A name
        // repeated in owners is judged once per entry; one repeated in discovered is collapsed to its
        // first relation and the rest are never read. That loses nothing from DiscoverAsync, which
        // reads the one schema public, where pg_class cannot hold two relations of one name.
        Dictionary<string, DiscoveredTable> discoveredByName = discovered
            .GroupBy(table => table.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        HashSet<string> written = new(owners.Select(entry => entry.Table), StringComparer.Ordinal);

        List<string> lines = [];

        foreach (TableOwnerEntry entry in owners)
        {
            if (!discoveredByName.TryGetValue(entry.Table, out DiscoveredTable? table))
            {
                lines.Add(
                    $"{entry.Table}: written as owned by {entry.Owner}, but the catalog holds no "
                    + "relation of that name");
            }
            else if (table.Ownership != Expected(entry.Owner))
            {
                lines.Add(
                    $"{entry.Table}: written as owned by {entry.Owner}, but the catalog reads "
                    + $"{table.Ownership} off its columns");
            }
        }

        foreach (DiscoveredTable table in discoveredByName.Values)
        {
            if (!written.Contains(table.Name))
            {
                lines.Add(
                    $"{table.Name}: the catalog holds this {table.Kind} and reads {table.Ownership} "
                    + "off its columns, but no owner entry names it");
            }
        }

        return [.. lines.Order(StringComparer.Ordinal)];
    }

    /// <summary>The catalog's word for the ownership a written word claims.</summary>
    private static TableOwnership Expected(OwnedBy owner) => owner switch
    {
        OwnedBy.User => TableOwnership.UserOwned,
        OwnedBy.Budget => TableOwnership.BudgetOwned,
        OwnedBy.Nobody => TableOwnership.None,
        _ => throw new ArgumentOutOfRangeException(nameof(owner), owner, null),
    };
}
