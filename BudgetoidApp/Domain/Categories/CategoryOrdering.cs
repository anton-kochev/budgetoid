using Domain.Common;

namespace Domain.Categories;

/// <summary>
/// Owns the ordering invariant for categories: positions inside a category group are
/// contiguous and zero-based, so every insertion, move, or removal reindexes the
/// affected groups. Persistence and test doubles delegate here instead of
/// reimplementing the algorithm.
/// </summary>
public static class CategoryOrdering
{
    /// <summary>
    /// Inserts <paramref name="category"/> at <paramref name="position"/> among the
    /// destination siblings and reindexes both affected groups. Sibling lists must be
    /// ordered and must not contain the moved category; pass the same list twice when
    /// the category stays in its current group.
    /// </summary>
    public static void Place(
        Category category,
        Guid destinationCategoryGroupId,
        int position,
        IReadOnlyList<Category> sourceSiblings,
        IReadOnlyList<Category> destinationSiblings)
    {
        if (position < 0 || position > destinationSiblings.Count)
        {
            throw PositionValidationException();
        }

        Guid sourceCategoryGroupId = category.CategoryGroupId;
        List<Category> destination = [.. destinationSiblings];
        destination.Insert(position, category);

        if (sourceCategoryGroupId != destinationCategoryGroupId)
        {
            Reindex(sourceSiblings, sourceCategoryGroupId);
        }

        Reindex(destination, destinationCategoryGroupId);
    }

    /// <summary>
    /// Reindexes the categories left in a group after one of them was removed.
    /// </summary>
    public static void CloseGap(
        IReadOnlyList<Category> remainingSiblings,
        Guid categoryGroupId)
    {
        Reindex(remainingSiblings, categoryGroupId);
    }

    private static void Reindex(IReadOnlyList<Category> categories, Guid categoryGroupId)
    {
        for (int index = 0; index < categories.Count; index++)
        {
            categories[index].Place(categoryGroupId, index);
        }
    }

    private static ValidationException PositionValidationException() => new(
        new Dictionary<string, string[]>
        {
            [nameof(Category.Position)] =
                ["Position is outside the destination category group."],
        });
}
