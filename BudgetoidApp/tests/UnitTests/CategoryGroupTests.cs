using Domain.CategoryGroups;
using Domain.Common;

namespace UnitTests;

public sealed class CategoryGroupTests
{
    [Test]
    public async Task Create_WithValidInput_NormalizesValuesAndStoresPosition()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        DateTime createdAtUtc = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

        // Act
        CategoryGroup group = CategoryGroup.Create(
            userId,
            "  Essential Obligations  ",
            "  Required spending  ",
            2,
            createdAtUtc);

        // Assert
        await Assert.That(group.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(group.UserId).IsEqualTo(userId);
        await Assert.That(group.Name).IsEqualTo("Essential Obligations");
        await Assert.That(group.Description).IsEqualTo("Required spending");
        await Assert.That(group.Position).IsEqualTo(2);
        await Assert.That(group.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithBlankDescription_StoresNull()
    {
        // Act
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            "Essential Obligations",
            "   ",
            0,
            UtcNow());

        // Assert
        await Assert.That(group.Description).IsNull();
    }

    [Test]
    [Arguments("", 0, "Name")]
    [Arguments("Valid", -1, "Position")]
    public async Task Create_WithInvalidValue_ThrowsValidationException(
        string name,
        int position,
        string expectedField)
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            CategoryGroup.Create(Guid.CreateVersion7(), name, null, position, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(expectedField)).IsTrue();
    }

    [Test]
    public async Task Create_WithInvalidOwnerOrDescription_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            CategoryGroup.Create(Guid.Empty, "Valid", new string('x', 501), 0, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
    }

    [Test]
    public async Task Update_WithValidInput_NormalizesValues()
    {
        // Arrange
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            "Essential Obligations",
            "Old",
            0,
            UtcNow());

        // Act
        group.Update("  Essentials  ", "   ");

        // Assert
        await Assert.That(group.Name).IsEqualTo("Essentials");
        await Assert.That(group.Description).IsNull();
    }

    [Test]
    public async Task SetPosition_WithValidPosition_ChangesPosition()
    {
        // Arrange
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            "Essential Obligations",
            null,
            0,
            UtcNow());

        // Act
        group.SetPosition(3);

        // Assert
        await Assert.That(group.Position).IsEqualTo(3);
    }

    [Test]
    public async Task SetPosition_WithNegativePosition_ThrowsAndLeavesPositionUnchanged()
    {
        // Arrange
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            "Essential Obligations",
            null,
            1,
            UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() => group.SetPosition(-1));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
        await Assert.That(group.Position).IsEqualTo(1);
    }

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
