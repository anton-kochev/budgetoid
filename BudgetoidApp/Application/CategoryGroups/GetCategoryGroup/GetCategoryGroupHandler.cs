using Application.Abstractions;

namespace Application.CategoryGroups.GetCategoryGroup;

public sealed class GetCategoryGroupHandler(ICategoryGroupReadService readService)
    : IQueryHandler<GetCategoryGroupQuery, CategoryGroupDto?>
{
    public async Task<CategoryGroupDto?> HandleAsync(
        GetCategoryGroupQuery query,
        CancellationToken cancellationToken = default)
    {
        return await readService.GetByIdAsync(query.Id, cancellationToken);
    }
}
