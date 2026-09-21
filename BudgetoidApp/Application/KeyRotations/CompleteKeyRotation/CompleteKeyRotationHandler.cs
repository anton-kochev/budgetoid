using Application.Abstractions;
using Domain.Common;
using Domain.Users;
using ValidationException = Domain.Common.ValidationException;

namespace Application.KeyRotations.CompleteKeyRotation;

/// <summary>
/// The last step of a content-key rotation: copies every staged seal into the factor it was
/// encapsulated to and promotes the account's manifest to the staged generation, in one unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>EVERY OTHER STEP OF A ROTATION IS RECOVERABLE AND THIS ONE IS NOT.</b> A begin that goes wrong is
/// replaced by another begin. A chunk that goes wrong is re-sent. This step overwrites
/// <c>wrapped_account_keys.encapsulated_account_keys</c> for every factor the account holds, which are
/// the only copies of the generation still in force — and if it runs while a single narrative row is
/// still sealed under the old content key, that row is unreadable <b>forever</b>: no exception, no
/// SQLSTATE, nothing logged, and no repair path. Read that before weakening any refusal below.
/// </para>
/// <para>
/// <b>THE ORDER OF THE REFUSALS IS THE PROPERTY, AND TWO STEPS OF IT ARE EASY TO SKIP.</b> They are
/// numbered in the body. The two are: substituting the <em>staged</em> rotation identifier for the
/// caller's before the gate is asked, and letting <see cref="RotationScopeException" /> escape rather
/// than folding it into a refusal.
/// </para>
/// <para>
/// <b>Everything happens inside the transactional delegate, and the discard at the top of it is not
/// optional.</b> The unit of work is replayed under a retrying execution strategy against a change
/// tracker the rollback did not empty, so a completion that did not discard meets its own promoted
/// manifest on the second attempt — already at <c>N + 1</c> — and <c>FactorManifest.Promote</c> refuses
/// it, answering a <b>valid</b> completion with a 400 about the caller's arithmetic because the database
/// blinked. <c>RevokePasskeyHandler</c> and <c>GenerateRecoveryCodesHandler</c> write the argument out;
/// this handler inherits it.
/// </para>
/// <para>
/// <b>There is no re-authentication gate, and that is not an omission.</b> A completion spends no nonce
/// and verifies no assertion: the begin is the act a passkey is proved for, and a run in flight is
/// already the account's own. A prompt here would also be a prompt at the one moment a person has the
/// most to lose by abandoning the request.
/// </para>
/// <para>
/// <b><c>POST /api/me/key-rotation/completion</c> reaches this handler, and it adds nothing.</b> That
/// route binds one identifier and forwards it: it judges no member, holds no gate of its own, and
/// answers 204 carrying nothing — not a body and not a header — because the promoted generation is a
/// number a client may only learn by re-reading <c>GET /api/me/account-keys</c> through the gate
/// <c>docs/business-logic/account-keys.md</c> puts over it.
/// </para>
/// </remarks>
public sealed class CompleteKeyRotationHandler(
    IKeyRotationRepository keyRotations,
    IRotationCompletenessReadService completeness,
    IUserContext userContext,
    IPersistenceState persistenceState,
    ITransactionalExecutor transactionalExecutor)
    : ICommandHandler<CompleteKeyRotationCommand>
{
    public Task HandleAsync(
        CompleteKeyRotationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Read before the delegate is entered, for the reason ResealRowsHandler reads its own there: the
        // unit of work is replayed under a retrying execution strategy, and an accessor that threw would
        // then throw once per attempt for a request that never had an identity at all.
        Guid userId = userContext.UserId;

        return transactionalExecutor.ExecuteAsync(
            async token =>
            {
                // THE DISCARD IS AT THE TOP OF THE DELEGATE AND RUNS ON EVERY ATTEMPT. Made before the
                // executor is entered it would run once, on a tracker that has nothing in it yet, and
                // would not survive the rollback it exists to clean up after. See the class remarks for
                // what a surviving promoted manifest costs a valid completion.
                persistenceState.DiscardTrackedEntities();

                // ONE — WHICH RUN IS STAGED. Inside the delegate, so the run this completion is judged
                // against and the rows it promotes are read and written in one transaction; outside it,
                // a begin racing this request could replace the staged generation between the check and
                // the promotion.
                KeyRotation? staged = await keyRotations.FindStagedRotationAsync(userId, token);

                // TWO — THE CALLER IS QUOTING THE STAGED RUN. staged being null and its identifier
                // differing are ONE refusal on purpose, ResealRowsHandler's reason: splitting them would
                // put "this account has no rotation in flight" into the body of a request that was
                // already wrong, and the client's next act is the same either way. Written as a null
                // check rather than `staged.RotationId != ...` because the latter answers a
                // NullReferenceException — a 500 — for a request that is merely stale.
                if (staged is null || staged.RotationId != command.RotationId)
                {
                    throw Refused(
                        nameof(CompleteKeyRotationCommand.RotationId),
                        "This request names a rotation that is not the one in flight. Begin a rotation, "
                        + "then complete it under the identifier that begin staged.");
                }

                // FROM HERE THE STAGED IDENTIFIER IS USED AND THE CALLER'S IS NOT. The two are equal on
                // this line, and reaching for `command.RotationId` below would still be the defect: the
                // completeness gate takes a rotation identifier and has no idea which run an account has
                // staged, so a handler that passed the caller's through can be handed an ABANDONED run
                // whose stamps happen to be a full house, be told COMPLETE, and destroy the live keys on
                // the strength of a different run's stamps — leaving every row the staged run had not yet
                // reached sealed under a key nothing holds a copy of. Taking it off `staged` is what
                // makes the guard above load-bearing rather than decorative.
                Guid rotationId = staged.RotationId;

                // Read once, here, because the epoch rule below needs it and because a second read would
                // be a second chance to reach the row through something that is not the tracker.
                // Tracked, which is what makes the promotion at step seven a statement EF will emit.
                FactorManifest? factorManifest =
                    await keyRotations.FindFactorManifestAsync(userId, token);

                // THREE — "STAGED" NO LONGER MEANS "IN FLIGHT", AND THIS IS WHAT TELLS THE TWO APART. A
                // completion leaves the staging row standing: the role holds no DELETE on either rotation
                // table, and a delete run a moment early would destroy the only copies of a generation
                // rows have already been rewritten under. So a run that finished minutes ago still
                // carries the identifier the caller is quoting and still satisfies step two. What cannot
                // survive a completion is the epoch gap — the staged generation is above the manifest's
                // while the run is live and equal to it afterwards.
                //
                // A KIND OF ITS OWN, AND THE ALTERNATIVE IS WHY. Left to fall through, a re-sent request
                // would rewrite every factor with the value it already holds — harmless — and then ask
                // FactorManifest.Promote to move to a generation the row is already at, which it refuses
                // as a 400 about the caller's arithmetic. That is the wrong answer to a client whose
                // first request succeeded and whose response was lost: it says "your number is wrong"
                // where the truth is "this is already done".
                //
                // A NULL MANIFEST FALLS THROUGH TO STEP SIX RATHER THAN BEING COMPARED AGAINST HERE. It
                // is an integrity violation and a 500 by decision, not a conflict the caller can act on,
                // so it is raised where the refusals a caller CAN act on have finished.
                if (factorManifest is not null
                    && staged.StagedRotationEpoch <= factorManifest.RotationEpoch)
                {
                    throw new ConflictException(
                        "This rotation has already been completed and the generation it staged is the "
                        + "one in force. There is nothing left to promote.",
                        ConflictKind.RotationAlreadyCompleted);
                }

                // FOUR — THE GATE, ASKED WITH THE STAGED IDENTIFIER. A false says at least one row of
                // this account still holds ciphertext under the content key the promotion is about to
                // destroy the last copies of.
                //
                // ROTATIONSCOPEEXCEPTION IS NOT CAUGHT HERE, AND A BROAD CATCH WOULD TAKE IT BY ACCIDENT
                // — it derives from InvalidOperationException. Refusing and answering false are two
                // different facts: a false says every row of the account is visible and some are
                // outstanding, while the scope refusal says the read could not see the question at all,
                // because five of the six sets it walks carry the BudgetIsolation filter and an account
                // owning a budget beside the ambient one would be answered for in part. The part the read
                // cannot reach is exactly the part the promotion would destroy, so a handler that
                // swallowed it promotes over rows nothing ever counted.
                if (!await completeness.EveryNarrativeRowIsStampedAsync(userId, rotationId, token))
                {
                    throw new ConflictException(
                        "Some of this account's rows have not been re-sealed under the new keys yet, so "
                        + "completing now would make them unreadable. Send the outstanding chunks, then "
                        + "complete this rotation again.",
                        ConflictKind.RotationIncomplete);
                }

                // FIVE — THE LIVE FACTOR SET IS EXACTLY WHAT THE RUN STAGED A SEAL FOR, IN BOTH
                // DIRECTIONS. The two directions fail differently, which is why neither can stand for the
                // other.
                //
                // A factor with no seal is the orphaning this whole slice exists to prevent, at the only
                // moment it becomes irreversible: the sealed factors adopt the new generation and the
                // unsealed one is left holding a copy of a content key that opens nothing — an
                // authenticator the person still has, still enrolled, still offered as a way back in,
                // with nothing anywhere reporting it.
                //
                // A seal naming a factor the account no longer holds reaches for a row that is not there:
                // a KeyNotFoundException at best, which is a 500 for a caller whose request was merely
                // overtaken, and a silently skipped factor at worst.
                //
                // TRACKED, because these are the instances step eight mutates and step nine flushes.
                // ListFactorsAsync is AsNoTracking and a promotion through it moves nothing at all.
                IReadOnlyDictionary<Guid, WrappedAccountKeys> factors =
                    await keyRotations.TrackFactorsAsync(userId, token);
                IReadOnlyDictionary<Guid, KeyRotationSeal> seals =
                    await keyRotations.ListStagedSealsAsync(userId, token);

                if (!factors.Keys.ToHashSet().SetEquals(seals.Keys))
                {
                    // Counts and never identifiers, the rule every refusal on this route keeps:
                    // ConflictExceptionHandler copies the message into the problem document's detail with
                    // no environment check, so what is written here is what a Production client reads.
                    //
                    // FactorSetMoved rather than a kind of its own, because the remedy is the one that
                    // member already names: read the account's keys back and run the ceremony again — a
                    // begin carrying the corrected set, which is the repair path IKeyRotationRepository
                    // .StageAsync promises replacement for.
                    // "Naming a different set" rather than a comparison of the two counts, which is
                    // BeginKeyRotationHandler's wording on the same pair of sets and for the reason it
                    // reads correctly when a factor was revoked and another registered while the run was
                    // in flight: the counts agree, the sets do not, and a sentence built on the numbers
                    // alone would say the account holds twelve and twelve were staged.
                    throw new ConflictException(
                        $"A rotation must promote one staged value for each of the {factors.Count} "
                        + $"factors this account holds and no others; {seals.Count} are staged, naming a "
                        + "different set, so completing it would leave a factor unable to open the "
                        + "account. Begin the rotation again carrying the factors it holds now.",
                        ConflictKind.FactorSetMoved);
                }

                // SIX — A MISSING MANIFEST IS AN INTEGRITY VIOLATION AND IS RAISED, NEVER BRANCHED ON AND
                // NEVER REPAIRED HERE. The same refusal RevokePasskeyHandler makes in the same words.
                // Registration has written a manifest for every account since the table existed, so there
                // is no account this can legitimately find nothing for. Filing a first one here would let
                // a completion establish the account's factor set under bytes and an epoch nothing
                // upstream agreed to, and skipping the promotion would leave the account's only statement
                // of its factor set describing a generation that no longer exists. It is deliberately not
                // a ValidationException: nothing the caller sent is wrong, so there is no member to key a
                // 400 on.
                FactorManifest manifest = factorManifest
                    ?? throw new InvalidOperationException(
                        "The account holds no factor manifest, so there is no generation to promote.");

                // SEVEN — THE MANIFEST MOVES FIRST, AND THE ORDER IS NOT ARBITRARY. Promote is the only
                // refusal left that can fire, over an epoch that is above the stored generation without
                // being exactly one above it; raised after the factors had adopted their seals it would
                // leave twelve rewritten rows behind an exception, and only the transaction would be
                // standing between that and an account whose factors and manifest describe two different
                // generations.
                //
                // ON THE TRACKED INSTANCE, never FactorManifest.For(user, bytes, epoch + 1) and an
                // Update. That shape hands EF a detached row whose original values are its current ones,
                // so the statement carries WHERE rotation_epoch = <the new value>: it matches nothing
                // against the row it was computed from, and it MATCHES against a row a racing promotion
                // has already moved — the one statement the concurrency token exists to refuse.
                //
                // The epoch stored is the one the begin staged, because it is bound into the manifest as
                // associated data; what the server owes is the refusal of anything that is not stored
                // plus one, which is Promote's — a 400 keyed on the member, and a different answer from
                // the 409 PromoteAsync raises for a caller whose epoch was right when it was read.
                manifest.Promote(staged.StagedManifest, staged.StagedRotationEpoch);

                // EIGHT — EACH FACTOR ADOPTS ITS OWN SEAL, BY KEY AND NEVER POSITIONALLY. The shorter
                // spelling walks the two sequences side by side and zips them, which reads perfectly well
                // and gives every row a well-formed value of the right width and the right version that
                // only some OTHER factor's private key can open: twelve good rows, no exception, no
                // SQLSTATE, and an account that opens with none of them. Neither read carries an ORDER
                // BY, so a zip is wrong here by construction rather than by luck.
                //
                // The indexer is safe because step five has just established that the two key sets are
                // equal. Read the other way round, that is the whole point of running the comparison
                // first: a lookup that could miss would be a KeyNotFoundException for a request that
                // should have been a 409.
                //
                // WrappedAccountKeys.Promote refuses a seal whose owner or factor disagrees with the
                // row's, which is unreachable from here today — both sets are scoped to this same user
                // and the lookup is by the row's own key — and is kept because that is the ring where a
                // cross-account promotion must be unperformable rather than merely improbable.
                List<WrappedAccountKeys> promoted = new(factors.Count);

                foreach ((Guid factorId, WrappedAccountKeys factor) in factors)
                {
                    factor.Promote(seals[factorId]);
                    promoted.Add(factor);
                }

                // NINE — ONE SAVE, COVERING THE MANIFEST AND EVERY FACTOR. Its signature is the only
                // statement in the product that these rows and this manifest move together; nothing below
                // it — no constraint, no policy — says so. And it is INSIDE the unit of work: a handler
                // that entered the executor, did nothing in the delegate and promoted afterwards
                // satisfies every count a test can take while being wrong in the one way nothing can
                // undo, because a write outside the transaction is a write nothing rolls back.
                await keyRotations.PromoteAsync(manifest, promoted, token);
            },
            cancellationToken);
    }

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it. Keyed on the member
    // the caller can correct, the shape BeginKeyRotationHandler and ResealRowsHandler raise their own
    // refusals through.
    //
    // THE MESSAGE IS RENDERED INTO THE RESPONSE BODY UNCONDITIONALLY, which is why it names no
    // identifier and no count drawn from what the server holds. ValidationExceptionHandler sets 400 and
    // copies Errors straight into a ValidationProblemDetails with no environment check anywhere in it,
    // so what is written at the call site is what a Production client reads.
    private static ValidationException Refused(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
