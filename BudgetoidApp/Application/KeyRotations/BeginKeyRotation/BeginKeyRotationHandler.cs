using Application.Abstractions;
using Application.Passkeys.Reauthentication;
using Domain.Users;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.KeyRotations.BeginKeyRotation;

/// <summary>
/// Opens a content-key rotation on the signed-in account: stages the next generation of its two
/// wrapped keys and hands the client what it needs to rewrite the rest.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second begin replaces the first rather than conflicting with it.</b> Begin is the repair path —
/// when a completion refuses because the account's live factor set moved, the only way forward is a
/// begin carrying the corrected set — so <c>IKeyRotationRepository.StageAsync</c> promises replacement
/// and this handler does not read the staging table before it writes. Reading it first would be the
/// "check, then insert" <see cref="KeyRotation" /> argues against at length: two statements with a
/// window between them, and the window is exactly wide enough for the second browser tab.
/// <c>key_rotations.user_id</c> is the primary key so that the rule is held by the database instead.
/// </para>
/// <para>
/// <b>Nothing here re-derives the gate's placement.</b> <c>EraseAccountHandler</c> and
/// <c>GenerateRecoveryCodesHandler</c> write out both halves of why the re-authentication runs outside
/// the transactional delegate; the two sentences below say which half applies and stop there.
/// </para>
/// </remarks>
public sealed class BeginKeyRotationHandler(
    IKeyRotationRepository keyRotations,
    IRotationInventoryReadService inventory,
    IUserContext userContext,
    IBudgetContext budgetContext,
    ITransactionalExecutor transactionalExecutor,
    PasskeyReauthentication reauthentication,
    TimeProvider timeProvider)
    : ICommandHandler<BeginKeyRotationCommand, KeyRotationBegun>
{
    /// <summary>
    /// The byte budget one chunk of a rotation may spend on its re-sealed rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A budget the client is handed, not a bound anything here enforces</b> — the route that
    /// accepts a chunk is unbuilt, and when it ships it reads this constant rather than restating the
    /// number. Published from the begin because a client cannot size its first chunk without it, and a
    /// client that guessed would discover the answer as a 413 in the middle of a run.
    /// </para>
    /// <para>
    /// <b>Half of what Kestrel accepts, and the halving is the whole of the argument.</b>
    /// <c>Api/Program.cs</c> caps a request body at 64 KB and owns that number; this project
    /// deliberately does not reference <c>Api</c>, so a budget equal to the cap would be a guaranteed
    /// 413 on every chunk of every rotation — the chunk is not the body, it travels inside JSON that
    /// also carries the rotation identifier, a row identifier per entry and base64url expansion on
    /// every envelope. Leaving the other half to that overhead is a margin rather than a measurement,
    /// and it is the right direction to be wrong in: a chunk smaller than it could be costs one extra
    /// round trip on a long rotation, and a chunk larger than the body costs a rotation that cannot
    /// make progress at all.
    /// </para>
    /// </remarks>
    public const int MaxChunkBytes = 32 * 1024;

    public async Task<KeyRotationBegun> HandleAsync(
        BeginKeyRotationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // THE GATE RUNS TO COMPLETION, AND IT RUNS FIRST — before the owned budget set is read, before
        // the factor set is judged, before anything is counted. Every refusal below is a real sentence
        // or a named exception, and each of them would answer an UNPROVEN caller with a fact about this
        // account: that it owns more than one budget, that it holds a second passkey, that the factor
        // it named is real. The scope refusal is the concrete one — RotationScopeException reaches the
        // caller as a 500 whose message the Development branch of GlobalExceptionHandler echoes into the
        // body — but the ordering is the rule for all of them, and it is the ordering
        // GenerateRecoveryCodesHandler states for its own validation. Past the gate the same sentences
        // cost nothing: the caller has proved possession of an authenticator registered to this account
        // and there is nobody left to enumerate about.
        //
        // AND IT RUNS OUTSIDE THE TRANSACTIONAL DELEGATE, for the two reasons EraseAccountHandler and
        // GenerateRecoveryCodesHandler both write out in full and which this handler inherits rather
        // than restates: the consume commits on a save of its own, so a rolled-back attempt would
        // restore the spent nonce and make the assertion replayable; and the delegate is replayed under
        // a retrying execution strategy, so a gate inside it would consume a second time and refuse a
        // VALID begin with the same 401 an attacker gets, because the database blinked.
        //
        // Identity is published by AuthenticateSessionHandler while the cookie was authenticated, long
        // before this line, so the connection is configured whenever it opens; the 22P02 ordering
        // CompleteAssertionHandler states for its own gate is not what is going on here.
        await reauthentication.VerifyAsync(command.Assertion, cancellationToken);

        Guid userId = userContext.UserId;

        // THE EXPORT'S RULE IN THE EXPORT'S SPELLING, and refusing it here is the cheaper half of the
        // refusal IRotationCompletenessReadService makes at the other end of the run. A rotation that
        // cannot be completed is better not begun: at this point the client has re-encrypted nothing,
        // while at completion it has rewritten the account and the promotion it is asking for is the
        // step that destroys the only wrapped copies of the keys the rest of it is sealed under.
        //
        // SET EQUALITY, IN BOTH DIRECTIONS, and deliberately not Count > 1. Owning a budget this
        // request is not inside means rows this rotation will never rewrite are invisible to every read
        // beneath it; being inside a budget the account does not own means somebody else's rows are
        // being measured as this account's progress. A count refuses only the first.
        // docs/business-logic/export.md is the authority and argues it in full.
        IReadOnlyList<Guid> ownedBudgetIds = await inventory.ListOwnedBudgetIdsAsync(userId, cancellationToken);
        Guid ambientBudgetId = budgetContext.BudgetId;
        HashSet<Guid> owned = [.. ownedBudgetIds];

        if (!owned.SetEquals([ambientBudgetId]))
        {
            // Counts, never ids, the rule RotationScopeException states: the Development branch of
            // GlobalExceptionHandler echoes this message into the response body, so a budget id spelled
            // here is a budget id handed to the caller of a request that was refused precisely so that
            // nothing would be.
            int ambientOwned = owned.Contains(ambientBudgetId) ? 1 : 0;

            throw new RotationScopeException(
                $"The account owns {owned.Count} budgets, of which {ambientOwned} is the one this "
                + "request operates inside; a rotation can rewrite the narrative rows of that budget "
                + "alone, and one that cannot finish is better not begun.");
        }

        // SET EQUALITY AGAIN, AND FOR A DIFFERENT REASON THAT FAILS THE SAME WAY. The staged factor set
        // must be EXACTLY the account's live passkey factors — not "the factor named is one of them".
        //
        // Today every account holds one passkey, so the two readings are indistinguishable and every
        // fixture in the suite passes either way. They come apart the day a second passkey becomes
        // registrable — Story 11.13 — and they come apart silently: under "is one of", a begin naming
        // one factor out of two succeeds, the rotation runs to completion, the promotion overwrites
        // wrapped_account_keys, and the second passkey is left holding a wrapped copy of a content key
        // that no longer opens anything. An authenticator the person still has, still enrolled, that
        // can no longer unlock the account, with no repair path that does not go through a recovery
        // code. Under set equality the same begin is refused here, at the start of the run, while the
        // client can still re-post one carrying both factors.
        //
        // The listing answers passkey factors and nothing else, so a factor identifier naming one of
        // the account's recovery-code shares is refused by this same comparison — see
        // IKeyRotationRepository.ListPasskeyFactorsAsync for why a set of codes may not begin a run at
        // all, and KeyRotation.Begin, which refuses the credential from the other end.
        IReadOnlyDictionary<Guid, Credential> passkeyFactors =
            await keyRotations.ListPasskeyFactorsAsync(userId, cancellationToken);
        HashSet<Guid> live = [.. passkeyFactors.Keys];

        if (!live.SetEquals([command.FactorId]))
        {
            // Keyed on the member the client can correct, and a real sentence rather than the
            // byte-identical 401 every gate refusal produces — the argument above about what is safe to
            // say past the gate. It names no factor identifier and no count: neither would tell the
            // person anything to act on, and the client already holds the account's factor list.
            //
            // Domain.Common.ValidationException under the alias every sibling handler uses, and the
            // choice between the two declarations is not cosmetic: ValidationExceptionHandler catches
            // that one, so Application.Abstractions.ValidationException would fall through to the
            // catch-all and answer a correctable 400 as a 500 carrying no member name at all.
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(BeginKeyRotationCommand.FactorId)] =
                [
                    "A rotation must be staged under exactly the account's live passkey factors, and "
                    + "the factor presented is not that set. Re-read the account's keys and begin "
                    + "again.",
                ],
            });
        }

        // AFTER BOTH REFUSALS, so a begin that will not be staged costs the database six counts it
        // would only have thrown away. It is also the only read here whose answer is measured over the
        // ambient budget alone, which is what the scope gate above has just established is the whole of
        // what the account owns.
        RotationInventory counts = await inventory.CountNarrativeRowsAsync(userId, cancellationToken);

        // THE CLOCK IS READ AND THE ROW IS BUILT BEFORE THE DELEGATE IS ENTERED, and both halves are
        // about the replay rather than about tidiness.
        //
        // The clock, for the reason GenerateRecoveryCodesHandler reads its own here: the delegate is
        // replayed, so an instant read inside it would stamp the row with whenever the surviving
        // attempt happened to run rather than with when the person asked.
        //
        // The row, because IKeyRotationRepository.StageAsync is promised ONE INSTANCE for all the
        // attempts of one begin. A KeyRotation minted per attempt would hand the adapter a second
        // object carrying the same primary key while the first is still tracked from an attempt the
        // database rolled back, which EF refuses by name. That contract is what lets this handler stay
        // out of IPersistenceState: the discard the sibling handlers open their delegate with is
        // answered by the port being an upsert over one object rather than by a dependency read for
        // one line.
        //
        // Every refusal KeyRotation.Begin can raise — a malformed envelope, an empty identifier, a
        // credential that is not a passkey — therefore also happens before anything is written.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        KeyRotation rotation = KeyRotation.Begin(
            passkeyFactors[command.FactorId],
            command.FactorId,
            command.RotationId,
            command.WrappedContentKey,
            command.WrappedIndexKey,
            now);

        return await transactionalExecutor.ExecuteAsync(
            async token =>
            {
                // INSIDE THE DELEGATE, and staging beside it would satisfy every count a test can
                // take — the executor ran, the row is there, the nonce was spent once — while being
                // wrong in the one way that matters: a write outside the transaction is a write
                // nothing rolls back, and on this path that is a staged next generation of the
                // account's keys surviving a failure that abandoned everything else the request was
                // doing.
                //
                // One write and one save, so there is nothing here for the transaction to hold
                // together today. It is still the right side of the line: the routes that continue and
                // complete a rotation land beside this one, and a staging write that had grown a habit
                // of sitting outside the unit of work is the habit they would inherit.
                await keyRotations.StageAsync(rotation, token);

                return new KeyRotationBegun(counts, MaxChunkBytes);
            },
            cancellationToken);
    }
}
