using Application.Categories;
using Application.Categories.CreateCategory;
using Application.Categories.DeleteCategory;
using Application.Categories.GetCategory;
using Application.Categories.PlaceCategory;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CategoryHandlerTests
{
    [Test]
    public async Task Create_AppendsWithinSelectedGroupAndReturnsGroupName()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var handler = new CreateCategoryHandler(
            fixture.Categories,
            fixture.CategoryGroups,
            new StubBudgetContext(fixture.BudgetId),
            fixture.TimeProvider);

        // Act
        var first = await handler.HandleAsync(new CreateCategoryCommand(
            "Groceries",
            null,
            fixture.SourceGroupId));
        var second = await handler.HandleAsync(new CreateCategoryCommand(
            "Utilities",
            null,
            fixture.SourceGroupId));

        // Assert
        await Assert.That(first.Position).IsEqualTo(0);
        await Assert.That(second.Position).IsEqualTo(1);
        await Assert.That(second.CategoryGroupName).IsEqualTo("Essentials");
    }

    [Test]
    public async Task Create_WithUnknownGroup_ThrowsValidationException()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var handler = new CreateCategoryHandler(
            fixture.Categories,
            fixture.CategoryGroups,
            new StubBudgetContext(fixture.BudgetId),
            fixture.TimeProvider);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new CreateCategoryCommand(
                "Groceries",
                null,
                Guid.CreateVersion7())));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("CategoryGroupId")).IsTrue();
        await Assert.That(fixture.Categories.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Place_AcrossGroups_ReindexesSourceAndDestination()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var groceries = await fixture.Categories.CreateAsync(fixture.SourceGroupId, "Groceries");
        var utilities = await fixture.Categories.CreateAsync(fixture.SourceGroupId, "Utilities");
        var dining = await fixture.Categories.CreateAsync(fixture.DestinationGroupId, "Dining Out");
        var handler = new PlaceCategoryHandler(fixture.Categories, fixture.CategoryGroups);

        // Act
        await handler.HandleAsync(new PlaceCategoryCommand(
            groceries.Id,
            fixture.DestinationGroupId,
            0));
        var categories = await fixture.Categories.GetAllAsync();

        // Assert
        var moved = categories.Single(category => category.Id == groceries.Id);
        var sourceRemainder = categories.Single(category => category.Id == utilities.Id);
        var destinationRemainder = categories.Single(category => category.Id == dining.Id);
        await Assert.That(moved.CategoryGroupId).IsEqualTo(fixture.DestinationGroupId);
        await Assert.That(moved.Position).IsEqualTo(0);
        await Assert.That(destinationRemainder.Position).IsEqualTo(1);
        await Assert.That(sourceRemainder.Position).IsEqualTo(0);
    }

    [Test]
    public async Task Place_WithUnknownDestinationGroup_ThrowsValidationException()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var category = await fixture.Categories.CreateAsync(fixture.SourceGroupId);
        var handler = new PlaceCategoryHandler(fixture.Categories, fixture.CategoryGroups);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new PlaceCategoryCommand(
                category.Id,
                Guid.CreateVersion7(),
                0)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("CategoryGroupId")).IsTrue();
        await Assert.That(fixture.Categories.PlaceCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Place_OutsideDestinationList_ThrowsValidationException()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var category = await fixture.Categories.CreateAsync(fixture.SourceGroupId);
        var handler = new PlaceCategoryHandler(fixture.Categories, fixture.CategoryGroups);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new PlaceCategoryCommand(
                category.Id,
                fixture.DestinationGroupId,
                1)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
        await Assert.That(category.CategoryGroupId).IsEqualTo(fixture.SourceGroupId);
    }

    [Test]
    public async Task Delete_WhenCategoryHasTransactions_ThrowsValidationException()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var category = await fixture.Categories.CreateAsync(fixture.SourceGroupId);
        fixture.Categories.MarkReferenced(category.Id);
        var handler = new DeleteCategoryHandler(fixture.Categories);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new DeleteCategoryCommand(category.Id)));

        // Assert
        await Assert.That(exception.Errors["Id"].Single())
            .IsEqualTo("Category cannot be deleted because it has transactions.");
        await Assert.That(fixture.Categories.DeleteCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Get_ReturnsCategoryWithGroupName()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var category = await fixture.Categories.CreateAsync(fixture.SourceGroupId, "Groceries");
        var handler = new GetCategoryHandler(fixture.Categories);

        // Act
        CategoryDto? dto = await handler.HandleAsync(new GetCategoryQuery(category.Id));

        // Assert
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Id).IsEqualTo(category.Id);
        await Assert.That(dto.Name).IsEqualTo("Groceries");
        await Assert.That(dto.CategoryGroupName).IsEqualTo("Essentials");
    }

    [Test]
    public async Task Get_WithUnknownId_ReturnsNull()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var handler = new GetCategoryHandler(fixture.Categories);

        // Act
        CategoryDto? dto = await handler.HandleAsync(new GetCategoryQuery(Guid.CreateVersion7()));

        // Assert
        await Assert.That(dto).IsNull();
    }

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

    private sealed record Fixture(
        Guid BudgetId,
        FakeTimeProvider TimeProvider,
        InMemoryCategoryGroupRepository CategoryGroups,
        InMemoryCategoryRepository Categories,
        Guid SourceGroupId,
        Guid DestinationGroupId)
    {
        public static async Task<Fixture> CreateAsync()
        {
            var budgetId = Guid.CreateVersion7();
            var timeProvider = new FakeTimeProvider(
                new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero));
            var categoryGroups = new InMemoryCategoryGroupRepository(budgetId, timeProvider);
            var source = await categoryGroups.CreateAsync("Essentials");
            var destination = await categoryGroups.CreateAsync("Lifestyle");
            var categories = new InMemoryCategoryRepository(budgetId, timeProvider, categoryGroups);
            return new Fixture(
                budgetId,
                timeProvider,
                categoryGroups,
                categories,
                source.Id,
                destination.Id);
        }
    }
}
