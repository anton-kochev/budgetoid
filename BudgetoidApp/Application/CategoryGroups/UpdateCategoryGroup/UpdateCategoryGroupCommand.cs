namespace Application.CategoryGroups.UpdateCategoryGroup;

public sealed record UpdateCategoryGroupCommand(Guid Id, string Name, string? Description);
