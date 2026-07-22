namespace Domain.CategoryGroups;

public interface ICategoryGroupRepository
{
    Task AddAsync(CategoryGroup categoryGroup, CancellationToken cancellationToken = default);
    Task<CategoryGroup?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<int> GetNextPositionAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(CategoryGroup categoryGroup, CancellationToken cancellationToken = default);
    Task MoveToPositionAsync(CategoryGroup categoryGroup, int position, CancellationToken cancellationToken = default);
    Task DeleteAsync(CategoryGroup categoryGroup, CancellationToken cancellationToken = default);
    Task<bool> HasCategoriesAsync(Guid categoryGroupId, CancellationToken cancellationToken = default);
}
