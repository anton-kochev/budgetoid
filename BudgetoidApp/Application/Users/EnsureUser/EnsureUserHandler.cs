using Application.Abstractions;
using Domain.Budgets;
using Domain.Common;
using Domain.Users;

namespace Application.Users.EnsureUser;

public sealed class EnsureUserHandler(
    IUserRepository repository,
    IBudgetRepository budgetRepository,
    IUserContextWriter userContextWriter,
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

    // Every publication below lands before the next statement that touches a policed table, which is
    // the whole ordering contract: app.current_user_id reaches the database on the next connection
    // open, so an id published afterwards is an id that statement ran without.
    private async Task<Guid> EnsureUserIdAsync(EnsureUserCommand command, CancellationToken cancellationToken)
    {
        // Reads credentials alone — the one table still exempt from row-level security, because it
        // is what answers "who is asking".
        Guid? existingUserId = await repository.FindUserIdByFederatedCredentialAsync(
            Credential.GoogleProvider,
            command.GoogleSubject,
            cancellationToken);
        if (existingUserId is not null)
        {
            // Nothing about the stored profile is touched here, and the credential row was all this
            // path read. The provider gates registration and is not consulted again, so what it now
            // reports about this account is not authority to change anything: an email change is a
            // separate exchange the user deliberately initiates. Refreshing here would apply one
            // nobody asked for.
            userContextWriter.ResolveUser(existingUserId.Value);
            return existingUserId.Value;
        }

        // One `now` for both rows: the user and the credential that resolves to it come into
        // existence in the same save, so they carry the same creation instant.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        User user = User.Create(command.Email, now);
        Credential credential = Credential.CreateFederated(
            user.Id,
            Credential.GoogleProvider,
            command.GoogleSubject,
            now);

        // Before the insert, not after: User.Create mints the id with Guid.CreateVersion7 on this
        // side, so it exists before the row does, and the users INSERT is checked against
        // app.current_user_id — publish afterwards and WITH CHECK refuses every new account.
        userContextWriter.ResolveUser(user.Id);
        if (await repository.TryAddAsync(user, credential, cancellationToken))
        {
            return user.Id;
        }

        // The insert lost to an existing row on the credential's (provider, subject) or on the email,
        // and only this re-read separates the two. A reported unique violation means the conflicting
        // transaction committed — under read committed the insert waits for it, and would have
        // succeeded had it aborted — so a winning credential on this subject is visible here. Finding
        // none therefore proves the subject was never duplicated and the email alone collided, with a
        // different account holding it.
        Guid? concurrentUserId = await repository.FindUserIdByFederatedCredentialAsync(
            Credential.GoogleProvider,
            command.GoogleSubject,
            cancellationToken);
        if (concurrentUserId is null)
        {
            throw new ConflictException("This email address is already linked to a different Google account.");
        }

        // Overwrites the id published above, which named a row that was never written. The session
        // carries the last word into the budgets read and on into the rest of the request, so leaving
        // the loser's id there would police every later statement against an account that does not
        // exist.
        userContextWriter.ResolveUser(concurrentUserId.Value);

        return concurrentUserId.Value;
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
