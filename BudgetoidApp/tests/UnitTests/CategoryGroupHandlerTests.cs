using Application.CategoryGroups;
using Application.CategoryGroups.CreateCategoryGroup;
using Application.CategoryGroups.DeleteCategoryGroup;
using Application.CategoryGroups.GetCategoryGroup;
using Application.CategoryGroups.MoveCategoryGroup;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CategoryGroupHandlerTests
{
    [Test]
    public async Task Create_AppendsGroupsInUserOrder()
    {
        // Arrange
        var budgetId = Guid.CreateVersion7();
        var timeProvider = TimeProvider();
        var repository = new InMemoryCategoryGroupRepository(budgetId, timeProvider);
        var handler = new CreateCategoryGroupHandler(
            repository,
            new StubBudgetContext(budgetId),
            timeProvider);

        // Act
        var first = await handler.HandleAsync(new CreateCategoryGroupCommand("Essentials", null));
        var second = await handler.HandleAsync(new CreateCategoryGroupCommand("Lifestyle", null));

        // Assert
        await Assert.That(first.Position).IsEqualTo(0);
        await Assert.That(second.Position).IsEqualTo(1);
    }

    [Test]
    public async Task Move_ReindexesGroupsContiguously()
    {
        // Arrange
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryCategoryGroupRepository(budgetId, TimeProvider());
        var first = await repository.CreateAsync("First");
        var second = await repository.CreateAsync("Second");
        var third = await repository.CreateAsync("Third");
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
        var categoryGroup = await repository.CreateAsync("Only");
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
        var categoryGroup = await repository.CreateAsync();
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
        var categoryGroup = await repository.CreateAsync("Essentials");
        var handler = new GetCategoryGroupHandler(repository);

        // Act
        CategoryGroupDto? dto = await handler.HandleAsync(
            new GetCategoryGroupQuery(categoryGroup.Id));

        // Assert
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Id).IsEqualTo(categoryGroup.Id);
        await Assert.That(dto.Name).IsEqualTo("Essentials");
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
