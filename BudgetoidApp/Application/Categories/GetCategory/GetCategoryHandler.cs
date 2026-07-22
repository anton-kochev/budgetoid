using Application.Abstractions;

namespace Application.Categories.GetCategory;

public sealed class GetCategoryHandler(ICategoryReadService readService)
    : IQueryHandler<GetCategoryQuery, CategoryDto?>
{
    public async Task<CategoryDto?> HandleAsync(
        GetCategoryQuery query,
        CancellationToken cancellationToken = default)
    {
        return await readService.GetByIdAsync(query.Id, cancellationToken);
    }
}
