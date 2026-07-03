using Domain.Common;
using Domain.Groups;

namespace UnitTests;

public sealed class GroupTests
{
    [Test]
    public async Task Create_WithValidInput_TrimsNameAndStoresDescriptionAndCreatedAtUtc()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        DateTime createdAtUtc = new(2026, 7, 3, 13, 14, 15, DateTimeKind.Utc);

        // Act
        Group group = Group.Create(userId, "  Groceries  ", "  Weekly food  ", createdAtUtc);

        // Assert
        await Assert.That(group.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(group.UserId).IsEqualTo(userId);
        await Assert.That(group.Name).IsEqualTo("Groceries");
        await Assert.That(group.Description).IsEqualTo("Weekly food");
        await Assert.That(group.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithBlankDescription_StoresNull()
    {
        // Act
        Group group = Group.Create(Guid.CreateVersion7(), "Groceries", "   ", UtcNow());

        // Assert
        await Assert.That(group.Description).IsNull();
    }

    [Test]
    public async Task Create_WithNullDescription_StoresNull()
    {
        // Act
        Group group = Group.Create(Guid.CreateVersion7(), "Groceries", null, UtcNow());

        // Assert
        await Assert.That(group.Description).IsNull();
    }

    [Test]
    public async Task Create_WithEmptyUserId_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Group.Create(Guid.Empty, "Groceries", null, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankName_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Group.Create(Guid.CreateVersion7(), "   ", null, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithNameLongerThan200Characters_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Group.Create(Guid.CreateVersion7(), new string('x', 201), null, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithDescriptionLongerThan500Characters_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Group.Create(Guid.CreateVersion7(), "Groceries", new string('x', 501), UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
    }

    [Test]
    public async Task Update_WithValidInput_ReplacesNameAndDescription()
    {
        // Arrange
        Group group = Group.Create(Guid.CreateVersion7(), "Groceries", "old", UtcNow());

        // Act
        group.Update("  Food  ", "  new  ");

        // Assert
        await Assert.That(group.Name).IsEqualTo("Food");
        await Assert.That(group.Description).IsEqualTo("new");
    }

    [Test]
    public async Task Update_WithBlankDescription_ClearsDescription()
    {
        // Arrange
        Group group = Group.Create(Guid.CreateVersion7(), "Groceries", "old", UtcNow());

        // Act
        group.Update("Groceries", "   ");

        // Assert
        await Assert.That(group.Description).IsNull();
    }

    [Test]
    public async Task Update_WithBlankName_ThrowsValidationExceptionAndLeavesGroupUnchanged()
    {
        // Arrange
        Group group = Group.Create(Guid.CreateVersion7(), "Groceries", "old", UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            group.Update("   ", "new"));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(group.Name).IsEqualTo("Groceries");
        await Assert.That(group.Description).IsEqualTo("old");
    }

    private static DateTime UtcNow() => new(2026, 7, 3, 13, 14, 15, DateTimeKind.Utc);

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
