namespace Application.Abstractions;

public interface IUserContext
{
    Guid UserId { get; }
}

public sealed class FakeUserContext : IUserContext
{
    public Guid UserId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
