using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Application.Sessions;
using Application.Sessions.RevokeSessionsForCredential;
using Domain.Sessions;
using Domain.Users;
using ValidationException = Domain.Common.ValidationException;

namespace Application.RecoveryCodes.GenerateRecoveryCodes;

/// <summary>
/// Issues the signed-in account's set of recovery codes, replacing whatever set it already held.
/// </summary>
/// <remarks>
/// <para>
/// <b>Issuing replaces; it never adds.</b> An account holds at most one set — a rule
/// <c>IX_credentials_user_id_recovery_codes</c> owns, because two sets would be two remaining-counts
/// with nothing saying which one binds — so the previous set's credential is deleted and its codes
/// leave with it by the database's own cascade. A handler that only inserted would leave the codes on
/// a card the person has already thrown away still working.
/// </para>
/// <para>
/// <b>The server never sees a code.</b> Verifiers arrive, hashes are stored, and nothing on this path
/// holds a value a code can be recovered from — see <see cref="RecoveryCodeHash"/>.
/// </para>
/// <para>
/// <b>Replacing a set that was carrying live sessions opens one session over the new set; replacing a
/// set that was carrying none opens nothing.</b> The person this route is for very often lost their
/// authenticator, redeemed a code, registered a replacement passkey, and is regenerating the card
/// while signed in on the session that redemption opened — so the sweep below takes their own session,
/// and without this rule they would be handed ten fresh codes and thrown out of the flow in the same
/// response. The condition, and why it is the set's sessions rather than the caller's, is argued at
/// the call site.
/// </para>
/// </remarks>
public sealed class GenerateRecoveryCodesHandler(
    IRecoveryCodeRepository recoveryCodes,
    IUserContext userContext,
    IPersistenceState persistenceState,
    ITransactionalExecutor transactionalExecutor,
    PasskeyReauthentication reauthentication,
    RevokeSessionsForCredentialHandler revokeSessions,
    ISessionRepository sessionRepository,
    TimeProvider timeProvider)
    : ICommandHandler<GenerateRecoveryCodesCommand, RecoveryCodesGeneration>
{
    /// <summary>How many codes an issued set holds.</summary>
    /// <remarks>
    /// <b>Product policy, and it lives in this layer</b> — the placement
    /// <see cref="SessionPolicy.Lifetime"/> makes the argument for. It is not a domain invariant: a set
    /// of nine codes is not a malformed set, it is a smaller quantity of a thing somebody chose, and
    /// ADR 0002 keeps policy above the invariants because the bottom is the most expensive layer to
    /// change. It is not a database constraint either — a <c>CHECK</c> counting sibling rows cannot be
    /// written without a trigger, and pushing procedural logic down to satisfy "lowest layer" is the
    /// boundary that ADR draws.
    /// <para>
    /// It stays on this handler where the lifetime moved out, and the asymmetry is the reason: how many
    /// codes a set holds is a number only this path has any use for, while the lifetime is one three
    /// paths have to agree on.
    /// </para>
    /// </remarks>
    public const int RequiredCodeCount = 10;

    public async Task<RecoveryCodesGeneration> HandleAsync(
        GenerateRecoveryCodesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The gate runs to completion OUTSIDE the transactional delegate, for two reasons and no
        // others:
        //
        // 1. The nonce has to stay spent. The gate consumes the challenge on a save of its own; run
        //    inside this transaction, a rolled-back attempt would put the row back and make the same
        //    assertion replayable, destroying the single-use property the whole design rests on.
        // 2. The delegate is replayed. ITransactionalExecutor runs under a retrying execution
        //    strategy, so a transient failure runs the body again — a gate inside it would consume a
        //    second time, find the nonce already spent, and refuse a VALID request with the same 401
        //    an attacker gets, because the database blinked.
        //
        // Identity is published by UserProvisioningMiddleware long before this line, so the connection
        // is configured whenever it opens; the 22P02 ordering CompleteAssertionHandler states for its
        // own gate is not what is going on here.
        //
        // What rides on the gate is larger than on any other reauthenticated route: a set of recovery
        // codes is a full-session credential, so minting one on an unproven request hands the account
        // to whoever holds a stolen bearer token — and, because issuing REPLACES, destroys the real
        // set in the same breath.
        await reauthentication.VerifyAsync(command.Assertion, cancellationToken);

        // AFTER the gate, and the ordering is a rule rather than a reading preference. Validating
        // first would answer an UNPROVEN caller with the required set size and the required verifier
        // width — the two facts a client needs to present a set at all — for a caller holding nothing
        // but a bearer token. Past the gate those same sentences cost nothing, because the caller has
        // proved possession of an authenticator registered to this account and there is nobody left to
        // enumerate about; that is also why each refusal below is a real sentence where every gate
        // refusal is a byte-identical 401, the argument CompleteRegistrationHandler makes for its own.
        byte[][] verifiers = DecodeAndValidate(command.Verifiers);

        // Read before the transaction, so a replayed attempt stamps the set with one instant rather
        // than with whenever the surviving attempt happened to run.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        return await transactionalExecutor.ExecuteAsync(
            async token =>
            {
                // REPLAY HYGIENE, and that is the whole of what this line is. The executor runs the
                // delegate under a retrying execution strategy, so a transient failure replays the
                // body against a database that rolled the abandoned attempt back and a change tracker
                // that did not. Rows the abandoned attempt queued for insert are the tracker's, not
                // the database's — a ROLLBACK never saw them — so without this the surviving attempt
                // commits two credentials and both attempts' codes against an index that permits one
                // set.
                //
                // THREE KINDS OF ROW NOW, not two: the credential, its hashes, and the session an
                // abandoned attempt re-established at the end of the delegate. That third one is the
                // reason a replayed replacement leaves ONE live session rather than one per attempt —
                // it is in the tracker and not in the database, and this is what removes it.
                persistenceState.DiscardTrackedEntities();

                // Scoped by owner AND type. credentials is exempt from row-level security (ADR 0011),
                // so nothing beneath this line narrows the read; the predicate is the only thing
                // standing between this request and another account's set — and, since the entity
                // travels on to the delete, the only thing scoping that delete too.
                Credential? previousSet =
                    await recoveryCodes.FindRecoveryCodeCredentialAsync(userContext.UserId, token);

                int sessionsEnded = 0;
                if (previousSet is not null)
                {
                    // EXPLICIT, and the delete below would take these rows anyway by the cascade from
                    // credentials — which is exactly why the revocation has to be here. The schema
                    // after the request is byte-identical either way, so "the replaced set has no live
                    // session afterwards" is green with this call deleted and proves nothing. The
                    // count is the only place the evidence can live, which is what makes it a response
                    // member rather than an internal return value.
                    //
                    // Two mutations produce the same wrong number: deleting this call reports 0, and
                    // swapping it with the delete reports 0 as well, because the cascade has already
                    // taken the session rows and the sweep matches nothing.
                    //
                    // Through the command handler and never straight to ISessionRepository — which is
                    // in scope below, so this is a live choice rather than a limitation: the handler
                    // is where the clock is read for a sweep, so one decision to end access is stamped
                    // as one instant. The establishment further down takes the opposite route for the
                    // opposite reason — its instant is this method's `now`, shared with the credential
                    // and the hash rows, so routing it through a collaborator that reads its own clock
                    // would spread one issuing decision.
                    sessionsEnded = await revokeSessions.HandleAsync(
                        new RevokeSessionsForCredentialCommand(previousSet.Id),
                        token);

                    // A second discard, and it is not the first one restated. The sweep above loaded
                    // every unrevoked Session of this credential into the tracker. Remove the
                    // Credential with those dependents still tracked and EF cascades into the copies
                    // it can see, emitting its own DELETE FROM sessions — on a table granted SELECT,
                    // INSERT, UPDATE (revoked_at_utc) and deliberately no DELETE, so the request dies
                    // with 42501 having removed nothing. Those rows are meant to leave by the
                    // database's own cascade from credentials, which runs with the referencing table
                    // owner's privileges rather than this role's.
                    //
                    // Do not answer that 42501 with a grant on sessions: the SQLSTATE names a
                    // privilege, the cause is the change tracker, and app-role-grants.sql argues that
                    // the absent DELETE is what keeps a session accountable. RevokePasskeyHandler and
                    // EraseAccountHandler document the identical mechanism for their own tables.
                    //
                    // The credential survives as a detached object; Remove attaches it back as
                    // Deleted, so the statement below still names that one row.
                    persistenceState.DiscardTrackedEntities();

                    // The loaded entity, never an id (ADR 0014). Its hash rows leave by the database's
                    // own cascade.
                    //
                    // THE RULE NOTHING BENEATH THE APPLICATION CAN CATCH: the old set's
                    // recovery_code_hashes rows are never materialised on this path. No read above
                    // loads them and DeleteSetAsync takes the set's credential and nothing else. Were
                    // they tracked, EF would emit its own DELETE FROM recovery_code_hashes — and
                    // unlike sessions, the role IS granted DELETE there, so it would silently succeed
                    // and the rows would leave by the application instead of by the cascade, with no
                    // SQLSTATE to say so. A future reader adding a "load the codes so we can count
                    // them" read here is the way that breaks.
                    await recoveryCodes.DeleteSetAsync(previousSet, token);
                }

                // One credential for the whole set, minted from the request's own identity — no
                // account is named on the command.
                Credential set = Credential.CreateRecoveryCodes(userContext.UserId, now);

                // One row per verifier, each hashed by the entity. The instant is the handler's, so
                // one issuing decision reads as one instant across every row.
                IReadOnlyList<RecoveryCodeHash> hashes =
                    [.. verifiers.Select(verifier => RecoveryCodeHash.From(set, verifier, now))];

                // One save for the credential and its codes: a set that counts as issued and holds no
                // code can never be redeemed.
                await recoveryCodes.AddSetAsync(set, hashes, token);

                // THE REPLACED SET'S SESSIONS WERE THE PERSON'S WAY IN, AND THE SWEEP ABOVE TOOK THEM.
                // Somebody who lost their authenticator, redeemed a code, registered a replacement
                // passkey and is now regenerating the card is signed in ON A SESSION THIS VERY REQUEST
                // just revoked. Without the line below they are handed ten fresh codes and thrown out
                // of the flow in the same response, at the worst possible moment.
                //
                // THE CONDITION IS sessionsEnded > 0, AND IT IS ABOUT THE SET, NOT THE CALLER. Nothing
                // on this request presents a session — the proof is a WebAuthn assertion — so the
                // server cannot know whose session it swept, and asks instead whether the set it
                // replaced was carrying any at all. It is deliberately NOT "always establish": a first
                // issue is the most frequent call to this route, and a phantom session there is a
                // sign-in somebody never made, at onboarding, indistinguishable from a compromise, and
                // revoking it does not undo having been told it.
                //
                // THE READING IS GENEROUS IN EXACTLY ONE DIRECTION, AND DELIBERATELY SO. No false
                // negatives: a live session over the replaced set is always swept, so anybody signed
                // out here is signed back in. Two false positives: a live session on ANOTHER device,
                // and a session unrevoked but past its expiry — RevokeForCredentialAsync narrows on
                // revoked_at_utc is null and says nothing about expiry. Each costs one inert row that
                // nothing has been handed to anybody, which is the cheap side of the trade.
                //
                // DO NOT "TIGHTEN" THIS TO Session.IsActiveAt(now). The number is
                // RevokeSessionsForCredentialHandler's, RevokePasskeyHandler reports the same number
                // and means something different by it, and narrowing it would change what BOTH paths
                // report — a change to a sweep's semantics dressed up as a change to this rule.
                //
                // AFTER AddSetAsync, and every other placement fails concretely:
                //   - Before it: SessionRepository.AddAsync saves on its own, so the row would name a
                //     credential that does not exist yet — 23503 on every request.
                //   - Between the sweep and the second discard, which reads tidier because it sits
                //     beside the revocation: the discard drops the queued session, the save never sees
                //     it, and this handler reports a kind and an expiry for a row that is not there.
                //     No SQLSTATE, no exception.
                //   - Inside the `if`, before DeleteSetAsync: the session lands over the OLD
                //     credential and leaves with its cascade. Silent again.
                //   - Outside ExecuteAsync: atomicity gone — the set is committed, the sweep ran, the
                //     insert fails on its own, and there is nothing left to roll back.
                //   - Folded into AddSetAsync: that hands IRecoveryCodeRepository the right to write
                //     sessions, which is the attribution RepositoryAttributionCensusTests pins. Two
                //     SaveChanges inside one transaction is the same atomicity, so it buys nothing.
                //
                // Stamped from the SAME `now` as the revocation, the credential and the ten hash rows:
                // one clock, one instant, so a replayed attempt does not spread one issuing decision.
                //
                // THE 22P02 ORDERING REDEMPTION DOCUMENTS DOES NOT APPLY HERE, and a reader will look
                // for it by analogy. RedeemRecoveryCodeHandler must publish the identity before its
                // transaction opens, because the connection is configured on open and sessions is
                // policed by user_isolation. On this route UserProvisioningMiddleware published the
                // identity long before the handler was entered, so app.current_user_id is already on
                // the connection whenever it opens.
                //
                // UNDER REPLAY THE RULE IS CONVERGENT. A second attempt sees its own committed set as
                // the previous one, sweeps the session it opened itself, deletes, re-inserts and opens
                // another — the correct end state, one set and one live session. "Never establish on a
                // retry" would leave the person with nothing.
                ReestablishedSession? reestablished = null;
                if (sessionsEnded > 0)
                {
                    // Built from the Credential and never from a kind this handler names.
                    // Session.Establish derives the kind from the credential's type, and that
                    // derivation is what makes "a sign-in reaching more of the account than its
                    // credential may" unrepresentable; a factory taking a SessionKind would let this
                    // call site name the kind and dissolve the rule. RedeemRecoveryCodeHandler opens
                    // its session the same way, for the same reason.
                    Session session = Session.Establish(set, now, now + SessionPolicy.Lifetime);
                    await sessionRepository.AddAsync(session, token);

                    reestablished = new ReestablishedSession(session.Kind, session.ExpiresAtUtc);
                }

                return new RecoveryCodesGeneration(sessionsEnded, reestablished);
            },
            cancellationToken);
    }

    /// <summary>
    /// Decodes the presented verifiers, or refuses the set with the one thing that is wrong with it.
    /// </summary>
    /// <remarks>
    /// Three rules, three sentences of their own. Each says something a caller can act on, and one
    /// shared "the recovery codes are invalid." would satisfy every refusal test while telling a
    /// person who has already proved presence nothing at all.
    /// </remarks>
    private static byte[][] DecodeAndValidate(IReadOnlyList<string> presented)
    {
        // Count first. Too few is a person left with fewer ways back into their account than the
        // screen told them they had; too many is a client the server no longer agrees with about what
        // a set is; zero is the argument a handler is most likely to treat as "nothing to do" and
        // answer 200 to, having replaced a live set with nothing. A missing array on the wire arrives
        // here as null despite the non-nullable declaration, and it is the same refusal — an absent
        // set is a set of the wrong size, not a fault.
        if (presented is not { Count: RequiredCodeCount })
        {
            throw Refused($"Exactly {RequiredCodeCount} recovery code verifiers are required.");
        }

        byte[][] verifiers = new byte[RequiredCodeCount][];
        for (int index = 0; index < presented.Count; index++)
        {
            // One refusal covers "not base64url" and "wrong width" because the sentence states the
            // whole requirement, and because splitting them would tell a caller which half of an
            // opaque value it got wrong. The ceiling is the width itself, so no oversized member is
            // ever decoded — PasskeyEncoding.TryDecode judges the encoded length before it validates
            // or allocates.
            //
            // Reused rather than re-spelled: base64url is the one alphabet every binary member of this
            // exchange crosses JSON in, and two decoders with different bounds is a difference nobody
            // meant.
            if (!PasskeyEncoding.TryDecode(presented[index], RecoveryCodeHash.VerifierLength, out byte[]? verifier)
                || verifier.Length != RecoveryCodeHash.VerifierLength)
            {
                throw Refused(
                    "Each recovery code verifier must be base64url text decoding to exactly "
                    + $"{RecoveryCodeHash.VerifierLength} bytes.");
            }

            verifiers[index] = verifier;
        }

        // The rule the count check cannot express: a set of the required size that is one code short
        // of it, because two of its members repeat. Left
        // to the database it becomes a primary-key collision on verifier_hash — a 500 for a caller
        // whose request was merely wrong, arriving after the previous set has already been deleted
        // inside the same transaction. Left to nothing at all it is a person holding a card whose
        // entries outnumber the codes their account will accept.
        //
        // It is also the closest this layer gets to the entropy it cannot measure: a client repeating
        // a verifier inside one set has randomness that is not what it claims. Compared decoded, since
        // two spellings of one value — padded and unpadded — are the same secret. A duplicate ACROSS
        // sets is invisible here by design: the previous set's rows are never loaded.
        if (verifiers.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count() != verifiers.Length)
        {
            throw Refused("Every recovery code verifier in the set must be different.");
        }

        return verifiers;
    }

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it. Keyed on the
    // command member the caller can correct.
    private static ValidationException Refused(string message) =>
        new(new Dictionary<string, string[]>
        {
            [nameof(GenerateRecoveryCodesCommand.Verifiers)] = [message],
        });
}
