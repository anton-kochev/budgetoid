namespace Application.Categories;

public sealed record CategoryDto(
    Guid Id,
    string Name,
    string? Description,
    Guid CategoryGroupId,
    string CategoryGroupName,
    int Position);

public sealed record CategoryListResponse(IReadOnlyList<CategoryDto> Items);
