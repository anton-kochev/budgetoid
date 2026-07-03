namespace Application.Groups;

public sealed record GroupDto(Guid Id, string Name, string? Description);

public sealed record GroupListResponse(IReadOnlyList<GroupDto> Items);
