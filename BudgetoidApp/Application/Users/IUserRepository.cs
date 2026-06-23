using Domain.Users;

namespace Application.Users;

public interface IUserRepository
{
    Task<User?> FindByGoogleSubjectAsync(string googleSubject, CancellationToken cancellationToken = default);
    Task<bool> TryAddAsync(User user, CancellationToken cancellationToken = default);
    Task UpdateProfileAsync(User user, CancellationToken cancellationToken = default);
}
