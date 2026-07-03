namespace Application.Groups.UpdateGroup;

public sealed record UpdateGroupCommand(Guid Id, string Name, string? Description);
