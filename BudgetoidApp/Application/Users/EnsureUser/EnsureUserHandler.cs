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
/// <para>
/// Resolving is delegated to <see cref="ResolveUserHandler" /> rather than repeated here, so the
/// returning-user branch is defined in exactly one place and this class is only ever the part that
/// creates.
/// </para>
/// <para>
/// <b>No <c>ITransactionalExecutor</c> wraps the mint, and that is a correctness ruling rather than a
/// cost one.</b> Such an executor opens its transaction through <c>CreateExecutionStrategy()</c>, and
/// <c>BeginTransactionAsync</c> is what opens the connection — which is when
/// <c>SessionContextInterceptor</c> writes <c>app.current_user_id</c>. Inside a transaction the
/// interceptor runs once, at the begin, so any wrap whose delegate contains the publication below
/// configures the connection while the setting is still empty and the <c>users</c> INSERT meets
/// <c>''::uuid</c> in its <c>WITH CHECK</c> — a <c>22P02</c>, the exact trap already documented for the
/// passkey assertion path. Publishing outside the wrap fixes it, at the price of a new ordering rule
/// nobody may re-break and a retry that must not re-publish. One save through
/// <see cref="IUserRepository.TryAddAsync" /> carries no such rule.
/// </para>
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
        // Resolve first, mint only when nothing resolved. Publishing the identity and reading the
        // budget already happened in there, so nothing below runs for an account that exists.
        ProvisionedUser? resolved = await resolveUserHandler.HandleAsync(
            new ResolveUserCommand(command.GoogleSubject),
            cancellationToken);
        if (resolved is not null)
        {
            return resolved;
        }

        return await CreateAsync(command, cancellationToken);
    }

    // Every publication below lands before the next statement that touches a policed table, which is
    // the whole ordering contract: app.current_user_id reaches the database on the next connection
    // open, so an id published afterwards is an id that statement ran without.
    private async Task<ProvisionedUser> CreateAsync(
        EnsureUserCommand command,
        CancellationToken cancellationToken)
    {
        // One `now` for all three rows: the user, the credential that resolves to it and the budget it
        // owns come into existence in the same save, so they carry the same creation instant.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        User user = User.Create(command.Email, now);
        Credential credential = Credential.CreateFederated(
            user.Id,
            Credential.GoogleProvider,
            command.GoogleSubject,
            now);
        Budget defaultBudget = Budget.CreateDefault(user.Id, now);

        // Before the insert, not after: User.Create mints the id with Guid.CreateVersion7 on this
        // side, so it exists before the row does, and the users INSERT is checked against
        // app.current_user_id — publish afterwards and WITH CHECK refuses every new account.
        userContextWriter.ResolveUser(user.Id);
        if (await repository.TryAddAsync(user, credential, defaultBudget, cancellationToken))
        {
            return new ProvisionedUser(user.Id, defaultBudget.Id);
        }

        // The insert lost to an existing row on the credential's (provider, subject) or on the email,
        // and only this re-read separates the two. A reported unique violation means the conflicting
        // transaction committed — under read committed the insert waits for it, and would have
        // succeeded had it aborted — so a winning credential on this subject is visible here. Finding
        // none therefore proves the subject was never duplicated and the email alone collided, with a
        // different account holding it.
        Guid? winnerId = await repository.FindUserIdByFederatedCredentialAsync(
            Credential.GoogleProvider,
            command.GoogleSubject,
            cancellationToken);
        if (winnerId is null)
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

        // Overwrites the id published above, which named a row that was never written — and it lands
        // before the budget read, which is what makes the ordering load-bearing rather than tidy:
        // budgets is policed by user_isolation, so a read still carrying the loser's phantom id comes
        // back empty and this method would report a broken invariant about an intact account. The
        // session also carries the last word into the rest of the request.
        userContextWriter.ResolveUser(winnerId.Value);

        // This save wrote nothing, so the only budget there is to report is the winner's — and it is
        // certainly there: the reported unique violation means the winner's transaction committed, and
        // one save means that transaction contained its budget row. Minting one here instead would
        // leave a second, orphaned budget nobody ever opens.
        Budget winnersBudget = await budgetRepository.FindFirstForUserAsync(winnerId.Value, cancellationToken)
                               ?? throw new InvalidOperationException(
                                   "The account that won the credential race owns no budget, which one "
                                   + "save cannot produce.");

        return new ProvisionedUser(winnerId.Value, winnersBudget.Id);
    }
}
