using Application.Abstractions;

namespace Application.CategoryGroups.GetCategoryGroups;

public sealed class GetCategoryGroupsHandler(ICategoryGroupReadService readService)
    : IQueryHandler<GetCategoryGroupsQuery, CategoryGroupListResponse>
{
    public async Task<CategoryGroupListResponse> HandleAsync(
        GetCategoryGroupsQuery query,
        CancellationToken cancellationToken = default)
    {
        return new CategoryGroupListResponse(await readService.GetAllAsync(cancellationToken));
    }
}
