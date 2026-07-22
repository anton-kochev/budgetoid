using Domain.Categories;
using Domain.Common;

namespace UnitTests;

public sealed class CategoryTests
{
    [Test]
    public async Task Create_WithValidInput_NormalizesValuesAndRequiresGroup()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var categoryGroupId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();

        // Act
        Category category = Category.Create(
            userId,
            categoryGroupId,
            "  Groceries  ",
            "  Food and household supplies  ",
            2,
            createdAtUtc);

        // Assert
        await Assert.That(category.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(category.UserId).IsEqualTo(userId);
        await Assert.That(category.CategoryGroupId).IsEqualTo(categoryGroupId);
        await Assert.That(category.Name).IsEqualTo("Groceries");
        await Assert.That(category.Description).IsEqualTo("Food and household supplies");
        await Assert.That(category.Position).IsEqualTo(2);
        await Assert.That(category.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithMissingOwnerOrGroup_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Category.Create(Guid.Empty, Guid.Empty, "Groceries", null, 0, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("CategoryGroupId")).IsTrue();
    }

    [Test]
    [Arguments("", null, 0, "Name")]
    [Arguments("Groceries", null, -1, "Position")]
    public async Task Create_WithInvalidValue_ThrowsValidationException(
        string name,
        string? description,
        int position,
        string expectedField)
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Category.Create(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                name,
                description,
                position,
                UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(expectedField)).IsTrue();
    }

    [Test]
    public async Task Update_WithValidInput_NormalizesValues()
    {
        // Arrange
        Category category = CreateCategory();

        // Act
        category.Update("  Food  ", "   ");

        // Assert
        await Assert.That(category.Name).IsEqualTo("Food");
        await Assert.That(category.Description).IsNull();
    }

    [Test]
    public async Task Place_WithValidDestination_ChangesGroupAndPosition()
    {
        // Arrange
        Category category = CreateCategory();
        var destinationGroupId = Guid.CreateVersion7();

        // Act
        category.Place(destinationGroupId, 4);

        // Assert
        await Assert.That(category.CategoryGroupId).IsEqualTo(destinationGroupId);
        await Assert.That(category.Position).IsEqualTo(4);
    }

    [Test]
    public async Task Place_WithInvalidDestination_ThrowsAndLeavesPlacementUnchanged()
    {
        // Arrange
        Category category = CreateCategory();
        Guid originalGroupId = category.CategoryGroupId;
        int originalPosition = category.Position;

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            category.Place(Guid.Empty, -1));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("CategoryGroupId")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
        await Assert.That(category.CategoryGroupId).IsEqualTo(originalGroupId);
        await Assert.That(category.Position).IsEqualTo(originalPosition);
    }

    private static Category CreateCategory() => Category.Create(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "Groceries",
        "Food",
        1,
        UtcNow());

    private static DateTime UtcNow() =>
        new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    private static ValidationException ThrowsValidationException(Action action)
    {
        try
        {
            action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
