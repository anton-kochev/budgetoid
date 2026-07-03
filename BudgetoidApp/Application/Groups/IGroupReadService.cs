namespace Application.Groups;

public interface IGroupReadService
{
    Task<IReadOnlyList<GroupDto>> GetAllAsync(CancellationToken cancellationToken = default);
}
