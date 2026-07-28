using Application.Abstractions;
using Domain.Budgets;
using Domain.Common;
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
                // The result is ignored deliberately. A false means another user holds that email, so
                // the refresh was rolled back and this user keeps its stored one — identity comes from
                // the google_subject, and a stale cached attribute must not lock anyone out.
                _ = await repository.UpdateProfileAsync(existing, cancellationToken);
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

        // The insert lost to an existing row on the subject or on the email, and only this re-read
        // separates the two. A reported unique violation means the conflicting transaction committed
        // — under read committed the insert waits for it, and would have succeeded had it aborted —
        // so a winner on this subject is visible here. Finding none therefore proves the subject was
        // never duplicated and the email alone collided, with a different Google account holding it.
        User? concurrentExisting = await repository.FindByGoogleSubjectAsync(command.GoogleSubject, cancellationToken);

        return concurrentExisting?.Id
               ?? throw new ConflictException("This email address is already linked to a different Google account.");
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
