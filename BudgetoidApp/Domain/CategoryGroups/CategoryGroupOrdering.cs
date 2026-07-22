using Domain.Common;

namespace Domain.CategoryGroups;

/// <summary>
/// Owns the ordering invariant for category groups: positions are contiguous and
/// zero-based across a user's groups, so every move or removal reindexes the whole
/// list. Persistence and test doubles delegate here instead of reimplementing the
/// algorithm.
/// </summary>
public static class CategoryGroupOrdering
{
    /// <summary>
    /// Moves <paramref name="categoryGroup"/> to <paramref name="position"/> and
    /// reindexes the list contiguously. <paramref name="orderedCategoryGroups"/> is the
    /// user's full ordered list, including the moved group.
    /// </summary>
    public static void MoveToPosition(
        CategoryGroup categoryGroup,
        int position,
        IReadOnlyList<CategoryGroup> orderedCategoryGroups)
    {
        if (position < 0 || position >= orderedCategoryGroups.Count)
        {
            throw PositionValidationException();
        }

        List<CategoryGroup> ordered = orderedCategoryGroups
            .Where(group => group.Id != categoryGroup.Id)
            .ToList();
        ordered.Insert(position, categoryGroup);
        Reindex(ordered);
    }

    /// <summary>
    /// Reindexes the groups left after one of them was removed.
    /// </summary>
    public static void CloseGap(IReadOnlyList<CategoryGroup> remainingCategoryGroups)
    {
        Reindex(remainingCategoryGroups);
    }

    private static void Reindex(IReadOnlyList<CategoryGroup> categoryGroups)
    {
        for (int index = 0; index < categoryGroups.Count; index++)
        {
            categoryGroups[index].SetPosition(index);
        }
    }

    private static ValidationException PositionValidationException() => new(
        new Dictionary<string, string[]>
        {
            [nameof(CategoryGroup.Position)] =
                ["Position is outside the category group list."],
        });
}
