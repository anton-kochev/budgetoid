namespace Domain.Categories;

public interface ICategoryRepository
{
    Task AddAsync(Category category, CancellationToken cancellationToken = default);
    Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<int> GetNextPositionAsync(Guid categoryGroupId, CancellationToken cancellationToken = default);
    Task UpdateAsync(Category category, CancellationToken cancellationToken = default);
    Task PlaceAsync(Category category, Guid categoryGroupId, int position, CancellationToken cancellationToken = default);
    Task DeleteAsync(Category category, CancellationToken cancellationToken = default);
    Task<bool> HasTransactionsAsync(Guid categoryId, CancellationToken cancellationToken = default);
}
