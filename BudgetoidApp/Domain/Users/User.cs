using Domain.Common;

namespace Domain.Users;

public sealed class User
{
    private User()
    {
    }

    public Guid Id { get; private set; }
    public Email Email { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }

    public static User Create(string email, DateTime createdAtUtc)
    {
        Email emailValue = Email.Create(email);

        return new User
        {
            Id = Guid.CreateVersion7(),
            Email = emailValue,
            CreatedAtUtc = createdAtUtc
        };
    }
}
