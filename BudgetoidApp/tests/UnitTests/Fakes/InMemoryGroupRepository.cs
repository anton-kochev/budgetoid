using Application.Groups;
using Domain.Groups;

namespace UnitTests.Fakes;

public sealed class InMemoryGroupRepository(Guid userId, TimeProvider timeProvider) : IGroupRepository, IGroupReadService
{
    private readonly List<Group> _groups = [];
    private readonly HashSet<Guid> _referencedGroupIds = [];

    public int AddCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }

    public Task AddAsync(Group group, CancellationToken cancellationToken = default)
    {
        AddCallCount++;
        _groups.Add(group);
        return Task.CompletedTask;
    }

    public Task<Group?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Group? group = _groups.SingleOrDefault(group => group.Id == id && group.UserId == userId);
        return Task.FromResult(group);
    }

    public Task UpdateAsync(Group group, CancellationToken cancellationToken = default)
    {
        UpdateCallCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Group group, CancellationToken cancellationToken = default)
    {
        DeleteCallCount++;
        _groups.Remove(group);
        return Task.CompletedTask;
    }

    public Task<bool> HasTransactionsAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_referencedGroupIds.Contains(groupId));
    }

    public void MarkReferenced(Guid groupId) => _referencedGroupIds.Add(groupId);

    public Task<IReadOnlyList<GroupDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GroupDto> groups = _groups
            .OrderBy(group => group.Name)
            .Select(group => new GroupDto(group.Id, group.Name, group.Description))
            .ToList();

        return Task.FromResult(groups);
    }

    public async Task<Group> CreateAsync(string name = "Groceries", string? description = null)
    {
        Group group = Group.Create(userId, name, description, timeProvider.GetUtcNow().UtcDateTime);
        await AddAsync(group);
        return group;
    }
}
