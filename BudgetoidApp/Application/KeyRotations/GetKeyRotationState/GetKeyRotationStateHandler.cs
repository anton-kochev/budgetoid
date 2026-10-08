using Application.Abstractions;
using Application.KeyRotations.BeginKeyRotation;
using Domain.Users;

namespace Application.KeyRotations.GetKeyRotationState;

/// <summary>
/// Answers what rotation the signed-in account has in flight — the staged manifest, the generation it
/// is filed at, one staged seal per factor the run sealed for, and the inventory the rest of the run is
/// driven against — or <see langword="null" /> when there is none.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS IS THE READ THAT MAKES AN INTERRUPTION RECOVERABLE, AND EVERY DECISION BELOW FOLLOWS FROM
/// THAT.</b> <c>docs/business-logic/key-rotation.md</c> promises that staging both generations means an
/// interruption is always recoverable; the promise is only true if a client that lost the content key
/// to a reload can be handed the staged seals back, because until a completion promotes them they are
/// the only copies of that generation in existence.
/// </para>
/// <para>
/// <b>Nothing here refuses by answering <see langword="null" />.</b> A <see langword="null" /> means
/// exactly one thing — this account has no run in flight — and the route above turns it into a 200
/// carrying a null member rather than a 404, for the reason <c>GetAccountKeysHandler</c> gives about
/// its own empty answer: every client in this product reads a failed read as "try again in a minute",
/// which for an account that simply never began a rotation never succeeds.
/// </para>
/// <para>
/// <b>No <c>ITransactionalExecutor</c>, and that is not an oversight.</b> Four reads and no write:
/// there is no unit of work to make atomic. Opening a transaction would also put the authentication
/// path's ordering trap back in play — a transaction configures its connection when it opens, and
/// every policed statement inside one opened before the identity was published meets <c>''::uuid</c>
/// and raises <c>22P02</c>. <c>GetAccountKeysHandler</c> makes the same refusal in the same words.
/// </para>
/// <para>
/// <b>It takes no <c>ILogger</c> and must never take one</b>, the rule every handler on this path
/// keeps: a factor id is the associated data both of that factor's envelopes were sealed with, so a log
/// line holding factor ids beside encapsulated values is a partial reconstruction of the account's key
/// custody in a sink with a different audience and a different retention policy from the table it came
/// from. No gate anywhere reads a log line from here, so there is nothing to trade against.
/// </para>
/// </remarks>
public sealed class GetKeyRotationStateHandler(
    IKeyRotationRepository keyRotations,
    IRotationInventoryReadService inventory,
    IUserContext userContext,
    IBudgetContext budgetContext)
    : IQueryHandler<GetKeyRotationStateQuery, StagedKeyRotation?>
{
    public async Task<StagedKeyRotation?> HandleAsync(
        GetKeyRotationStateQuery query,
        CancellationToken cancellationToken = default)
    {
        Guid userId = userContext.UserId;

        // ONE — IS THERE A ROW AT ALL. Nothing is refused on the way here: an account that has never
        // begun a rotation is the ordinary case this read answers, not an error it reports.
        KeyRotation? staged = await keyRotations.FindStagedRotationAsync(userId, cancellationToken);

        if (staged is null)
        {
            return null;
        }

        // TWO — A ROW IS NOT A RUN, AND THIS COMPARISON IS THE WHOLE OF WHAT TELLS THEM APART.
        // IKeyRotationRepository.PromoteAsync deletes nothing — the role holds no DELETE on either
        // rotation table, and a tidy-up that ran a moment early would destroy the only copies of a
        // generation the account has just been rewritten under — so a finished run leaves its row
        // standing carrying the identifier the client is still quoting. A read keyed on the row's
        // existence alone would tell a client that has just completed a rotation it has one to resume,
        // sending it back through a whole re-encryption under a content key it no longer holds.
        //
        // THE SAME PREDICATE CompleteKeyRotationHandler's STEP THREE MAKES, DELIBERATELY DOWN TO THE
        // NULL ARM. A live run's staged epoch is above the generation the manifest holds and a
        // completed one's is equal to it; an account holding no manifest row has no stored generation
        // for this comparison to be about, so the run is reported as the in-flight run it is. That is
        // the same arm the completion takes — it compares only when the manifest is there and raises
        // the missing one further down, past the refusals a caller can act on — and a read that
        // invented a refusal here would answer "nothing in flight" to an account whose run is exactly
        // as resumable as anybody else's.
        //
        // Tracked, because FindFactorManifestAsync is the tracked read by contract: FactorManifest
        // .Promote needs the snapshotted generation to build its concurrency predicate from, so no
        // AsNoTracking may be added there. Nothing on this path saves, so nothing is flushed and the
        // instance is read and dropped.
        FactorManifest? factorManifest =
            await keyRotations.FindFactorManifestAsync(userId, cancellationToken);

        if (factorManifest is not null && staged.StagedRotationEpoch <= factorManifest.RotationEpoch)
        {
            return null;
        }

        // THREE — THE SCOPE REFUSAL, AND IT IS OWED BECAUSE THE INVENTORY BELOW IS RECOMPUTED RATHER
        // THAN READ OFF THE STAGED ROW. Five of the six sets CountNarrativeRowsAsync walks carry the
        // BudgetIsolation query filter and are scoped to the AMBIENT budget, which takes no argument
        // and cannot be re-pointed part-way through a request — so unless the owned set is exactly that
        // budget, the counts are a denominator measured over part of an account. A resuming client
        // driven by one of those reaches 100% with rows still sealed under the old content key and then
        // asks for the completion, which is the step that destroys the last copies of that key.
        // RotationCompletenessReadService refuses for exactly this reason and ExportDataHandler for its
        // own; docs/business-logic/export.md is the authority.
        //
        // SET EQUALITY IN BOTH DIRECTIONS, never Count > 1. Owning a budget this request is not inside
        // means rows the rotation must rewrite are invisible to the count; being inside a budget the
        // account does not own means somebody else's rows are being reported as this account's work
        // left to do. A count refuses only the first.
        //
        // AFTER THE TWO CHEAP ANSWERS AND NOT BEFORE THEM, which is the difference between this
        // handler and BeginKeyRotationHandler's ordering of the same refusal. There the refusal comes
        // first because everything past it is a write; here an account with nothing staged has no
        // denominator to be wrong about, so asking it earlier would buy a 500 for a read whose honest
        // answer is "nothing in flight".
        Guid ambientBudgetId = budgetContext.BudgetId;
        HashSet<Guid> owned = [.. await inventory.ListOwnedBudgetIdsAsync(userId, cancellationToken)];

        if (!owned.SetEquals([ambientBudgetId]))
        {
            // Counts, never ids, the rule RotationScopeException states: the Development branch of
            // GlobalExceptionHandler echoes this message into the response body, so a budget id spelled
            // here is a budget id handed to the caller of a request that was refused precisely so that
            // nothing would be.
            int ambientOwned = owned.Contains(ambientBudgetId) ? 1 : 0;

            throw new RotationScopeException(
                $"The account owns {owned.Count} budgets, of which {ambientOwned} is the one this "
                + "request operates inside; the progress a resuming client is handed can only be "
                + "measured over that budget, and a denominator covering part of an account is one a "
                + "rotation can pass without having finished.");
        }

        // FOUR — THE DENOMINATOR AS IT STANDS NOW, not as the begin published it. Nothing stores those
        // counts and storing them would be wrong: a row created since the begin is sealed under the
        // generation this run is replacing, so a chunk has to visit it and the completeness gate counts
        // it. The population is the gate's population — rows CARRYING a narrative value — which is why
        // a note-less transaction is in neither.
        RotationInventory counts = await inventory.CountNarrativeRowsAsync(userId, cancellationToken);

        // FIVE — THE SEALS, DRIVEN OFF key_rotation_seals AND NEVER OFF THE ACCOUNT'S LIVE FACTORS.
        // The two sets are equal on the day a run is begun and part company the moment a factor is
        // enrolled or revoked while it is in flight, and each way of papering over the difference fails
        // a client worse than the difference does:
        //
        //   PADDING the answer out to the live set with that factor's OWN encapsulated_account_keys
        //   hands back a well-formed 158-byte value of the right version carrying the SUPERSEDED
        //   generation, and a resuming client that adopted it would believe that factor already holds
        //   the new keys when it holds the old ones.
        //
        //   PADDING it with an entry carrying no value is a seal a client cannot use and will skip,
        //   which is the orphaning the seal set exists to prevent, arriving as a gap nobody reports.
        //
        // Leaving the factor out is the only honest answer: the run genuinely has no seal for it. Such
        // a run cannot be completed — CompleteKeyRotationHandler's step five raises factor_set_moved
        // and the remedy is a fresh begin carrying the corrected set — and it is not this read's job to
        // hide that, only to say truthfully what was staged.
        //
        // The owner predicate is the port's, written rather than left to user_isolation: a read that
        // lost it answers EMPTY in production rather than wrong, and empty here reads as a run that
        // sealed for nobody.
        IReadOnlyDictionary<Guid, KeyRotationSeal> seals =
            await keyRotations.ListStagedSealsAsync(userId, cancellationToken);

        // Ordered by the factor, which buys determinism rather than a meaning — the same promise
        // GetAccountKeysHandler makes about its own answer, and for the same reason: the client tries
        // and adopts each entry by the factor it names, so no sequence is more useful than another,
        // while two reads of unchanged rows disagreeing would be a difference a client cannot explain.
        // Neither read underneath carries an ORDER BY.
        return new StagedKeyRotation(
            staged.RotationId,
            staged.StagedManifest,
            staged.StagedRotationEpoch,
            staged.StartedAtUtc,
            counts,
            BeginKeyRotationHandler.MaxChunkBytes,
            [
                .. seals.Values
                    .OrderBy(seal => seal.FactorId)
                    .Select(seal => new RotationSeal(seal.FactorId, seal.EncapsulatedAccountKeys)),
            ]);
    }
}
