using Domain.CategoryGroups;
using Domain.Common;
using TestSupport;

namespace UnitTests;

public sealed class CategoryGroupOrderingTests
{
    [Test]
    public async Task MoveToPosition_ReindexesContiguously()
    {
        // Arrange
        CategoryGroup first = CreateCategoryGroup("First", 0);
        CategoryGroup second = CreateCategoryGroup("Second", 1);
        CategoryGroup moved = CreateCategoryGroup("Moved", 2);

        // Act
        CategoryGroupOrdering.MoveToPosition(moved, 0, [first, second, moved]);

        // Assert
        await Assert.That(moved.Position).IsEqualTo(0);
        await Assert.That(first.Position).IsEqualTo(1);
        await Assert.That(second.Position).IsEqualTo(2);
    }

    [Test]
    public async Task MoveToPosition_OutsideList_ThrowsValidationExceptionWithoutMutating()
    {
        // Arrange
        CategoryGroup only = CreateCategoryGroup("Only", 0);

        // Act
        ValidationException? exception = null;
        try
        {
            CategoryGroupOrdering.MoveToPosition(only, 1, [only]);
        }
        catch (ValidationException caught)
        {
            exception = caught;
        }

        // Assert
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Errors.ContainsKey("Position")).IsTrue();
        await Assert.That(only.Position).IsEqualTo(0);
    }

    [Test]
    public async Task CloseGap_ReindexesRemainingContiguously()
    {
        // Arrange
        CategoryGroup first = CreateCategoryGroup("First", 0);
        CategoryGroup last = CreateCategoryGroup("Last", 2);

        // Act
        CategoryGroupOrdering.CloseGap([first, last]);

        // Assert
        await Assert.That(first.Position).IsEqualTo(0);
        await Assert.That(last.Position).IsEqualTo(1);
    }

    // The label seals a name and its index; nothing in this file reads either back. Ordering is over
    // Position and Id, both values this server can still read, so sealing the name took no capability
    // away from the rule under test - only from the way a fixture spells one.
    private static CategoryGroup CreateCategoryGroup(string label, int position) =>
        CategoryGroup.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed(label),
            null,
            position,
            new DateTime(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc));
}
