using Application.Abstractions;
using Domain.Budgets;
using Domain.Common;
using Domain.Users;

namespace Application.Users.EnsureUser;

/// <summary>
/// The minting half of provisioning: find the account, or bring it into existence. Reachable only from
/// a route group carrying <c>ProvisionsUserAttribute</c>.
/// </summary>
/// <remarks>
/// Resolving is delegated to <see cref="ResolveUserHandler" /> rather than repeated here, so the
/// returning-user branch — and with it the budget heal — is defined in exactly one place, and this
/// class is only ever the part that creates.
/// </remarks>
public sealed class EnsureUserHandler(
    IUserRepository repository,
    IBudgetRepository budgetRepository,
    IUserContextWriter userContextWriter,
    TimeProvider timeProvider,
    ResolveUserHandler resolveUserHandler) : ICommandHandler<EnsureUserCommand, ProvisionedUser>
{
    public async Task<ProvisionedUser> HandleAsync(
        EnsureUserCommand command,
        CancellationToken cancellationToken = default)
    {
        // Resolve first, mint only when nothing resolved. Everything a returning request needs —
        // publishing the identity, and healing a budget insert that was lost — already happened in
        // there, so nothing below runs for an account that exists.
        ProvisionedUser? resolved = await resolveUserHandler.HandleAsync(
            new ResolveUserCommand(command.GoogleSubject),
            cancellationToken);
        if (resolved is not null)
        {
            return resolved;
        }

        Guid userId = await CreateUserIdAsync(command, cancellationToken);

        // Reached on the conflict path too, where the id adopted below belongs to an account that
        // already existed and may already own a budget — hence find-or-create rather than a bare insert.
        Guid budgetId = await DefaultBudgetProvisioning.EnsureDefaultBudgetIdAsync(
            budgetRepository,
            timeProvider,
            userId,
            cancellationToken);

        return new ProvisionedUser(userId, budgetId);
    }

    // Every publication below lands before the next statement that touches a policed table, which is
    // the whole ordering contract: app.current_user_id reaches the database on the next connection
    // open, so an id published afterwards is an id that statement ran without.
    private async Task<Guid> CreateUserIdAsync(EnsureUserCommand command, CancellationToken cancellationToken)
    {
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
            // The id published before the insert is still in request scope, and it names a row that was
            // never written. That is left standing on purpose. Nothing reads it — the request ends in a
            // 409 — and anything that later did would fail closed rather than wrong: under an identity
            // with no row, every policed statement reachable from here returns zero rows or 42501, and
            // the ambient budget is unresolved besides, because ResolveUser cleared it and nothing on
            // this path ever called ResolveBudget. So a budget-scoped read added below refuses outright
            // rather than scoping to a stranger. Wrong rows is the only failure mode that would matter,
            // and neither the ''::uuid policy shape nor an unresolved budget produces it.
            //
            // What clearing the id would cost is no longer "widening a single-capability interface":
            // IUserContextWriter already carries two members and already mutates two pieces of request
            // state, so a third would not change its character. It is declined on its own merits — a
            // ClearUser() with one caller, guarding a state nothing in the request can observe, is a
            // capability to unname a request bought to fix nothing, and the suite would have no way to
            // hold it honest.
            //
            // The ruling rests on nothing running after this throw. Add post-provisioning middleware, an
            // audit logger, anything that reads the ambient identity late in the request, and it runs as
            // a user that does not exist — reconsider this then rather than inherit it, and note the
            // price has dropped since it was last weighed.
            throw new ConflictException("This email address is already linked to a different Google account.");
        }

        // Overwrites the id published above, which named a row that was never written. The session
        // carries the last word into the budgets read and on into the rest of the request, so leaving
        // the loser's id there would police every later statement against an account that does not
        // exist.
        userContextWriter.ResolveUser(concurrentUserId.Value);

        return concurrentUserId.Value;
    }
}
