namespace Application.CategoryGroups;

public sealed record CategoryGroupDto(
    Guid Id,
    string Name,
    string? Description,
    int Position);

public sealed record CategoryGroupListResponse(IReadOnlyList<CategoryGroupDto> Items);
