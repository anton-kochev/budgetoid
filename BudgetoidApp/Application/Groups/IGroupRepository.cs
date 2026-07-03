using Domain.Groups;

namespace Application.Groups;

public interface IGroupRepository
{
    Task AddAsync(Group group, CancellationToken cancellationToken = default);
    Task<Group?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateAsync(Group group, CancellationToken cancellationToken = default);
    Task DeleteAsync(Group group, CancellationToken cancellationToken = default);
    Task<bool> HasTransactionsAsync(Guid groupId, CancellationToken cancellationToken = default);
}
