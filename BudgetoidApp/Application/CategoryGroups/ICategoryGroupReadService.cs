namespace Application.CategoryGroups;

public interface ICategoryGroupReadService
{
    Task<IReadOnlyList<CategoryGroupDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<CategoryGroupDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
