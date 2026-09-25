using Domain.Categories;
using Domain.Common;
using TestSupport;

namespace UnitTests;

public sealed class CategoryOrderingTests
{
    [Test]
    public async Task Place_WithinGroup_ReindexesContiguously()
    {
        // Arrange
        var categoryGroupId = Guid.CreateVersion7();
        Category first = CreateCategory(categoryGroupId, "First", 0);
        Category second = CreateCategory(categoryGroupId, "Second", 1);
        Category moved = CreateCategory(categoryGroupId, "Moved", 2);
        List<Category> siblings = [first, second];

        // Act
        CategoryOrdering.Place(moved, categoryGroupId, 0, siblings, siblings);

        // Assert
        await Assert.That(moved.Position).IsEqualTo(0);
        await Assert.That(first.Position).IsEqualTo(1);
        await Assert.That(second.Position).IsEqualTo(2);
    }

    [Test]
    public async Task Place_AcrossGroups_ReindexesSourceAndDestination()
    {
        // Arrange
        var sourceGroupId = Guid.CreateVersion7();
        var destinationGroupId = Guid.CreateVersion7();
        Category sourceFirst = CreateCategory(sourceGroupId, "Source First", 0);
        Category moved = CreateCategory(sourceGroupId, "Moved", 1);
        Category sourceLast = CreateCategory(sourceGroupId, "Source Last", 2);
        Category destinationOnly = CreateCategory(destinationGroupId, "Destination Only", 0);

        // Act
        CategoryOrdering.Place(
            moved,
            destinationGroupId,
            1,
            [sourceFirst, sourceLast],
            [destinationOnly]);

        // Assert
        await Assert.That(moved.CategoryGroupId).IsEqualTo(destinationGroupId);
        await Assert.That(moved.Position).IsEqualTo(1);
        await Assert.That(destinationOnly.Position).IsEqualTo(0);
        await Assert.That(sourceFirst.Position).IsEqualTo(0);
        await Assert.That(sourceLast.Position).IsEqualTo(1);
    }

    [Test]
    public async Task Place_OutsideDestination_ThrowsValidationExceptionWithoutMutating()
    {
        // Arrange
        var sourceGroupId = Guid.CreateVersion7();
        var destinationGroupId = Guid.CreateVersion7();
        Category moved = CreateCategory(sourceGroupId, "Moved", 0);
        Category destinationOnly = CreateCategory(destinationGroupId, "Destination Only", 0);

        // Act
        ValidationException? exception = null;
        try
        {
            CategoryOrdering.Place(moved, destinationGroupId, 2, [], [destinationOnly]);
        }
        catch (ValidationException caught)
        {
            exception = caught;
        }

        // Assert
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Errors.ContainsKey("Position")).IsTrue();
        await Assert.That(moved.CategoryGroupId).IsEqualTo(sourceGroupId);
        await Assert.That(moved.Position).IsEqualTo(0);
        await Assert.That(destinationOnly.Position).IsEqualTo(0);
    }

    [Test]
    public async Task CloseGap_ReindexesRemainingContiguously()
    {
        // Arrange
        var categoryGroupId = Guid.CreateVersion7();
        Category first = CreateCategory(categoryGroupId, "First", 0);
        Category last = CreateCategory(categoryGroupId, "Last", 2);

        // Act
        CategoryOrdering.CloseGap([first, last], categoryGroupId);

        // Assert
        await Assert.That(first.Position).IsEqualTo(0);
        await Assert.That(last.Position).IsEqualTo(1);
    }

    /// <summary>
    /// A category in <paramref name="categoryGroupId" /> at <paramref name="position" />.
    /// </summary>
    /// <param name="label">
    /// What distinguishes this category from its siblings in a failure message. It is NOT the category's
    /// name and is never read back as one — the column holds an envelope this side has no key for. Every
    /// case in this file asserts on <c>Position</c> alone, so the label reaches
    /// <c>SealedNarrative.Name</c> and <c>SealedNarrative.BlindIndex</c> only and never an encoded
    /// member; the labels are the ordering words they always were.
    /// </param>
    private static Category CreateCategory(Guid categoryGroupId, string label, int position) =>
        Category.Create(
            // The id is minted HERE and threaded in, because Category.Create no longer mints one: it is
            // the associated data the name was sealed against, so the factory takes it and never
            // invents it.
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            categoryGroupId,
            SealedNarrative.Indexed(label),
            null,
            position,
            new DateTime(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc));
}
