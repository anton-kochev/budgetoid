using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Application.Sessions.RevokeSessionsForCredential;
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
/// </remarks>
public sealed class GenerateRecoveryCodesHandler(
    IRecoveryCodeRepository recoveryCodes,
    IUserContext userContext,
    IPersistenceState persistenceState,
    ITransactionalExecutor transactionalExecutor,
    PasskeyReauthentication reauthentication,
    RevokeSessionsForCredentialHandler revokeSessions,
    TimeProvider timeProvider)
    : ICommandHandler<GenerateRecoveryCodesCommand, RecoveryCodesGeneration>
{
    /// <summary>How many codes an issued set holds.</summary>
    /// <remarks>
    /// <b>Product policy, and it lives here</b> — the placement
    /// <c>CompleteAssertionHandler.SessionLifetime</c> makes the argument for. It is not a domain
    /// invariant: a set of nine codes is not a malformed set, it is a smaller quantity of a thing
    /// somebody chose, and ADR 0002 keeps policy above the invariants because the bottom is the most
    /// expensive layer to change. It is not a database constraint either — a <c>CHECK</c> counting
    /// sibling rows cannot be written without a trigger, and pushing procedural logic down to satisfy
    /// "lowest layer" is the boundary that ADR draws.
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
                    // Through the command handler and never straight to ISessionRepository: the
                    // handler is where the clock is read, so one decision to end access is stamped as
                    // one instant.
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

                return new RecoveryCodesGeneration(sessionsEnded);
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
