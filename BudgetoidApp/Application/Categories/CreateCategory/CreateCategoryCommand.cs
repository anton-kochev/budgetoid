namespace Application.Categories.CreateCategory;

public sealed record CreateCategoryCommand(
    string Name,
    string? Description,
    Guid CategoryGroupId);
