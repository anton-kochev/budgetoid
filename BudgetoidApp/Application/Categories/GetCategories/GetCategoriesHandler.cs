using Application.Abstractions;

namespace Application.Categories.GetCategories;

public sealed class GetCategoriesHandler(ICategoryReadService readService)
    : IQueryHandler<GetCategoriesQuery, CategoryListResponse>
{
    public async Task<CategoryListResponse> HandleAsync(
        GetCategoriesQuery query,
        CancellationToken cancellationToken = default)
    {
        return new CategoryListResponse(await readService.GetAllAsync(cancellationToken));
    }
}
