using Infrastructure.Persistence.Inventory;

namespace UnitTests;

/// <summary>
/// The list FR-034 and FR-035 are enforced over: the columns whose value no log record may carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two halves with two owners, and the split is the requirement.</b> The narrative columns are
/// already named by <see cref="DataInventory" />, which derives them from the model's own types, so
/// <see cref="NeverLoggedColumns.All" /> draws them from there and never copies them (NFR-022). What
/// the inventory cannot say is the three columns that identify a person without being narrative —
/// <c>users.email</c> is <i>arithmetic</i> and is exported, which is exactly why "not in a log" needs a
/// list of its own. <see cref="NeverLoggedColumns.Entries" /> is that list and nothing else.
/// </para>
/// <para>
/// Every difference below is asserted as a collection, not a joined string: TUnit truncates string
/// assertions, and a set comparison that names only its first offender hides the rest.
/// </para>
/// </remarks>
public sealed class NeverLoggedColumnsTests
{
    /// <summary>
    /// The three columns that name a person or a person's authenticator without being narrative.
    /// </summary>
    /// <remarks>
    /// Pinned as a set on purpose. Adding a fourth is a decision about what identifies a person, and it
    /// should cost an edit here that a reviewer reads; a narrative column never belongs here, because it
    /// reaches <see cref="NeverLoggedColumns.All" /> through the inventory.
    /// </remarks>
    private static readonly string[] ExpectedEntries =
    [
        "credentials.subject",
        "passkey_public_keys.webauthn_credential_id",
        "users.email",
    ];

    /// <summary>
    /// The shortest reason an entry may give. The same floor <c>DataInventoryCoverageTests</c> holds an
    /// exclusion to, and for the same reason: it makes writing nothing impossible and judges nothing
    /// else.
    /// </summary>
    private const int MinimumReasonLength = 80;

    [Test]
    public async Task Entries_NameOnlyColumnsTheModelMaps()
    {
        // Arrange
        HashSet<string> mapped = new(
            MappedSchema.ColumnsOf(MappedSchema.DesignTimeModel()).Select(column => column.Qualified),
            StringComparer.Ordinal);

        // Act
        string[] unmapped =
        [
            .. NeverLoggedColumns.Entries
                .Select(entry => entry.Qualified)
                .Where(qualified => !mapped.Contains(qualified))
                .Order(StringComparer.Ordinal),
        ];

        // Assert — non-vacuity first: an empty list names no unmapped column either.
        await Assert.That(NeverLoggedColumns.Entries).IsNotEmpty();
        await Assert.That(unmapped).IsEmpty();
    }

    [Test]
    public async Task Entries_AreExactlyTheThreeColumnsThatIdentifyAPerson()
    {
        // Arrange
        string[] actual = [.. NeverLoggedColumns.Entries.Select(entry => entry.Qualified)];

        // Act — both directions of the difference, each kept whole.
        string[] unexpected = [.. actual.Except(ExpectedEntries, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] missing = [.. ExpectedEntries.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // Assert — and the count, so a duplicate cannot hide behind an equal set.
        await Assert.That(unexpected).IsEmpty();
        await Assert.That(missing).IsEmpty();
        await Assert.That(actual.Length).IsEqualTo(ExpectedEntries.Length);
    }

    [Test]
    public async Task Entries_EachStateAReasonSomebodyCanArgueWith()
    {
        // Arrange / Act
        string[] tooShort =
        [
            .. NeverLoggedColumns.Entries
                .Where(entry => (entry.Reason?.Trim().Length ?? 0) < MinimumReasonLength)
                .Select(entry => entry.Qualified),
        ];

        // Assert
        await Assert.That(NeverLoggedColumns.Entries).IsNotEmpty();
        await Assert.That(tooShort).IsEmpty();
    }

    [Test]
    public async Task Entries_NameNoColumnTheInventoryClassifiesNarrative()
    {
        // Arrange
        HashSet<string> narrative = new(
            DataInventory.Of(ColumnClassification.Narrative).Select(entry => entry.Qualified),
            StringComparer.Ordinal);

        // Act — a narrative column written here would be a copy of the inventory, which is the one
        // shape NFR-022 forbids: it stays when the inventory changes.
        string[] copied =
        [
            .. NeverLoggedColumns.Entries
                .Select(entry => entry.Qualified)
                .Where(narrative.Contains)
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(narrative).IsNotEmpty();
        await Assert.That(copied).IsEmpty();
    }

    [Test]
    public async Task All_IsTheInventorysNarrativeColumnsAndTheEntries()
    {
        // Arrange — the expected set built from the two owners, not written out, so a ninth narrative
        // column moves both sides of this comparison without anybody editing it.
        string[] expected =
        [
            .. DataInventory.Of(ColumnClassification.Narrative)
                .Select(entry => entry.Qualified)
                .Concat(NeverLoggedColumns.Entries.Select(entry => entry.Qualified)),
        ];
        string[] actual = [.. NeverLoggedColumns.All.Select(entry => entry.Qualified)];

        // Act
        string[] unexpected = [.. actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] missing = [.. expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Console.WriteLine($"Never logged ({actual.Length}): {string.Join(", ", actual.Order(StringComparer.Ordinal))}");

        // Assert — non-vacuity, both directions, and the count against duplicates.
        await Assert.That(DataInventory.Of(ColumnClassification.Narrative)).IsNotEmpty();
        await Assert.That(unexpected).IsEmpty();
        await Assert.That(missing).IsEmpty();
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
    }

    /// <summary>
    /// The narrative door refuses an entry the inventory classifies anything else — which is what makes
    /// "the narrative half is only reachable through the inventory" a property rather than a habit.
    /// </summary>
    /// <remarks>
    /// Every classification but narrative is tried, each with a real inventory entry rather than one
    /// built here, so a guard narrowed to refuse only one of them is red on the other.
    /// </remarks>
    [Test]
    [Arguments(ColumnClassification.Arithmetic)]
    [Arguments(ColumnClassification.Excluded)]
    public async Task Narrative_ForAnEntryNotClassifiedNarrative_ThrowsArgumentException(
        ColumnClassification classification)
    {
        // Arrange
        ColumnClassificationEntry entry = DataInventory.Of(classification).First();

        // Act
        Func<NeverLoggedColumn> act = () => NeverLoggedColumn.Narrative(entry, "a reason nobody reads");

        // Assert
        await Assert.That(act).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task All_GivesEveryNarrativeColumnTheSameReason()
    {
        // Arrange — the narrative half of All, read by name from the inventory.
        HashSet<string> narrative = new(
            DataInventory.Of(ColumnClassification.Narrative).Select(entry => entry.Qualified),
            StringComparer.Ordinal);

        // Act — one reason shared by all, because the reason is the classification, not the column.
        // A per-column sentence would be a place for a ninth column to need an edit.
        string[] reasons =
        [
            .. NeverLoggedColumns.All
                .Where(entry => narrative.Contains(entry.Qualified))
                .Select(entry => entry.Reason)
                .Distinct(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(reasons.Length).IsEqualTo(1);
        await Assert.That(reasons[0].Trim().Length).IsGreaterThanOrEqualTo(MinimumReasonLength);
    }
}
