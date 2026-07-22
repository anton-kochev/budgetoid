namespace Application.Categories;

public interface ICategoryReadService
{
    Task<IReadOnlyList<CategoryDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<CategoryDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
