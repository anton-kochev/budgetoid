namespace Application.Categories.PlaceCategory;

public sealed record PlaceCategoryCommand(Guid Id, Guid CategoryGroupId, int Position);
