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
/// <b>The factor-set gate is back, over the seals rather than over the manifest, and the block where it
/// stands says exactly what it does and does not hold.</b> Read that comment before changing the route
/// that reaches this handler, or adding a member or a test here: the set of factors a run stages a value for is compared against the account's
/// live factors in both directions, while the set named <em>inside</em> the staged manifest is still
/// authenticated by a key this server does not hold and is still judged by nothing.
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
    /// accepts a chunk, <c>POST /api/me/key-rotation/chunks</c>, judges nothing about a body's size and
    /// neither restates nor reads this number; the request body cap in <c>Api/Program.cs</c> is the one
    /// enforcement. Published from the begin because a client cannot size its first chunk without it, and a
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
        // IKeyRotationRepository.ListFactorsAsync is arbitrary the day an account holds two passkeys,
        // and the arbitrariness is invisible: the wrong one is a perfectly good passkey of the right
        // account, so the row stages, the run completes, and nothing anywhere reports that the choice
        // was made by iteration order. It is not even available now — that listing answers
        // wrapped_account_keys rows, which carry no credential — and it should not be made available
        // again. Giving Begin a Guid userId instead would move the only refusal of a fabricated
        // KeyRotation out of the Domain and into whichever caller remembered it.
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

        // THE FACTOR-SET GATE. WHAT IT HOLDS AND WHAT IT STILL DOES NOT ARE BOTH WRITTEN OUT, AND THE
        // SECOND HALF IS THE ONE TO READ BEFORE CHANGING THE ROUTE THAT REACHES THIS HANDLER.
        //
        // RESTORED: a run stages a value for EXACTLY the account's live factor set, in both directions
        // — and the set now covers recovery-code factors, which it never did before. The guard this
        // replaces compared one factor id on the command against a listing of passkey factors; it was
        // surrendered when the command stopped naming a factor, and it comes back here over the seals
        // the command now carries, which is where the set a run actually stages is stated in the clear.
        //
        // STILL NOT HELD: the manifest's own named set. command.StagedManifest is authenticated as a
        // set by a key this server does not hold, so a client may stage a manifest naming a different
        // set than its seals and nothing below refuses it — judging it means parsing a client's
        // grammar, which KeyRotation deliberately does not do either. That residual gap is the
        // client's to hold: it compares the factor set the server serves it against the manifest it
        // opened. FR-123 is therefore held over the seals and NOT over the manifest, and a comment
        // reading as though the requirement were now closed server-side would be the overclaim to
        // avoid.
        //
        // POST /api/me/key-rotation reaches this handler, and it does not close this gap either: its
        // decode judges the staged manifest's framing and never the set it names, so the gap is open on
        // the wire and the client's comparison is what holds it.
        //
        // AFTER THE SCOPE REFUSAL AND BEFORE THE COUNTS, so that the three refusals below cost the
        // database nothing. Read the counting read's own comment for what that is and is not worth: it
        // is these three and the scope refusal that are free, and NOT every refusal this handler can
        // make — the two factories run after the counts and cannot be moved above them.
        IReadOnlyDictionary<Guid, WrappedAccountKeys> factors =
            await keyRotations.ListFactorsAsync(userId, cancellationToken);

        // A missing array on the wire binds to null despite the non-nullable declaration, the same way
        // RecoveryCodeSetValidation meets one on a card. POST /api/me/key-rotation already forwards it
        // as an empty list; this line keeps the handler from depending on that. It is not a fault: it
        // is a request that staged no seal, and the next guard is what says so.
        IReadOnlyList<RotationSeal> submitted = command.Seals is { } presented ? presented : [];

        // NO SEALS AT ALL IS ITS OWN REFUSAL, AND IT IS THE ONE THAT DOES NOT DEPEND ON THE COMPARISON
        // BELOW BEING RIGHT. Set equality between two empty sets passes vacuously, so a listing that
        // came back EMPTY — which in production is what a query that lost its owner predicate answers,
        // since user_isolation makes such a query empty rather than wrong — would agree with a client
        // that submitted nothing, and the run would stage a generation no factor in the world can open.
        // An account holding no factor at all is not a state any path produces: registration files
        // eleven in one save, and every path that moves a factor set replaces rather than empties it.
        // So neither side of that agreement is a legitimate answer, and refusing the client's side
        // first costs one comparison and needs nothing from the listing.
        //
        // ALL THREE REFUSALS IN THIS BLOCK SAY COUNTS AND NEVER IDENTIFIERS, AND THE BOUND IS HARDER
        // THAN THE SCOPE REFUSAL'S ABOVE. That one is unmapped, so it falls to GlobalExceptionHandler
        // and only the Development branch there echoes its message. These three are a
        // Domain.Common.ValidationException, which ValidationExceptionHandler renders into a 400 by
        // copying Errors verbatim into a ValidationProblemDetails — that handler contains no
        // environment check of any kind, so every sentence below ships to the caller in PRODUCTION. Do
        // not borrow the scope refusal's Development-branch reasoning for these; it is the weaker
        // argument for the stronger rule, and a reader who checked it would find it false here. See
        // the Refused helper at the foot of this class.
        if (submitted.Count == 0)
        {
            throw Refused(
                nameof(BeginKeyRotationCommand.Seals),
                "A rotation must stage the next generation's account keys for every factor the account "
                + "holds; this request staged none.");
        }

        // THE DISTINCT COUNT BEFORE THE SET COMPARISON, because a HashSet absorbs a duplicate silently:
        // eleven seals naming ten factors collapse to ten and satisfy set equality against ten factors,
        // and the account would then be one seal short of what the client believed it sent, with
        // nothing anywhere saying which factor was repeated. It is RecoveryCodeSetValidation's rule
        // over the ten factor identifiers of a card, on the other set of the same identifiers.
        int distinctFactorCount = submitted.Select(seal => seal.FactorId).Distinct().Count();

        if (distinctFactorCount != submitted.Count)
        {
            throw Refused(
                nameof(BeginKeyRotationCommand.Seals),
                $"A rotation must stage exactly one seal per factor: {submitted.Count} seals were "
                + $"presented naming {distinctFactorCount} distinct factors.");
        }

        // SET EQUALITY, IN BOTH DIRECTIONS, never "is each named factor one of them" — and the two
        // directions fail differently, so neither can stand for the other.
        //
        // A seal set MISSING a factor is the orphaning this story exists to prevent: the run completes,
        // the promotion overwrites wrapped_account_keys.encapsulated_account_keys for the factors that
        // were sealed, and every factor that was not is left holding a copy of a content key that opens
        // nothing — an authenticator still in the drawer, a card still in the wallet, still enrolled,
        // that can no longer unlock the account. Nothing reports it, and the only repair goes through a
        // factor that was sealed.
        //
        // A seal set naming a factor the account DOES NOT hold is a value staged against a row the
        // promotion will not find. Left to the database it is the composite foreign key to
        // wrapped_account_keys raising 23503 mid-save, which is a 500 for a caller whose request was
        // merely wrong; KeyRotationSeal.For makes the same refusal one ring further in, and this is the
        // ring that can say which SET was wrong rather than which row.
        //
        // WHAT STILL PASSES THIS: a seal carrying the right factor id and the wrong bytes. The value is
        // ciphertext under a public key, so the server cannot tell the account's new keys from any
        // other 158 bytes of the right framing — the width and the version are all KeyRotationSeal.For
        // can judge, and the halves inside are a client contract nothing here can reach.
        if (!submitted.Select(seal => seal.FactorId).ToHashSet().SetEquals(factors.Keys))
        {
            // Two counts and no identifier — the only refusal here that reports a number drawn from
            // what the SERVER holds rather than from what the request carried, which is why the rule
            // stated above the first of these three is worth re-reading on this line. It reaches the
            // caller in every environment.
            throw Refused(
                nameof(BeginKeyRotationCommand.Seals),
                $"A rotation must stage one seal for each of the {factors.Count} factors the account "
                + $"holds and no others; {submitted.Count} were presented, naming a different set.");
        }

        // AFTER THE SCOPE REFUSAL AND AFTER THE FACTOR-SET GATE, SO FOUR REFUSALS COST NOTHING AND
        // EVERYTHING BELOW THIS LINE HAS ALREADY PAID. The four are the scope refusal and the gate's
        // three — no seals, a repeated factor, a set that is not the account's — and they are the ones
        // that can be decided from what the request says about a set.
        //
        // NOT "a begin that will not be staged never costs the counts", WHICH WOULD BE THE OVERCLAIM
        // HERE. The seals are built below, because KeyRotationSeal.For takes the loaded KeyRotation and
        // that needs the clock — so every refusal KeyRotation.Begin can raise (an over-wide manifest, an
        // epoch below the floor, an empty rotation id, a credential that is not a passkey) and every one
        // KeyRotationSeal.For can raise (a seal of the wrong width or the wrong framing version) happens
        // after all six counts have been taken and thrown away. That is measured rather than reasoned:
        // BeginKeyRotationHandlerTests.HandleAsync_WithAMalformedSeal_RefusesBeforeAnythingIsStaged
        // asserts the count read ran exactly once on a request that is refused.
        //
        // Reordering to close that is refused: the counts cannot move below the seal construction
        // without moving the clock and the row with them, which is the shape the block below argues
        // for, and it would buy one saved read on a malformed request.
        //
        // It is also the only read here whose answer is measured over the ambient budget alone, which
        // is what the scope gate above has just established is the whole of what the account owns.
        RotationInventory counts = await inventory.CountNarrativeRowsAsync(userId, cancellationToken);

        // THE CLOCK IS READ AND THE ROWS ARE BUILT BEFORE THE DELEGATE IS ENTERED, and both halves are
        // about the replay rather than about tidiness.
        //
        // The clock, for the reason GenerateRecoveryCodesHandler reads its own here: the delegate is
        // replayed, so an instant read inside it would stamp the row with whenever the surviving
        // attempt happened to run rather than with when the person asked.
        //
        // The rows, because IKeyRotationRepository.StageAsync is promised ONE INSTANCE SET for all the
        // attempts of one begin. A KeyRotation or a KeyRotationSeal minted per attempt would hand the
        // adapter a second object carrying the same primary key while the first is still tracked from
        // an attempt the database rolled back, which EF refuses by name. That contract is what lets
        // this handler stay out of IPersistenceState: the discard the sibling handlers open their
        // delegate with is answered by the port being an upsert over one object set rather than by a
        // dependency read for one line.
        //
        // Every refusal KeyRotation.Begin can raise — an absent or over-wide manifest, an epoch below
        // the floor, an empty identifier, a credential that is not a passkey — therefore also happens
        // before anything is written, and so does every refusal KeyRotationSeal.For can raise: a value
        // of the wrong width or the wrong framing version, and a factor of another account. That last
        // one is unreachable from here today rather than merely unlikely — the gate above has already
        // established that every submitted factor is a key of a listing scoped to this same user — and
        // it is kept because the factory is the ring where a cross-account seal must be unconstructable
        // rather than merely unstorable.
        //
        // AND THE INSTANT IS CUT TO THE MICROSECOND BEFORE THE ROW IS BUILT, because the response
        // answers it. started_at_utc is a timestamptz and keeps microseconds; a DateTime keeps ticks.
        // Left whole, the row in memory carries a seventh fractional digit the stored row does not, and
        // EF never refreshes a tracked value from what the database kept — so the begin would answer one
        // instant while GET /api/me/key-rotation answers another for the same run. Truncating HERE,
        // rather than trusting the provider to drop the digit the same way on the write, means the value
        // handed to the save is already one the column holds exactly, and what this handler answers is
        // what was stored whichever way Npgsql would have rounded. It is a whole-tick subtraction, so the
        // Kind stays Utc and the wire spelling is the resume read's.
        DateTime clock = timeProvider.GetUtcNow().UtcDateTime;
        DateTime now = clock.AddTicks(-(clock.Ticks % TimeSpan.TicksPerMicrosecond));
        KeyRotation rotation = KeyRotation.Begin(
            passkey,
            command.RotationId,
            command.StagedManifest,
            command.StagedRotationEpoch,
            now);

        // The indexer is safe because the set comparison above has just established that every
        // submitted factor id is a key of this listing. Read it the other way and it is the whole point
        // of running that comparison first: a lookup that could miss would be a KeyNotFoundException
        // for a request that should have been a 400.
        List<KeyRotationSeal> seals =
        [
            .. submitted.Select(seal => KeyRotationSeal.For(
                rotation,
                factors[seal.FactorId],
                seal.EncapsulatedAccountKeys)),
        ];

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
                // One save covering the staging row and one seal per factor, which is more than this
                // path used to have for the transaction to hold together: a row committed without its
                // seals, or seals committed without their row, is a staged generation that cannot be
                // completed. The routes that continue and complete a rotation land beside this one, and
                // a staging write that had grown a habit of sitting outside the unit of work is the
                // habit they would inherit.
                await keyRotations.StageAsync(rotation, seals, token);

                // The staged row's own start, read off the instance the save just wrote rather than off
                // the local above, so an answer and a row cannot come from two different values. On a
                // second begin the adapter copies this instance's values onto the row it found, stamp
                // included, so this is the replacement's start and never the first run's.
                return new KeyRotationBegun(counts, MaxChunkBytes, rotation.StartedAtUtc);
            },
            cancellationToken);
    }

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it. Keyed on the member
    // the caller can correct, the shape RevokePasskeyHandler and GenerateRecoveryCodesHandler raise
    // their own refusals through.
    //
    // EVERY MESSAGE HANDED TO THIS HELPER IS RENDERED INTO THE RESPONSE BODY UNCONDITIONALLY, WHICH IS
    // WHY NONE OF THEM MAY NAME AN IDENTIFIER. ValidationExceptionHandler sets 400 and copies Errors
    // straight into a ValidationProblemDetails; there is no environment check anywhere in that file, so
    // what is written at a call site is what a Production client reads. The neighbouring
    // RotationScopeException is a WEAKER case and its own comment says so in its own terms — unmapped,
    // caught by GlobalExceptionHandler, message echoed only under IsDevelopment. Borrowing that
    // sentence for a refusal raised here understates the rule by one environment.
    private static Domain.Common.ValidationException Refused(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
