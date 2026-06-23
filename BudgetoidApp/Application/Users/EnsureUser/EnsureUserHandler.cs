using Application.Abstractions;
using Domain.Users;

namespace Application.Users.EnsureUser;

public sealed class EnsureUserHandler(
    IUserRepository repository,
    TimeProvider timeProvider) : ICommandHandler<EnsureUserCommand, Guid>
{
    public async Task<Guid> HandleAsync(EnsureUserCommand command, CancellationToken cancellationToken = default)
    {
        User? existing = await repository.FindByGoogleSubjectAsync(command.GoogleSubject, cancellationToken);
        if (existing is not null)
        {
            Email previousEmail = existing.Email;
            string? previousDisplayName = existing.DisplayName;

            existing.UpdateProfile(command.Email, command.DisplayName);
            if (existing.Email != previousEmail || existing.DisplayName != previousDisplayName)
            {
                await repository.UpdateProfileAsync(existing, cancellationToken);
            }

            return existing.Id;
        }

        User user = User.Create(
            command.GoogleSubject,
            command.Email,
            command.DisplayName,
            timeProvider.GetUtcNow().UtcDateTime);
        if (await repository.TryAddAsync(user, cancellationToken))
        {
            return user.Id;
        }

        User? concurrentExisting = await repository.FindByGoogleSubjectAsync(command.GoogleSubject, cancellationToken);

        return concurrentExisting?.Id
               ?? throw new InvalidOperationException("Unique user insert failed but user could not be re-read.");
    }
}
