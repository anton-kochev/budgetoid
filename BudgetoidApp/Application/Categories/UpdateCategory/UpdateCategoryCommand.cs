namespace Application.Categories.UpdateCategory;

public sealed record UpdateCategoryCommand(Guid Id, string Name, string? Description);
