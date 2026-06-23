namespace Application.Users.EnsureUser;

public sealed record EnsureUserCommand(string GoogleSubject, string Email, string? DisplayName);
