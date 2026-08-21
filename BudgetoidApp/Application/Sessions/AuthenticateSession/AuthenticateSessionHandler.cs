using Application.Abstractions;
using Application.Users;
using Domain.Budgets;
using Domain.Sessions;

namespace Application.Sessions.AuthenticateSession;

/// <summary>
/// Turns the digest of a presented session handle into the request's identity and its ambient budget.
/// Returns <see langword="null"/> when no session in the installation is presented by it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A command rather than a query, and it writes no row.</b> Nothing here inserts, updates or
/// deletes, so the classification looks wrong until you ask what a query promises. This is the one
/// place a request acquires an identity: <see cref="IUserContextWriter.ResolveUser"/> is the single act
/// the whole row-level-security model rests on, and every policed statement for the rest of the request
/// is decided by the id published here. Calling it a query would advertise "no effects, safe to call
/// anywhere" about the call where that is most dangerously false — and the label is the only thing
/// standing between a later reader and a second, cheaper-looking way to ask who is signed in.
/// </para>
/// <para>
/// <b>The order of the two reads is the decision ADR 0019 exists for, and it cannot be rearranged.</b>
/// <c>session_tokens</c> is <b>exempt</b> from row-level security and is read first, naming no owner,
/// because the request arrives holding a cookie and nothing else — there is no account to scope by
/// until this read has answered. <c>sessions</c> is <b>policed</b> by <c>user_isolation</c> and is read
/// second, and it works only because the publication between them put <c>app.current_user_id</c> where
/// <c>SessionContextInterceptor</c> will find it at the next connection open. Swap the two and the
/// policy meets <c>''::uuid</c> and raises <c>22P02</c> on every authenticated request in the product.
/// </para>
/// <para>
/// <b>No transaction anywhere on this path, which is the same trap from the other side.</b> One opened
/// before the publication configures its connection while <c>app.current_user_id</c> is still empty, and
/// every policed statement inside it fails <c>22P02</c> — the trap <c>CompleteAssertionHandler</c>,
/// <c>RedeemRecoveryCodeHandler</c> and <c>RegisterAccountHandler</c> each already carry in their own
/// remarks. Nothing here writes, so there is nothing an atomic unit would be protecting; the two reads
/// are two round trips and are stated as the cost rather than hidden.
/// </para>
/// <para>
/// <b>A matching hash is the proof, and it is the only proof there is.</b> The digest is SHA-256 of a
/// 256-bit value this server minted, so a caller presenting one it cannot have been given is guessing
/// it. That is why the identity may be published on the strength of the lookup alone, and it is also
/// why the publication survives a refusal below: an ended or unknown session leaves
/// <c>app.current_user_id</c> naming the account whose handle really did match, for a request that goes
/// on to be refused. It reaches no budget — the tenant is published only on the live path — and every
/// route that runs without authenticating publishes its own identity after its own proof, so the
/// residue changes no answer today. It is stated because the next anonymous route added is where it
/// would start to.
/// </para>
/// </remarks>
public sealed class AuthenticateSessionHandler(
    ISessionTokenRepository sessionTokenRepository,
    ISessionRepository sessionRepository,
    IBudgetRepository budgetRepository,
    IUserContextWriter userContextWriter,
    TimeProvider timeProvider) : ICommandHandler<AuthenticateSessionCommand, AuthenticatedSession?>
{
    public async Task<AuthenticatedSession?> HandleAsync(
        AuthenticateSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The discovery read: an exempt table, no owner predicate, on a connection naming nobody.
        SessionToken? token = await sessionTokenRepository.FindByTokenHashAsync(
            command.TokenHash,
            cancellationToken);
        if (token is null)
        {
            return null;
        }

        // Only now. Everything below this line is policed, and everything above it had to be exempt.
        userContextWriter.ResolveUser(token.UserId);

        // Policed, by the id published one line above. A null here is nearly unreachable and is not
        // dead code: the composite foreign key makes a token naming another account's session
        // unstorable, so the policy cannot hide the row from the owner this very token named, and a
        // session that is deleted takes its token rows with it by cascade. What is left is the race —
        // an erasure, or a revocation of the establishing credential, committing between these two
        // reads. The honest answer to it is that the handle now names nothing, which is also the answer
        // that fails closed if a later path finds a second way here.
        Session? session = await sessionRepository.FindByIdAsync(token.SessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        // IsActiveAt answers both halves — unrevoked and unexpired — with the exclusive boundary the
        // domain owns, so neither is restated here. A revocation check written out beside this call
        // would be a second reading of "live" able to disagree with Session's own.
        if (!session.IsActiveAt(timeProvider.GetUtcNow().UtcDateTime))
        {
            // No budget published. An ended session reaches no tenant at all, whatever a route later
            // decides to let it do, so a budget-scoped statement under one meets an unresolved budget
            // and throws rather than being scoped to a stranger and matching nothing.
            return new AuthenticatedSession(token.UserId, session.Id, session.Kind, IsLive: false);
        }

        // Read, never repaired, and after the identity — the contract FindFirstForUserAsync states.
        // An account and its budget land in one save, so an account without one is a state nothing
        // produces; meeting it here means the invariant broke, and inventing a tenant to carry on with
        // would answer this request with somebody else's rows.
        Budget budget = await budgetRepository.FindFirstForUserAsync(token.UserId, cancellationToken)
                        ?? throw new InvalidOperationException(
                            "A session names an account owning no budget, which registration cannot "
                            + "produce.");

        // Second, always: ResolveUser clears the ambient budget, so a budget published before it is a
        // budget the rest of the request does not have.
        userContextWriter.ResolveBudget(budget.Id);

        return new AuthenticatedSession(token.UserId, session.Id, session.Kind, IsLive: true);
    }
}
