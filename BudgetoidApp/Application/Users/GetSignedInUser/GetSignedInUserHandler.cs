using Application.Abstractions;

namespace Application.Users.GetSignedInUser;

/// <summary>
/// Answers the signed-in account's own email address, and the budget the request is operating inside.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes no <c>ILogger</c> and no <c>ILoggerFactory</c>, and must never take one.</b> The value
/// it holds is an email address, and a single log line of it copies a person's identifier into a sink
/// with a different retention policy and a different audience from the database it came from. No gate
/// anywhere reads a log line from this path, so there is nothing to trade against.
/// </para>
/// <para>
/// A <see langword="null" /> address is not a state the product can be left in <em>at rest</em>: the
/// account, its first credential and its default budget go in one save, and <c>users</c> →
/// <c>credentials</c> cascades on delete, so a live credential standing over a missing user row is not
/// a shape the schema holds. What produces one here is <b>read skew</b> rather than a stored state.
/// <c>AuthenticateSessionHandler</c> reads <c>session_tokens</c>, then <c>sessions</c>, then
/// <c>budgets</c>, and only then does this handler read <c>users</c> — four round trips sharing no
/// transaction with each other, deliberately so, since a transaction anywhere on that path meets an
/// unpublished identity and fails <c>22P02</c>. An erasure committing inside that window therefore
/// leaves the earlier reads valid and this one empty. It throws, the same policy
/// <c>ExportDataHandler</c> states, and the caller sees the 500 <c>GlobalExceptionHandler</c> writes
/// rather than a defensive branch or <b>a 404</b>: the account genuinely no longer exists, the request
/// is already authenticated <em>as that account</em>, and <c>AuthenticateSessionHandler</c> answers the
/// same race one step earlier the same way — its budget read throws rather than inventing a tenant.
/// Answering "no such account" would file it as an ordinary missing resource — the one shape nobody
/// investigates — while the honest reading is that the request was authenticated against a row that
/// has since been erased.
/// </para>
/// <para>
/// <b>The budget is read off <see cref="IBudgetContext" /> and is never looked up.</b> The wrong
/// implementation is a read of <c>budgets</c> by <c>user_id</c> — the shape <c>ExportDataHandler</c>
/// uses for a different question, and the one somebody reaches for when the ambient budget is not to
/// hand. It agrees with the right answer for every account holding one budget and disagrees the day
/// one holds two, at which point the browser keys a blind index under a budget the request is not
/// scoped by while the row lands in the one the ambient query filters chose. Nothing sees that: the
/// unique index over <c>(budget_id, name_key)</c> still enforces exactly what it always did, the two
/// values simply never collide, and the row can never be found again. The ambient budget is the only
/// value that is by construction the one this request writes through.
/// </para>
/// <para>
/// <b><see cref="IBudgetContext.BudgetId" /> and not
/// <see cref="IBudgetContext.ResolvedBudgetId" />.</b> This route declares no policy of its own, so
/// it sits behind the fallback one: an anonymous caller never reaches it, an ended session reaches
/// only the route that ends sessions, and a locked session is refused by
/// <c>FullSessionRequirement</c>. A null here is therefore a broken invariant rather than a state the
/// pipeline can be in, and a caller that answered <see cref="Guid.Empty" /> would publish a budget
/// every client folds into every index it computes.
/// </para>
/// </remarks>
public sealed class GetSignedInUserHandler(
    IUserContext userContext,
    IBudgetContext budgetContext,
    IUserAccountReadService readService)
    : IQueryHandler<GetSignedInUserQuery, SignedInUser>
{
    public async Task<SignedInUser> HandleAsync(
        GetSignedInUserQuery query,
        CancellationToken cancellationToken = default)
    {
        Guid userId = userContext.UserId;

        string email = await readService.FindEmailAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The resolved identity for this request answers to no user row.");

        return new SignedInUser(email, budgetContext.BudgetId);
    }
}
