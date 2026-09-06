using Infrastructure.Persistence.Inventory;
using Microsoft.EntityFrameworkCore.Metadata;

namespace UnitTests;

/// <summary>
/// Covers the enumerator the data inventory is built on: every property the EF model maps, rendered
/// as the <c>table.column</c> pair the catalog would name it by.
/// </summary>
/// <remarks>
/// <para>
/// FR-005 wants every schema column classified as <i>narrative</i>, <i>arithmetic</i> or
/// <i>excluded</i>, with the build red on a column nobody classified. Nothing can classify a column
/// it cannot name, and nothing can name a column without saying which table it sits on — two tables
/// in this schema carry a <c>name</c>, four carry a <c>created_at_utc</c>, and a classification list
/// keyed on the bare column name would silently apply one argument to several columns. So the first
/// thing the inventory owes is an enumerator that <b>keeps the table</b>.
/// </para>
/// <para>
/// The walk itself is not new. <c>ProhibitedColumnVocabularyTests</c> and
/// <c>ErasureRemnantVocabularyTests</c> both do design-time model → <c>GetEntityTypes</c> →
/// <c>StoreObjectIdentifier.Create</c> → <c>GetProperties</c> → <c>GetColumnName(table)</c>, and both
/// throw the table away, because both ask a question about a <i>name</i> rather than about a column.
/// <see cref="MappedSchema" /> is that same walk with the table kept, moved into production code so
/// that the reconciliation against the live catalog and the classification built on top of it read
/// one enumerator rather than three.
/// </para>
/// <para>
/// No database: this reads the design-time model, which is regenerated from the configurations on
/// every build. The runtime read-optimized model is deliberately not the subject, for the reason the
/// neighbouring model tests give — it drops what only migrations consume. Nothing here opens the
/// connection the options carry.
/// </para>
/// </remarks>
public sealed class MappedSchemaTests
{
    /// <summary>
    /// The floor the enumerated count must clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A floor and never an exact count.</b> The model maps something over ninety properties
    /// today, and a number written here would be a pin nothing else in the repository holds: it goes
    /// red on every legitimate column, which trains the next reader to edit the number rather than
    /// read the diff, and this repository has already drifted on a written-down count twice. What the
    /// floor is for is the failure the set assertions below cannot see — an enumerator that returns
    /// the primary keys only, or the first entity type only, or an empty list because
    /// <c>StoreObjectIdentifier.Create</c> came back null for every entity. Sixteen tables' keys
    /// would be about sixteen entries; eighty cannot be reached without walking the properties of
    /// most of the model.
    /// </para>
    /// </remarks>
    private const int MappedColumnFloor = 80;

    [Test]
    public async Task MappedSchema_ColumnsOf_ReturnsQualifiedTableAndColumnForEveryMappedProperty()
    {
        // Arrange — the design-time model, as the two existing walks read it.
        IModel model = MappedSchema.DesignTimeModel();

        // Act
        IReadOnlyList<MappedColumn> columns = MappedSchema.ColumnsOf(model);
        string[] qualified = [.. columns.Select(column => column.Qualified)];

        // Assert — the floor first, because every assertion after it is a Contains and an empty list
        // satisfies none of them loudly: a broken enumerator returning nothing fails here with a
        // number rather than four times with a name.
        await Assert.That(columns.Count).IsGreaterThanOrEqualTo(MappedColumnFloor);

        // Two named columns, one from each end of the schema. `transactions.description` is a
        // narrative column on the largest table, `wrapped_account_keys.wrapped_content_key` is key
        // material on the newest one — an enumerator that reached only the tables registered first,
        // or only the ones with simple property types, misses one of the two.
        await Assert.That(qualified).Contains("transactions.description");
        await Assert.That(qualified).Contains("wrapped_account_keys.wrapped_content_key");

        // The table is KEPT, which is the one thing this enumerator does that its two ancestors do
        // not. `accounts.name` and `payees.name` are one column name on two tables: an enumerator
        // that threw the table away would render both as `name`, which is a set of one where the
        // inventory needs a set of two, and every later classification would inherit that collapse.
        await Assert.That(qualified).Contains("accounts.name");
        await Assert.That(qualified).Contains("payees.name");

        // And `Qualified` is the two members joined, rather than a third thing stored beside them.
        // Without this, a record whose `Qualified` returned `Column` alone would pass every Contains
        // above the moment the strings happened to agree — which on `transactions.description` they
        // never do, but on a single-table lookup later they would.
        string[] disagreeing =
        [
            .. columns
                .Where(column => !string.Equals(
                    column.Qualified,
                    $"{column.Table}.{column.Column}",
                    StringComparison.Ordinal))
                .Select(column => $"{column.Table} / {column.Column} rendered as {column.Qualified}")
                .Order(StringComparer.Ordinal),
        ];
        await Assert.That(disagreeing).IsEmpty();

        // Every entry names a table, a column and a CLR type. The type is what the next phase's
        // narrative/arithmetic split reads, and an entry carrying `null!` for it compiles under
        // nullable reference types and detonates only when somebody finally asks.
        string[] incomplete =
        [
            .. columns
                .Where(column => string.IsNullOrWhiteSpace(column.Table)
                                 || string.IsNullOrWhiteSpace(column.Column)
                                 || column.ClrType is null)
                .Select(column => column.Qualified)
                .Order(StringComparer.Ordinal),
        ];
        await Assert.That(incomplete).IsEmpty();
    }
}
