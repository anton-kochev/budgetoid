using Application.Abstractions;
using Application.Passkeys.Reauthentication;
using Domain.Transactions;
using Domain.Users;

namespace Application.Users.EraseAccount;

/// <summary>
/// Erases the signed-in account and everything owned beneath it.
/// </summary>
/// <remarks>
/// <para>
/// Deleting the <c>users</c> row is what removes the graph: every owned table hangs off it through
/// <c>ON DELETE CASCADE</c>, directly or through <c>budgets</c>. What this handler deletes itself is
/// only what that cascade cannot carry. The general rule, which is what a future table should be
/// measured against rather than the single call below:
/// </para>
/// <para>
/// <b>Erasure deletes explicitly only what a RESTRICT edge would otherwise block, in dependency
/// order; everything joined to the account by CASCADE alone is left to the cascade.</b>
/// </para>
/// <para>
/// Today exactly one deletion satisfies it. The owned graph carries five RESTRICT edges —
/// <c>transactions → budgets</c>, <c>transactions → accounts</c>, <c>transactions → categories</c>,
/// <c>transactions → payees</c> and <c>categories → category_groups</c> — and <c>transactions</c> is
/// the child of four of them. Those four are RESTRICT on purpose: they are the guard that stops an
/// ordinary delete taking recorded money movement with it, and they are why that table is emptied
/// first. It is also the only one emptied, because the fifth edge cannot bite once its rows are
/// gone.
/// </para>
/// <para>
/// <c>categories → category_groups</c> is left to the cascade rather than pre-emptied, and that does
/// not rest on the order the constraints happen to have been created in. Both tables cascade from
/// <c>budgets</c>, so one <c>budgets</c> delete reaches two tables joined to each other by a RESTRICT
/// edge — but PostgreSQL queues the check for that edge as an after-row trigger when the
/// <c>category_groups</c> row is deleted, which is strictly after the cascade into <c>categories</c>
/// was already queued, and the after-trigger queue is FIFO. RESTRICT being non-deferrable does not
/// make the check fire mid-statement. Either constraint ordering leaves both tables empty.
/// </para>
/// <para>
/// <c>SchemaConstraintSnapshotTests.Schema_PinsEveryForeignKeyAndItsDeleteRule</c> is the tripwire
/// that keeps the rule honest: a new RESTRICT edge into the owned graph moves a line there, which
/// forces someone to come back and measure it against the paragraph above.
/// </para>
/// <para>
/// An already-gone account is not an error. This handler states a post-condition rather than acting
/// on a row: it never reads the user first and never turns absence into a 404, because a 404 would
/// tell someone their data might still be there.
/// </para>
/// <para>
/// What it is <b>not</b> idempotent about is the caller's experience. A second request authenticates
/// as a brand-new account that user provisioning minted moments earlier, holding no passkey, so the
/// gate refuses it — a 401 that makes no claim about data at all, which is why it does not violate the
/// paragraph above.
/// </para>
/// </remarks>
public sealed class EraseAccountHandler(
    ITransactionRepository transactions,
    IUserRepository users,
    IUserContext userContext,
    IPersistenceState persistenceState,
    ITransactionalExecutor transactionalExecutor,
    PasskeyReauthentication reauthentication)
    : ICommandHandler<EraseAccountCommand>
{
    public async Task HandleAsync(
        EraseAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The gate runs to completion OUTSIDE the transactional delegate, and the position is
        // load-bearing for two reasons — neither of them the 22P02 one CompleteAssertionHandler gives
        // for its own ordering. Identity is already published here by UserProvisioningMiddleware, so
        // the connection is configured correctly whenever it opens.
        //
        // 1. The consume must commit independently of the erasure. ConsumeAsync deletes the nonce row
        //    on its own save; inside the erasure transaction, a rolled-back erasure would RESTORE the
        //    spent nonce and make the same assertion replayable, destroying the single-use property the
        //    whole design rests on.
        // 2. The delegate is replayed. ITransactionalExecutor runs under
        //    NpgsqlRetryingExecutionStrategy, so a transient failure runs the whole delegate again — a
        //    gate inside it would consume a second time, find the nonce already spent, and refuse a
        //    VALID erasure with the same 401 an attacker gets, because the database blinked.
        //
        // EraseAccountHandlerTests.HandleAsync_WhenTheUnitOfWorkIsReplayed_StillErasesTheAccount goes
        // red the moment this call moves below the ExecuteAsync line.
        await reauthentication.VerifyAsync(command.Assertion, cancellationToken);

        // One transaction over both saves. Each repository saves on its own, and an erasure that
        // committed the transactions delete and then failed would have destroyed recorded movement
        // while leaving the account that justified it.
        await transactionalExecutor.ExecuteAsync(
            async token =>
            {
                // Load-bearing on the FIRST attempt of the FIRST request, not just under retry.
                // UserProvisioningMiddleware has already resolved the request's identity through this
                // same scoped context, which leaves the Budget entity tracked. Remove the User with
                // that dependent still in the tracker and EF cascades to the copy it can see, emitting
                // its own `DELETE FROM budgets` — and the app role has SELECT and INSERT on budgets
                // and deliberately no DELETE, so the request dies with 42501 before it deletes
                // anything. Budgets are meant to leave by the database's own cascade from users, which
                // runs as the table owner and is not a statement the app role has to be granted.
                //
                // Do not delete this line as retry hygiene, and do not answer the 42501 with a grant:
                // the failure names a permission but the cause is the change tracker. It is also what
                // makes the delegate safe to replay, which is the reason CompleteAssertionHandler
                // states for its own copy of this call.
                //
                // More load-bearing since the gate above, not less: verifying the assertion
                // materialises a PasskeyPublicKey and a PasskeySignatureCounter on this same scoped
                // context, the counter possibly with an advance the rollback would not undo. This
                // discard sweeps them along with the tracked Budget.
                persistenceState.DiscardTrackedEntities();

                // The order is a property of this method, stated here, rather than one EF derives.
                // Collapse these into one save and EF topologically sorts the batch by the foreign
                // keys *between the entity types in it*: Transaction points at Budget, Budget points
                // at User, and Budget is not in the tracker at all. No edge, no guarantee — and a draw
                // that puts the users delete first cascades into budgets and is refused with 23503 by
                // the RESTRICT edge under it. Two round-trips on a once-per-account request buys an
                // ordering a reviewer can read.
                await transactions.DeleteAllForAmbientBudgetAsync(token);
                await users.DeleteAsync(userContext.UserId, token);
            },
            cancellationToken);
    }
}
