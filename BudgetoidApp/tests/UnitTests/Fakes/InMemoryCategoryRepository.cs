using Application.Categories;
using Application.CategoryGroups;
using Domain.Categories;

namespace UnitTests.Fakes;

public sealed class InMemoryCategoryRepository(
    Guid userId,
    TimeProvider timeProvider,
    InMemoryCategoryGroupRepository categoryGroups)
    : ICategoryRepository, ICategoryReadService
{
    private readonly List<Category> _categories = [];
    private readonly HashSet<Guid> _referencedCategoryIds = [];

    public int AddCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }
    public int PlaceCallCount { get; private set; }

    public Task AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        AddCallCount++;
        _categories.Add(category);
        categoryGroups.MarkHasCategories(category.CategoryGroupId);
        return Task.CompletedTask;
    }

    public Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_categories.SingleOrDefault(category =>
            category.Id == id && category.UserId == userId));
    }

    public Task<int> GetNextPositionAsync(
        Guid categoryGroupId,
        CancellationToken cancellationToken = default)
    {
        List<Category> groupCategories = _categories
            .Where(category => category.CategoryGroupId == categoryGroupId)
            .ToList();
        int position = groupCategories.Count == 0
            ? 0
            : groupCategories.Max(category => category.Position) + 1;
        return Task.FromResult(position);
    }

    public Task UpdateAsync(Category category, CancellationToken cancellationToken = default)
    {
        UpdateCallCount++;
        return Task.CompletedTask;
    }

    public Task PlaceAsync(
        Category category,
        Guid categoryGroupId,
        int position,
        CancellationToken cancellationToken = default)
    {
        List<Category> source = SiblingsWithout(category.CategoryGroupId, category);
        List<Category> destination = category.CategoryGroupId == categoryGroupId
            ? source
            : SiblingsWithout(categoryGroupId, category);
        CategoryOrdering.Place(category, categoryGroupId, position, source, destination);
        PlaceCallCount++;
        categoryGroups.MarkHasCategories(categoryGroupId);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Category category, CancellationToken cancellationToken = default)
    {
        DeleteCallCount++;
        _categories.Remove(category);
        CategoryOrdering.CloseGap(
            SiblingsWithout(category.CategoryGroupId, category),
            category.CategoryGroupId);
        return Task.CompletedTask;
    }

    public Task<bool> HasTransactionsAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_referencedCategoryIds.Contains(categoryId));
    }

    public void MarkReferenced(Guid categoryId) => _referencedCategoryIds.Add(categoryId);

    public async Task<IReadOnlyList<CategoryDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CategoryGroupDto> groups = await categoryGroups.GetAllAsync(cancellationToken);
        Dictionary<Guid, CategoryGroupDto> groupById = groups.ToDictionary(group => group.Id);

        return _categories
            .OrderBy(category => groupById[category.CategoryGroupId].Position)
            .ThenBy(category => category.Position)
            .ThenBy(category => category.Id)
            .Select(category => new CategoryDto(
                category.Id,
                category.Name,
                category.Description,
                category.CategoryGroupId,
                groupById[category.CategoryGroupId].Name,
                category.Position))
            .ToList();
    }

    async Task<CategoryDto?> ICategoryReadService.GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CategoryDto> categories = await GetAllAsync(cancellationToken);
        return categories.SingleOrDefault(category => category.Id == id);
    }

    public async Task<Category> CreateAsync(
        Guid categoryGroupId,
        string name = "Groceries",
        string? description = null)
    {
        Category category = Category.Create(
            userId,
            categoryGroupId,
            name,
            description,
            await GetNextPositionAsync(categoryGroupId),
            timeProvider.GetUtcNow().UtcDateTime);
        await AddAsync(category);
        return category;
    }

    private List<Category> SiblingsWithout(Guid categoryGroupId, Category excluded) =>
        _categories
            .Where(category => category.CategoryGroupId == categoryGroupId && category != excluded)
            .OrderBy(category => category.Position)
            .ThenBy(category => category.Id)
            .ToList();
}
