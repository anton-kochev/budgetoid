using Application.CategoryGroups;
using Domain.CategoryGroups;

namespace UnitTests.Fakes;

public sealed class InMemoryCategoryGroupRepository(Guid budgetId, TimeProvider timeProvider)
    : ICategoryGroupRepository, ICategoryGroupReadService
{
    private readonly List<CategoryGroup> _categoryGroups = [];
    private readonly HashSet<Guid> _groupsWithCategories = [];

    public int AddCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }
    public int MoveCallCount { get; private set; }

    public Task AddAsync(CategoryGroup categoryGroup, CancellationToken cancellationToken = default)
    {
        AddCallCount++;
        _categoryGroups.Add(categoryGroup);
        return Task.CompletedTask;
    }

    public Task<CategoryGroup?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_categoryGroups.SingleOrDefault(group =>
            group.Id == id && group.BudgetId == budgetId));
    }

    public Task<int> GetNextPositionAsync(CancellationToken cancellationToken = default)
    {
        int position = _categoryGroups.Count == 0
            ? 0
            : _categoryGroups.Max(group => group.Position) + 1;
        return Task.FromResult(position);
    }

    public Task UpdateAsync(CategoryGroup categoryGroup, CancellationToken cancellationToken = default)
    {
        UpdateCallCount++;
        return Task.CompletedTask;
    }

    public Task MoveToPositionAsync(
        CategoryGroup categoryGroup,
        int position,
        CancellationToken cancellationToken = default)
    {
        CategoryGroupOrdering.MoveToPosition(
            categoryGroup,
            position,
            _categoryGroups.OrderBy(group => group.Position).ThenBy(group => group.Id).ToList());
        MoveCallCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CategoryGroup categoryGroup, CancellationToken cancellationToken = default)
    {
        DeleteCallCount++;
        _categoryGroups.Remove(categoryGroup);
        CategoryGroupOrdering.CloseGap(
            _categoryGroups.OrderBy(group => group.Position).ThenBy(group => group.Id).ToList());
        return Task.CompletedTask;
    }

    public Task<bool> HasCategoriesAsync(Guid categoryGroupId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_groupsWithCategories.Contains(categoryGroupId));
    }

    public void MarkHasCategories(Guid categoryGroupId) => _groupsWithCategories.Add(categoryGroupId);

    public Task<IReadOnlyList<CategoryGroupDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CategoryGroupDto> groups = _categoryGroups
            .OrderBy(group => group.Position)
            .ThenBy(group => group.Id)
            .Select(group => new CategoryGroupDto(
                group.Id,
                group.Name,
                group.Description,
                group.Position))
            .ToList();
        return Task.FromResult(groups);
    }

    async Task<CategoryGroupDto?> ICategoryGroupReadService.GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CategoryGroupDto> groups = await GetAllAsync(cancellationToken);
        return groups.SingleOrDefault(group => group.Id == id);
    }

    public async Task<CategoryGroup> CreateAsync(
        string name = "Essential Obligations",
        string? description = null)
    {
        CategoryGroup group = CategoryGroup.Create(
            budgetId,
            name,
            description,
            await GetNextPositionAsync(),
            timeProvider.GetUtcNow().UtcDateTime);
        await AddAsync(group);
        return group;
    }
}
