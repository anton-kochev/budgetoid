using Application.CategoryGroups;
using Domain.CategoryGroups;
using Domain.Security;

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
            // Through the production factory rather than a constructor call here: the members it
            // shapes are envelopes now, and a fake that encoded them its own way would let a handler
            // test agree with a spelling no route emits.
            .Select(CategoryGroupDto.FromCategoryGroup)
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

    /// <summary>
    /// Seeds one group at the next free position.
    /// </summary>
    /// <param name="name">The sealed name and its blind index — <see cref="TestSupport.SealedNarrative.Indexed" /> builds one from a label.</param>
    /// <param name="description">The sealed note, or <see langword="null" /> for a group filing none.</param>
    /// <remarks>
    /// <b>It takes the sealed values rather than the labels they were built from, and the extra noise at
    /// every call site is the point.</b> A <see cref="string" /> parameter here would read as a name and
    /// would put the one place a test could hand this fake plaintext behind a default argument. It also
    /// mints the identifier, which <see cref="CategoryGroup.Create" /> deliberately no longer does — a
    /// seeder is the one caller for which inventing one is honest, because nothing seeded here was
    /// sealed against a real id.
    /// </remarks>
    public async Task<CategoryGroup> CreateAsync(
        IndexedName name,
        NarrativeField? description = null)
    {
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            budgetId,
            name,
            description,
            await GetNextPositionAsync(),
            timeProvider.GetUtcNow().UtcDateTime);
        await AddAsync(group);
        return group;
    }
}
