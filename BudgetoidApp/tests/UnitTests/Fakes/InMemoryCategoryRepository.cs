using Application.Categories;
using Application.CategoryGroups;
using Domain.Categories;
using TestSupport;

namespace UnitTests.Fakes;

public sealed class InMemoryCategoryRepository(
    Guid budgetId,
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
            category.Id == id && category.BudgetId == budgetId));
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
            // EVERY NARRATIVE MEMBER OF CategoryDto IS AN ENVELOPE IN BASE64URL NOW, so this projection
            // encodes off the entity rather than reading text off it — which is what
            // CategoryReadService.Shape does, and the reason this fake has to follow it: a handler test
            // asserting on a category name is asserting on a value the real read path would have
            // encoded, and a fake that handed back something else would be describing a system that
            // does not exist.
            //
            // Base64UrlText and not Convert.ToBase64String: unpadded base64url is the one alphabet
            // every binary member of this API crosses JSON in, and the two spellings differ on padding
            // and on alphabet slots 62 and 63.
            //
            // A null description stays null and must never gain a `?? string.Empty`: the empty string
            // is not a legal envelope, and the difference being carried is "no note" against "a note
            // somebody emptied".
            .Select(category => new CategoryDto(
                category.Id,
                Base64UrlText.Encode(category.Name.Envelope.Span),
                category.Description is null
                    ? null
                    : Base64UrlText.Encode(category.Description.Envelope.Span),
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

    /// <summary>
    /// A category in <paramref name="categoryGroupId" />, at the end of that group.
    /// </summary>
    /// <param name="label">
    /// What distinguishes this category's name from the next one's. It is NOT the category's name and is
    /// never read back as one — the column holds an envelope this side has no key for. It survives as
    /// the seed both halves of the name are derived from, so a caller that wants two rows to hold "the
    /// same name" passes one label twice.
    /// </param>
    /// <param name="descriptionLabel">
    /// The same, for the note, or <see langword="null" /> for a category that has none. It must differ
    /// from <paramref name="label" /> where a case cares which column a value landed in: the two
    /// fixtures run one filler, so a shared label produces identical bytes through both doors.
    /// </param>
    /// <remarks>
    /// The id is minted HERE and threaded in, because <see cref="Category.Create" /> no longer mints
    /// one: it is the associated data both narrative members were sealed against, so the factory takes
    /// it and never invents it.
    /// </remarks>
    public async Task<Category> CreateAsync(
        Guid categoryGroupId,
        string label = "Groceries",
        string? descriptionLabel = null)
    {
        Category category = Category.Create(
            Guid.CreateVersion7(),
            budgetId,
            categoryGroupId,
            SealedNarrative.Indexed(label),
            descriptionLabel is null ? null : SealedNarrative.Description(descriptionLabel),
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
