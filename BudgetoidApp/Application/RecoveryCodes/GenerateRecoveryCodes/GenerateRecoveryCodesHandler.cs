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
    : ICommandHandler<GenerateRecoveryCodesCommand, Issued<RecoveryCodesGeneration>>
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

    public async Task<Issued<RecoveryCodesGeneration>> HandleAsync(
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
        //
        // Every code's factor identifier and pair of envelopes is judged in the same place and
        // inherits that ordering whole; there is nothing further to argue for them.
        IReadOnlyList<PresentedCode> presented = DecodeAndValidate(command);

        // Read before the transaction, so a replayed attempt stamps the set with one instant rather
        // than with whenever the surviving attempt happened to run.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        // AND THE HANDLE IS DRAWN HERE FOR THE SAME REASON THE CLOCK IS READ HERE: the delegate is
        // replayed under a retrying execution strategy, so a mint inside it would be a different
        // secret per attempt. Drawn once, the digest every attempt files is the same one, so the value
        // handed back is the value the surviving attempt stored.
        //
        // UNCONDITIONALLY, EVEN THOUGH MOST CALLS TO THIS ROUTE ESTABLISH NOTHING. A first issue
        // sweeps no session and re-establishes none, so this handle is never sealed, never encoded and
        // never leaves the method — thirty-two bytes drawn and collected. The alternative is a mint
        // inside the `if` far below, which puts the draw inside the replayed delegate to save that,
        // and buys the subtler retry story this comment exists to avoid.
        SessionHandle handle = SessionHandle.Mint();

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
                    //
                    // IT NOW BINDS A SECOND TABLE, AND THAT ONE FAILS THE OTHER WAY. The old set's
                    // wrapped_account_keys ROWS must not be materialised either — no read above loads
                    // them and nothing on this path takes them — but the role holds NO DELETE on that
                    // table at all. So the same mistake there does not succeed quietly, it dies with
                    // 42501 having removed nothing, on a request that has already swept the set's
                    // sessions. Do not answer that SQLSTATE with a grant: it names a privilege and the
                    // cause is the change tracker, and ADR 0018 records the absent DELETE as the thing
                    // that makes the mistake loud. The plausible way in is a "load the old envelopes so
                    // we can check them" read, and there is nothing to check — the server cannot open
                    // any of them.
                    //
                    // A SET IS TEN OF THOSE ROWS NOW, NOT ONE, and the reason survives the change
                    // undiminished: the mistake is a read, and one read materialises all ten at once.
                    // What ten does change is how a reader is tempted into it — "the replaced set had
                    // ten shares, let me load them and check I am replacing the same number" is a
                    // sentence nobody could have written while a set had one row.
                    //
                    // AND IT NOW BINDS A THIRD TABLE, WHICH FAILS THE LOUD WAY LIKE THE SECOND. Every
                    // session the sweep above loaded is presented by a session_tokens row, and those
                    // rows must never be materialised either. No read on this path loads one — the
                    // sweep selects sessions, DeleteSetAsync takes the credential, and Session carries
                    // no navigation to its handle — so the change tracker holds none, and they leave
                    // by the database's own cascade twice over: credentials to sessions, sessions to
                    // session_tokens. Were they tracked, EF would cascade into the copies it can see
                    // and emit its own DELETE FROM session_tokens on a table granted SELECT and INSERT
                    // and no DELETE of any shape, so the request would die with 42501 having removed
                    // nothing — on a request that has already swept the set's sessions.
                    //
                    // Do not answer that SQLSTATE with a grant: it names a privilege and the cause is
                    // the change tracker, and ADR 0019 records the absent DELETE as the thing that
                    // makes the mistake loud rather than silent. The way in is an Include, a
                    // navigation added to Session, or a "load the handles so we can revoke them"
                    // read — and there is nothing to revoke there: a handle is not revocation,
                    // revoked_at_utc on the session is, which the sweep has already stamped.
                    await recoveryCodes.DeleteSetAsync(previousSet, token);
                }

                // One credential for the whole set, minted from the request's own identity — no
                // account is named on the command.
                Credential set = Credential.CreateRecoveryCodes(userContext.UserId, now);

                // One row per code, each hashed by the entity. The instant is the handler's, so one
                // issuing decision reads as one instant across every row.
                IReadOnlyList<RecoveryCodeHash> hashes =
                    [.. presented.Select(code => RecoveryCodeHash.From(set, code.Verifier, now))];

                // ONE ROW PER CODE HERE TOO, AND EACH BUILT FROM ITS OWN CODE'S THREE VALUES. A set is
                // ten separate secrets and ten key-encryption keys, so ten shares of the account keys;
                // one share for the whole set would seal the account under whichever code that pair
                // belonged to and leave nine codes opening nothing.
                //
                // Projected from the one list rather than zipped from three, so a code's verifier and a
                // code's envelopes cannot come apart. Pairing one code's verifier with another code's
                // envelopes satisfies every constraint the database holds — the widths, the versions,
                // the owner, the credential and the factor's uniqueness are all still right — and it is
                // discovered in a browser months later, by somebody who redeemed a code, was handed a
                // session, and found the account still locked.
                //
                // Filed against `set` — the credential minted two lines above — and NEVER against the
                // passkey that authorized this request. That passkey is verified moments earlier and is
                // therefore the credential nearest to hand, which is exactly what makes the mistake
                // likely; it is also the one nothing would catch. The two factors derive different
                // key-encryption keys, so envelopes sealed under a recovery code and filed against a
                // passkey satisfy every check constraint and every foreign key here, and the discovery is
                // a browser failing to open that passkey's keys months later.
                //
                // Stamped from the SAME `now` as the credential and the ten hash rows, so one issuing
                // decision reads as one instant across every row it writes.
                IReadOnlyList<WrappedAccountKeys> wrappedAccountKeys =
                [
                    .. presented.Select(code => WrappedAccountKeys.For(
                        set,
                        code.FactorId,
                        code.WrappedContentKey,
                        code.WrappedIndexKey,
                        now)),
                ];

                // One save for the credential, its codes and every one of their shares of the account
                // keys: a set that counts as issued and holds no code can never be redeemed, and a code
                // holding no envelopes is a line on a card that unlocks nothing.
                await recoveryCodes.AddSetAsync(set, hashes, wrappedAccountKeys, token);

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
                //     sessions. NOTHING EXECUTABLE HOLDS THAT LINE, and a reader who goes looking will
                //     find the wrong enforcer if this comment names one. RepositoryAttributionCensusTests
                //     pins where each repository's exception narrowing is attributed — that every
                //     repository the Infrastructure assembly declares sits in exactly one bucket, and
                //     what the named file pins about its constraint filters. It says nothing about which
                //     DbSet a repository may touch, so a dbContext.Sessions.Add(...) inside AddSetAsync
                //     would leave it, and the rest of the suite, green. The boundary is kept here by
                //     hand and it is worth keeping: a repository named for recovery codes that also
                //     opens sessions puts this route's session rule where nobody reading the route would
                //     look for it. Two SaveChanges inside one transaction is the same atomicity, so the
                //     fold buys nothing in exchange.
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
                SessionHandoff? handoff = null;
                if (sessionsEnded > 0)
                {
                    // Built from the Credential and never from a kind this handler names.
                    // Session.Establish derives the kind from the credential's type, and that
                    // derivation is what makes "a sign-in reaching more of the account than its
                    // credential may" unrepresentable; a factory taking a SessionKind would let this
                    // call site name the kind and dissolve the rule. RedeemRecoveryCodeHandler opens
                    // its session the same way, for the same reason.
                    Session session = Session.Establish(set, now, now + SessionPolicy.Lifetime);

                    // The session and the handle it is presented by, in ONE save — see
                    // ISessionRepository.AddAsync. Re-establishing without a handle would be the
                    // cruellest shape this route can take: the person is told they were signed back
                    // in, in a body that says so, and the next request is a 401.
                    await sessionRepository.AddAsync(session, handle.TokenFor(session), token);

                    // BOTH ASSIGNED IN THIS ONE BRANCH, and that is why they are declared together
                    // above rather than derived from one another at the endpoint. The body member
                    // describing the session and the cookie carrying its handle are written on the
                    // same condition, so "a cookie for a session the body never mentioned" — and its
                    // mirror — are unwritable here rather than two conditions somebody has to keep in
                    // step.
                    reestablished = new ReestablishedSession(session.Kind, session.ExpiresAtUtc);
                    handoff = handle.IssuedFor(session);
                }

                // The handoff travels BESIDE the result. RecoveryCodesGeneration is what the response
                // says — a count and, sometimes, a session — and it names no handle, no code and no
                // id, for the reason that record's own remarks give.
                return new Issued<RecoveryCodesGeneration>(
                    new RecoveryCodesGeneration(sessionsEnded, reestablished),
                    handoff);
            },
            cancellationToken);
    }

    /// <summary>
    /// What one well-formed code of a set presents: its decoded verifier, the factor it stands for, and
    /// the two envelopes that factor holds the account's keys in.
    /// </summary>
    /// <remarks>
    /// One value of this type per code, never one per set. The three key-custody members belong to the
    /// code because the key-encryption key that sealed the envelopes was derived from that code, and
    /// carrying them together is what makes pairing one code's verifier with another code's envelopes
    /// unrepresentable past this point rather than merely unlikely.
    /// </remarks>
    private readonly record struct PresentedCode(
        byte[] Verifier,
        Guid FactorId,
        byte[] WrappedContentKey,
        byte[] WrappedIndexKey);

    /// <summary>
    /// Decodes what the request presents, or refuses it with the one thing that is wrong with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eight rules, eight sentences of their own, each keyed on the member a caller can correct. Each
    /// says something that caller can act on, and one shared "the recovery codes are invalid." would
    /// satisfy every refusal test while telling a person who has already proved presence nothing at all.
    /// </para>
    /// <para>
    /// <b>Three of the eight are about the set and five are about one code, and they run in that order
    /// for a reason.</b> The set's size is judged first, because the two set-wide rules below cannot be
    /// stated about a set of the wrong size at all; then every code is decoded, all ten of them, because
    /// a member is judged where it can be attributed to the code that sent it; then the two rules that
    /// only exist once every code has been decoded — the ones no single submission can be wrong about on
    /// its own.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<PresentedCode> DecodeAndValidate(GenerateRecoveryCodesCommand command)
    {
        IReadOnlyList<RecoveryCodeSubmission> presented = command.Codes;

        // Count first. Too few is a person left with fewer ways back into their account than the
        // screen told them they had; too many is a client the server no longer agrees with about what
        // a set is; zero is the argument a handler is most likely to treat as "nothing to do" and
        // answer 200 to, having replaced a live set with nothing. A missing array on the wire arrives
        // here as null despite the non-nullable declaration, and it is the same refusal — an absent
        // set is a set of the wrong size, not a fault.
        if (presented is not { Count: RequiredCodeCount })
        {
            throw Refused(
                nameof(GenerateRecoveryCodesCommand.Codes),
                $"Exactly {RequiredCodeCount} recovery codes are required.");
        }

        // EVERY CODE, NEVER ONLY THE FIRST. A loop that judged codes[0] and trusted the other nine
        // would file nine codes' worth of unjudged bytes into the account's key custody, and every
        // refusal test whose fault happens to sit on the first code would still be green.
        PresentedCode[] codes = new PresentedCode[RequiredCodeCount];
        for (int index = 0; index < presented.Count; index++)
        {
            codes[index] = Decode(presented[index], index);
        }

        // The rule the count check cannot express: a set of the required size that is one code short
        // of it, because two of its members repeat. Left to the database it becomes a primary-key
        // collision on verifier_hash — a 500 for a caller whose request was merely wrong, arriving
        // after the previous set has already been deleted inside the same transaction. Left to nothing
        // at all it is a person holding a card whose entries outnumber the codes their account will
        // accept.
        //
        // It is also the closest this layer gets to the entropy it cannot measure: a client repeating
        // a verifier inside one set has randomness that is not what it claims. Compared decoded, since
        // two spellings of one value — padded and unpadded — are the same secret. A duplicate ACROSS
        // sets is invisible here by design: the previous set's rows are never loaded.
        if (codes.Select(code => Convert.ToHexString(code.Verifier))
                .Distinct(StringComparer.Ordinal)
                .Count() != codes.Length)
        {
            throw Refused(
                nameof(GenerateRecoveryCodesCommand.Codes),
                "Every recovery code verifier in the set must be different.");
        }

        // THE SAME RULE OVER THE OTHER CLIENT-MINTED VALUE, AND IT CANNOT BE LEFT TO THE CONSTRAINT.
        // Ten codes are ten factors and therefore ten identifiers, and nothing outside this request can
        // see them as a set: each one on its own is a perfectly good uuid nobody else holds, so
        // PK_wrapped_account_keys is only reached when two of them arrive together. By then the previous
        // set's credential has already been deleted inside this same transaction and the sweep of its
        // sessions has already run, so the caller — whose request was merely wrong — meets a 409 that
        // says "that factor identifier is already registered", naming a factor they have never
        // registered and sending them looking for a request they never made.
        //
        // It is also the same evidence the verifier rule above refuses on: a client repeating an
        // identifier inside one set has randomness that is not what it claims, and the other nine are no
        // more trustworthy than the repeated one. Judged here, it is a 400 with a sentence, before
        // anything has been read or removed.
        if (codes.Select(code => code.FactorId).Distinct().Count() != codes.Length)
        {
            throw Refused(
                nameof(GenerateRecoveryCodesCommand.Codes),
                "Every factor identifier in the set must be different.");
        }

        return codes;
    }

    /// <summary>
    /// Decodes the code at <paramref name="ordinal"/>, or refuses it naming which code and which of its
    /// members was wrong.
    /// </summary>
    /// <remarks>
    /// The ordinal is in every key rather than in the sentences, so the message states the requirement
    /// whole — the same for all ten codes — while the key says which submission to correct. A refusal
    /// naming only "codes" would leave a client re-deriving ten codes when one of them was the problem.
    /// </remarks>
    private static PresentedCode Decode(RecoveryCodeSubmission? submission, int ordinal)
    {
        // Declared nullable against a non-nullable element type, because a `codes` array holding a JSON
        // null binds one — the serializer honours no declaration this layer makes. It has nothing to
        // decode, and it is refused as the whole submission rather than as one of its members, because
        // none of them arrived.
        if (submission is null)
        {
            throw Refused(
                CodeAt(ordinal),
                "Each recovery code must carry a verifier, a factor identifier and both wrapped keys.");
        }

        // One refusal covers "not base64url" and "wrong width" because the sentence states the
        // whole requirement, and because splitting them would tell a caller which half of an
        // opaque value it got wrong. The ceiling is the width itself, so no oversized member is
        // ever decoded — PasskeyEncoding.TryDecode judges the encoded length before it validates
        // or allocates.
        //
        // Reused rather than re-spelled: base64url is the one alphabet every binary member of this
        // exchange crosses JSON in, and two decoders with different bounds is a difference nobody
        // meant.
        if (!PasskeyEncoding.TryDecode(submission.Verifier, RecoveryCodeHash.VerifierLength, out byte[]? verifier)
            || verifier.Length != RecoveryCodeHash.VerifierLength)
        {
            throw Refused(
                CodeMember(ordinal, nameof(RecoveryCodeSubmission.Verifier)),
                "Each recovery code verifier must be base64url text decoding to exactly "
                + $"{RecoveryCodeHash.VerifierLength} bytes.");
        }

        // One spelling of the identifier and no more, judged by CanonicalFactorId because this path and
        // passkey registration write the same column and must not drift on what that spelling is. The
        // rule is shared; this sentence is not — it names which of the ten codes to correct. The all-zero
        // uuid is worse here than on the other path: ten codes would send it ten times and the set would
        // take itself down on its own insert.
        if (!CanonicalFactorId.TryParse(submission.FactorId, out Guid factorId))
        {
            throw Refused(
                CodeMember(ordinal, nameof(RecoveryCodeSubmission.FactorId)),
                "The factor identifier must be a uuid in the lower-case 36-character hyphenated form "
                + "with no surrounding whitespace, and not the all-zero uuid.");
        }

        // ONE SENTENCE PER MEMBER, STATING THE WHOLE REQUIREMENT, for the reason the verifier refusal
        // above gives about its own: splitting "not base64url" from "wrong width" from "unknown version"
        // would tell a caller which half of an opaque value it got wrong. The two envelopes are judged
        // separately because they are supplied separately — a handler that decoded one and passed the
        // other through would file whatever a client felt like sending into half of the account's key
        // custody.
        //
        // The width and the version are read off the entity that refuses a row against them, never
        // written out here: a message carrying its own copy of either goes on being confident after the
        // real bound has moved.
        if (!WrappedKeyEnvelope.TryDecode(submission.WrappedContentKey, out byte[]? wrappedContentKey))
        {
            throw Refused(
                CodeMember(ordinal, nameof(RecoveryCodeSubmission.WrappedContentKey)),
                MalformedEnvelope("wrapped content key"));
        }

        if (!WrappedKeyEnvelope.TryDecode(submission.WrappedIndexKey, out byte[]? wrappedIndexKey))
        {
            throw Refused(
                CodeMember(ordinal, nameof(RecoveryCodeSubmission.WrappedIndexKey)),
                MalformedEnvelope("wrapped index key"));
        }

        return new PresentedCode(verifier, factorId, wrappedContentKey, wrappedIndexKey);
    }

    /// <summary>
    /// The key one submission of the set is refused under, when the whole submission is what is wrong.
    /// </summary>
    private static string CodeAt(int ordinal) =>
        $"{nameof(GenerateRecoveryCodesCommand.Codes)}[{ordinal}]";

    /// <summary>
    /// The key one code's member is refused under: which submission of the set, and which member of it.
    /// </summary>
    private static string CodeMember(int ordinal, string member) => $"{CodeAt(ordinal)}.{member}";

    /// <summary>
    /// What is required of <paramref name="member"/>, said whole rather than split into which part of it
    /// was wrong.
    /// </summary>
    private static string MalformedEnvelope(string member) =>
        $"The {member} must be base64url text decoding to exactly "
        + $"{WrappedAccountKeys.EnvelopeLength} bytes carrying envelope version "
        + $"{WrappedAccountKeys.EnvelopeVersion}.";

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it.
    //
    // The field is a parameter rather than one constant for the whole method, because a refusal is only
    // actionable if it is filed under the member the caller can correct: a malformed envelope reported
    // against the whole set would send a client to re-derive ten codes that were never the problem. The
    // three set-wide rules land on Codes, because a set is what is wrong with those; everything a single
    // submission can be wrong about lands on that submission's own member — see CodeMember.
    private static ValidationException Refused(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
