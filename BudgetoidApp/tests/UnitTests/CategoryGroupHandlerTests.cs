using Application.CategoryGroups;
using Application.CategoryGroups.DeleteCategoryGroup;
using Application.CategoryGroups.GetCategoryGroup;
using Application.CategoryGroups.MoveCategoryGroup;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The three category-group handlers whose subject is not a narrative value: move, delete and read.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is what is LEFT after the create and update legs moved out, and it is not a remnant.</b>
/// <c>CreateCategoryGroupHandlerTests</c> and <c>UpdateCategoryGroupHandlerTests</c> supersede exactly one
/// case that used to live here — the create leg's append rule, now
/// <c>CreateCategoryGroupHandlerTests.HandleAsync_AppendsTheNextPosition</c> — and cover none of the five
/// below. <see cref="MoveCategoryGroupHandler" />, <see cref="DeleteCategoryGroupHandler" /> and
/// <see cref="GetCategoryGroupHandler" /> have no other unit coverage anywhere in the solution, so
/// deleting the file with the two new ones landing would have taken reindexing, the out-of-range refusal,
/// the has-categories refusal and both read outcomes with it.
/// </para>
/// <para>
/// <b>Sealing changed one thing here and it is the seeding, not the subject.</b> A group's position, its
/// identifier and whether it holds categories are all values this server can still read, so every rule
/// these handlers own survives the columns moving untouched. What moved is that a seeded name is now a
/// sealed envelope and its index — <see cref="SealedNarrative.Indexed" /> — and that the one assertion
/// reading a name back off a DTO reads the ENVELOPE the fixture sealed, in the alphabet the API carries
/// it in.
/// </para>
/// </remarks>
public sealed class CategoryGroupHandlerTests
{
    [Test]
    public async Task Move_ReindexesGroupsContiguously()
    {
        // Arrange
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryCategoryGroupRepository(budgetId, TimeProvider());
        var first = await repository.CreateAsync(SealedNarrative.Indexed("First"));
        var second = await repository.CreateAsync(SealedNarrative.Indexed("Second"));
        var third = await repository.CreateAsync(SealedNarrative.Indexed("Third"));
        var handler = new MoveCategoryGroupHandler(repository);

        // Act
        await handler.HandleAsync(new MoveCategoryGroupCommand(third.Id, 0));
        var ordered = await repository.GetAllAsync();

        // Assert
        await Assert.That(ordered[0].Id).IsEqualTo(third.Id);
        await Assert.That(ordered[1].Id).IsEqualTo(first.Id);
        await Assert.That(ordered[2].Id).IsEqualTo(second.Id);
        await Assert.That(ordered[0].Position).IsEqualTo(0);
        await Assert.That(ordered[1].Position).IsEqualTo(1);
        await Assert.That(ordered[2].Position).IsEqualTo(2);
    }

    [Test]
    public async Task Move_OutsideList_ThrowsValidationExceptionWithoutChangingOrder()
    {
        // Arrange
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryCategoryGroupRepository(budgetId, TimeProvider());
        var categoryGroup = await repository.CreateAsync(SealedNarrative.Indexed("Only"));
        var handler = new MoveCategoryGroupHandler(repository);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new MoveCategoryGroupCommand(categoryGroup.Id, 1)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
        await Assert.That(categoryGroup.Position).IsEqualTo(0);
    }

    [Test]
    public async Task Delete_WhenGroupHasCategories_ThrowsValidationException()
    {
        // Arrange
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryCategoryGroupRepository(budgetId, TimeProvider());
        var categoryGroup = await repository.CreateAsync(SealedNarrative.Indexed("Encumbered"));
        repository.MarkHasCategories(categoryGroup.Id);
        var handler = new DeleteCategoryGroupHandler(repository);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new DeleteCategoryGroupCommand(categoryGroup.Id)));

        // Assert
        await Assert.That(exception.Errors["Id"].Single())
            .IsEqualTo("Category group cannot be deleted because it has categories.");
        await Assert.That(repository.DeleteCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Get_ReturnsCategoryGroup()
    {
        // Arrange
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryCategoryGroupRepository(budgetId, TimeProvider());
        var categoryGroup = await repository.CreateAsync(SealedNarrative.Indexed("Lifestyle"));
        var handler = new GetCategoryGroupHandler(repository);

        // Act
        CategoryGroupDto? dto = await handler.HandleAsync(
            new GetCategoryGroupQuery(categoryGroup.Id));

        // Assert
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Id).IsEqualTo(categoryGroup.Id);
        await Assert.That(dto.Name).IsEqualTo(SealedNarrative.EncodedName("Lifestyle"));
    }

    [Test]
    public async Task Get_WithUnknownId_ReturnsNull()
    {
        // Arrange
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryCategoryGroupRepository(budgetId, TimeProvider());
        var handler = new GetCategoryGroupHandler(repository);

        // Act
        CategoryGroupDto? dto = await handler.HandleAsync(
            new GetCategoryGroupQuery(Guid.CreateVersion7()));

        // Assert
        await Assert.That(dto).IsNull();
    }

    private static FakeTimeProvider TimeProvider() => new(
        new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero));

    private static async Task<ValidationException> ThrowsValidationExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
