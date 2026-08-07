using Application.Abstractions;
using Domain.Budgets;
using Domain.Users;

namespace Application.Users.EnsureUser;

/// <summary>
/// The resolve-only half of provisioning: it answers who is asking, and it never brings an account into
/// existence. Returns <see langword="null" /> when no credential holds the subject.
/// </summary>
/// <remarks>
/// <para>
/// This is the only half a request on a route that may not mint an account is allowed to run — see
/// <c>ProvisionsUserAttribute</c> for which routes may and why the permission is opt-in.
/// <see cref="EnsureUserHandler" /> runs it first and mints only on <see langword="null" />, so the
/// returning-user branch lives here alone rather than in two classes.
/// </para>
/// <para>
/// <b>Why this is an <see cref="ICommandHandler{TCommand,TResult}" /> and not a query: it publishes the
/// request's identity.</b> It writes no row any more, but <see cref="IUserContextWriter.ResolveUser" />
/// is the single act the whole row-level-security model rests on — every policed statement for the rest
/// of the request is decided by the id set here. Reclassifying this as a query would advertise "no
/// effects, safe to call anywhere" about the one call where that is most dangerously false.
/// </para>
/// </remarks>
public sealed class ResolveUserHandler(
    IUserRepository repository,
    IBudgetRepository budgetRepository,
    IUserContextWriter userContextWriter) : ICommandHandler<ResolveUserCommand, ProvisionedUser?>
{
    public async Task<ProvisionedUser?> HandleAsync(
        ResolveUserCommand command,
        CancellationToken cancellationToken = default)
    {
        // Reads credentials alone — the one table still exempt from row-level security, because it is
        // what answers "who is asking".
        Guid? userId = await repository.FindUserIdByFederatedCredentialAsync(
            Credential.GoogleProvider,
            command.GoogleSubject,
            cancellationToken);
        if (userId is null)
        {
            return null;
        }

        // Nothing about the stored profile is touched here, and the credential row was all this path
        // read. The provider gates registration and is not consulted again, so what it now reports about
        // this account is not authority to change anything: an email change is a separate exchange the
        // user deliberately initiates. Refreshing here would apply one nobody asked for — and this
        // handler is not even told the address, so it could not.
        //
        // The publication lands before the budget read below, which is the whole ordering contract:
        // app.current_user_id reaches the database on the next connection open, so an id published
        // afterwards is an id that statement ran without.
        userContextWriter.ResolveUser(userId.Value);

        // Read, never repaired. An account and its budget land in one save, so an account without one
        // is a state nothing produces; meeting it here means the invariant broke, and inventing a
        // tenant to carry on with would hide that. InvalidOperationException rather than a
        // NotFoundException, which would tell a signed-in person their account is gone when it is not.
        Budget budget = await budgetRepository.FindFirstForUserAsync(userId.Value, cancellationToken)
                        ?? throw new InvalidOperationException(
                            "A resolved account owns no budget, which provisioning cannot produce.");

        return new ProvisionedUser(userId.Value, budget.Id);
    }
}
