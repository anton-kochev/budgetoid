using Application.Abstractions;
using Application.Passkeys.Reauthentication;
using Domain.Users;

namespace Application.KeyRotations.BeginKeyRotation;

/// <summary>
/// Opens a content-key rotation on the signed-in account: stages the next generation's manifest of
/// factor public keys and hands the client what it needs to rewrite the rest.
/// </summary>
/// <remarks>
/// <para>
/// <b>One guarantee this handler used to hold is surrendered in this slice, and the block where it
/// stood says so in full.</b> Read that comment before adding a route, a member or a test here: the
/// staged factor set is no longer compared against the account's live passkey factors, because the set
/// now lives inside a manifest nothing on this side parses.
/// </para>
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
        // anything is counted, before the staged row is built. Every refusal below is a real sentence
        // or a named exception, and each of them would answer an UNPROVEN caller with a fact about this
        // account: that it owns more than one budget, that the manifest it staged was judged at all.
        // The scope refusal is the concrete one — RotationScopeException reaches the
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
        // THE CREDENTIAL COMES BACK FROM THE GATE, and that is the strongest thing this handler could
        // have been given. It is the credential whose signature was just verified against a key looked
        // up under IUserContext.UserId, so it is by construction a passkey registered to this account —
        // exactly what KeyRotation.Begin's own refusal restates, established rather than asserted.
        //
        // The rejected alternatives, named so nobody reintroduces one. Picking a credential out of
        // IKeyRotationRepository.ListPasskeyFactorsAsync is arbitrary the day an account holds two
        // passkeys, and the arbitrariness is invisible: the wrong one is a perfectly good passkey of
        // the right account, so the row stages, the run completes, and nothing anywhere reports that
        // the choice was made by iteration order. Giving Begin a Guid userId instead would move the
        // only refusal of a fabricated KeyRotation out of the Domain and into whichever caller
        // remembered it.
        Credential passkey = await reauthentication.VerifyAsync(command.Assertion, cancellationToken);

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

        // A GUARANTEE STOOD HERE AND IS SURRENDERED FOR THIS SLICE. READ THIS BEFORE ADDING ANYTHING.
        //
        // What it held: the staged factor set must be EXACTLY the account's live passkey factors — set
        // equality in both directions, never "the factor named is one of them". The weaker reading
        // fails silently the day a second passkey becomes registrable: a begin naming one factor out of
        // two succeeds, the rotation runs to completion, the promotion overwrites wrapped_account_keys,
        // and the second passkey is left holding a copy of a content key that no longer opens
        // anything — an authenticator the person still has, still enrolled, that can no longer unlock
        // the account, with no repair that does not go through a recovery code. Under set equality the
        // same begin is refused here, at the start of the run, while the client can still re-post.
        //
        // Why it cannot hold now: it compared command.FactorId against
        // IKeyRotationRepository.ListPasskeyFactorsAsync's keys, and there is no longer a factor on the
        // command to compare. The set a run stages is the set named inside command.StagedManifest,
        // whose bytes are authenticated as a set by a key this server does not hold — so judging it
        // means parsing a client's grammar, which this slice does not do and KeyRotation deliberately
        // does not do either. The staged manifest is therefore accepted unexamined, and a client that
        // staged a generation omitting one of its own passkeys is not refused by anything.
        //
        // Nothing is exposed meanwhile: no route reaches this handler, so no request can begin a run at
        // all. That is what makes the gap affordable, and it is also what will make it easy to forget —
        // the day a route is added, this comment is the thing that has to be answered first. The
        // restoration belongs with whatever comes to read the manifest: it compares the factor ids the
        // staged manifest names against ListPasskeyFactorsAsync's keys, in both directions, and refuses
        // as a 400 keyed on the manifest member. That listing is still registered and still answers
        // passkey factors only; nothing in this handler calls it any more.
        //
        // Deleting this comment and calling the slice finished is the failure mode. A guard removed
        // with no trace is how a temporary gap becomes permanent.

        // AFTER THE SCOPE REFUSAL, so a begin that will not be staged costs the database six counts it
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
        // Every refusal KeyRotation.Begin can raise — an absent or over-wide manifest, an epoch below
        // the floor, an empty identifier, a credential that is not a passkey — therefore also happens
        // before anything is written.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        KeyRotation rotation = KeyRotation.Begin(
            passkey,
            command.RotationId,
            command.StagedManifest,
            command.StagedRotationEpoch,
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
