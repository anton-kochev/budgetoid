using Application.Abstractions;

namespace IntegrationTests;

public sealed class TestUserContext(Guid userId) : IUserContext
{
    public Guid UserId { get; } = userId;
}
