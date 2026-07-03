using Application.Abstractions;

namespace Application.Groups.GetGroups;

public sealed class GetGroupsHandler(IGroupReadService readService)
    : IQueryHandler<GetGroupsQuery, GroupListResponse>
{
    public async Task<GroupListResponse> HandleAsync(
        GetGroupsQuery query,
        CancellationToken cancellationToken = default)
    {
        return new GroupListResponse(await readService.GetAllAsync(cancellationToken));
    }
}
