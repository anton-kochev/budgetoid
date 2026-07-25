using Application.Abstractions;
using Domain.Budgets;
using Domain.Users;

namespace Application.Users.EnsureUser;

public sealed class EnsureUserHandler(
    IUserRepository repository,
    IBudgetRepository budgetRepository,
    TimeProvider timeProvider) : ICommandHandler<EnsureUserCommand, ProvisionedUser>
{
    public async Task<ProvisionedUser> HandleAsync(
        EnsureUserCommand command,
        CancellationToken cancellationToken = default)
    {
        Guid userId = await EnsureUserIdAsync(command, cancellationToken);

        // Runs on the existing-user path too: it heals a provisioning that inserted the user row but
        // lost its budget insert, which would otherwise leave every budget-scoped query empty.
        Guid budgetId = await EnsureDefaultBudgetIdAsync(userId, cancellationToken);

        return new ProvisionedUser(userId, budgetId);
    }

    private async Task<Guid> EnsureUserIdAsync(EnsureUserCommand command, CancellationToken cancellationToken)
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

        // A concurrent request won the unique insert; re-read to adopt its row.
        User? concurrentExisting = await repository.FindByGoogleSubjectAsync(command.GoogleSubject, cancellationToken);

        return concurrentExisting?.Id
               ?? throw new InvalidOperationException("Unique user insert failed but user could not be re-read.");
    }

    private async Task<Guid> EnsureDefaultBudgetIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        Budget? existing = await budgetRepository.FindFirstForUserAsync(userId, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        Budget budget = Budget.CreateDefault(userId, timeProvider.GetUtcNow().UtcDateTime);
        if (await budgetRepository.TryAddAsync(budget, cancellationToken))
        {
            return budget.Id;
        }

        // A concurrent request won the unique insert; re-read to adopt its row.
        Budget? concurrentExisting = await budgetRepository.FindFirstForUserAsync(userId, cancellationToken);

        return concurrentExisting?.Id
               ?? throw new InvalidOperationException("Unique budget insert failed but budget could not be re-read.");
    }
}
