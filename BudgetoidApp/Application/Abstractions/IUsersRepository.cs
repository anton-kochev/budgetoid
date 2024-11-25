using Domain.Entities;

namespace Application.Abstractions;

public interface IUsersRepository
{
    Task<User> CreateUserAsync(string email, CancellationToken cancellationToken);
    Task<Guid> GetUserIdAsync(string email, CancellationToken cancellationToken);
}
