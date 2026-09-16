using Application.Abstractions;
using Application.Passkeys.Reauthentication;
using Application.Sessions.RevokeSessionsForCredential;
using Domain.Common;
using Domain.Security;
using Domain.Users;

namespace Application.Passkeys.RevokePasskey;

/// <summary>
/// Removes one passkey of the signed-in account, and everything the database hangs off it.
/// </summary>
/// <remarks>
/// <para>
/// Deleting the <c>credentials</c> row is what removes the passkey: its public key, its signature
/// counter, <b>its share of the account's keys</b> and the sessions it opened all leave by
/// <c>ON DELETE CASCADE</c>, which runs with the privileges of the referencing table's owner rather
/// than this role's. The role holds no <c>DELETE</c> on any of them and must not be granted one — see
/// <c>docs/decisions/0014-scope-the-credential-delete-in-the-application.md</c>, which is also where
/// the argument for the delete being scoped in the application rather than by a policy lives.
/// </para>
/// <para>
/// <b>The share of the account's keys is the member of that cascade this route owes a manifest for.</b>
/// A factor leaving takes its <c>wrapped_account_keys</c> row with it, and
/// <see cref="FactorManifest"/> is the sole carrier of every factor's public key — so a revocation
/// that moved no manifest would leave the account's one statement of its factor set naming an
/// authenticator that holds no copy of the keys, and the next rotation would encapsulate them to a
/// device the person has just removed. The promotion therefore rides the delete's own
/// <c>SaveChanges</c>, not the session sweep's: both commit together inside this handler's one
/// transaction, but which statements travel together is the fact a reader can check, and
/// <see cref="IPasskeyRepository.DeletePasskeyAsync"/> is where it is named.
/// </para>
/// <para>
/// <b>An absent credential is a 404 here, unlike erasure, and the difference is what the two requests
/// name.</b> Erasure states a post-condition about the account the caller is already authenticated
/// as, so absence is success. This request names a row, the lookup that resolves it is scoped to the
/// caller's own account, and a caller asking about one of its own passkeys learns only whether that
/// passkey is one of its own — which it already knew.
/// </para>
/// </remarks>
public sealed class RevokePasskeyHandler(
    IPasskeyRepository passkeys,
    IUserContext userContext,
    IPersistenceState persistenceState,
    ITransactionalExecutor transactionalExecutor,
    PasskeyReauthentication reauthentication,
    RevokeSessionsForCredentialHandler revokeSessions)
    : ICommandHandler<RevokePasskeyCommand, PasskeyRevocation>
{
    public async Task<PasskeyRevocation> HandleAsync(
        RevokePasskeyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The gate runs to completion OUTSIDE the transactional delegate, for the two reasons
        // EraseAccountHandler writes out and for no other:
        //
        // 1. The nonce has to stay spent. The gate consumes the challenge on a save of its own; run
        //    inside this transaction, a rolled-back revocation would put the row back and make the
        //    same assertion replayable, which is the single-use property the whole design rests on.
        // 2. The delegate is replayed. ITransactionalExecutor runs under a retrying execution
        //    strategy, so a transient failure runs the body again — a gate inside it would consume a
        //    second time, find the nonce already spent, and refuse a VALID revocation with the same
        //    401 an attacker gets, because the database blinked.
        //
        // Identity is published by AuthenticateSessionHandler, which the cookie scheme ran before the
        // endpoint was reached, so the connection is configured whenever it opens; the 22P02 ordering
        // CompleteAssertionHandler states for its own gate is not what is going on here.
        await reauthentication.VerifyAsync(command.Assertion, cancellationToken);

        // AFTER THE GATE AND OUTSIDE THE TRANSACTION, the placement GenerateRecoveryCodesHandler uses
        // for its own manifest and for the same two reasons rather than new ones.
        //
        // After the gate, because a refusal here is a real sentence where every gate refusal on this
        // endpoint is a byte-identical 401: judged first, a caller holding nothing but a stolen bearer
        // token would learn that this account has a manifest and what shape one takes. Past the gate the
        // caller has proved possession of an authenticator registered to this account and there is
        // nobody left to enumerate about.
        //
        // Outside the transaction, because it judges the payload and reads nothing. The delegate below
        // is replayed by the execution strategy, and a decode inside it would be the same pure work done
        // again per attempt; nothing here touches the database and nothing here can go stale.
        //
        // WHAT IS JUDGED IS THE FRAMING AND NOTHING ELSE. The blob is sealed under the account's content
        // key, which this server has never held, so a manifest naming the factors that survive this
        // revocation, one still naming the passkey it removes, and 4096 bytes of noise are the same
        // value here. Presence, framing and the epoch are the enforceable half; FactorManifestEnvelope
        // carries the argument, including why it is not one of the two decoders the registration path
        // runs beside it — three framings, and all three lead with 0x01.
        if (!FactorManifestEnvelope.TryDecode(command.Manifest, out byte[]? manifestBytes))
        {
            throw Refused(nameof(RevokePasskeyCommand.Manifest), MalformedManifest());
        }

        return await transactionalExecutor.ExecuteAsync(
            async token =>
            {
                // REPLAY HYGIENE, and that is the whole of what this line is. ITransactionalExecutor
                // runs the delegate under a retrying execution strategy, so a transient failure replays
                // the body against a database that rolled the abandoned attempt back and a change
                // tracker that did not. Starting from an empty tracker is the contract the delegate is
                // written to, not an optimisation: CompleteAssertionHandler keeps its own copy of this
                // call for exactly that reason, and spells out what survives a rollback.
                //
                // It is NOT what saves the first attempt of the first request from 42501, whatever an
                // earlier version of this comment claimed. The gate above does materialise the PROVING
                // credential's PasskeyPublicKey and PasskeySignatureCounter onto this scoped context —
                // and a Credential removed with those dependents tracked does make EF emit its own
                // DELETE FROM passkey_public_keys and DELETE FROM passkey_signature_counters, on which
                // the role deliberately holds no DELETE. But the SECOND discard, further down, sits
                // between this line and the only statement that can cascade, and clears those same
                // dependents on the way past; it states that mechanism itself. Measured rather than
                // reasoned: deleting only this line reddens nothing across the suites, and deleting
                // both reddens four tests — so the mechanism is real and the second call is what
                // currently guards it.
                //
                // Keep the line anyway, and know why the two facts do not cancel: the justification is
                // the replay contract, which holds no matter what the statements below happen to look
                // like today, while the 42501 cover is an accident of the second discard's position. A
                // reader moving or removing EITHER discard is changing which of them carries the 42501
                // — that is what to check, and no test will ask for this one back.
                persistenceState.DiscardTrackedEntities();

                // Three predicates, three different jobs, and none of them is belt-and-braces.
                // The id selects the row. The OWNER is the only thing scoping this read at all —
                // credentials is exempt from row-level security (ADR 0011), so no policy and no query
                // filter narrows it, and dropping the user id would let one account name another's
                // credential. The TYPE is what makes the federated credential unrevocable BY
                // CONSTRUCTION rather than by a branch somebody can delete: the Google credential the
                // account signs in with cannot be selected here, so this route cannot be turned into
                // one that strips an account of its provider identity.
                //
                // The entity, not the id, travels on to the delete, and that carries the scope
                // downstream — though not because a Credential can only come from here. Both of its
                // factories are public, and either would hand the delete an instance; what they hand it
                // is one carrying a freshly minted id, which names no row the database has, so that
                // delete matches nothing and raises instead of taking a stranger's passkey. This lookup
                // is therefore the only thing on this path producing a credential the table actually
                // holds — with the owner already in the predicate that found it. Adding a second such
                // producer to PasskeyRepository is what would break the chain, and review is what
                // catches that.
                Credential credential = await passkeys.FindPasskeyCredentialAsync(
                        command.CredentialId,
                        userContext.UserId,
                        token)
                    ?? throw new NotFoundException("Passkey was not found.");

                // The floor, and where it sits is half of the rule: after the gate has proved presence
                // and after the lookup has established the passkey is this account's, but before
                // anything is removed. A handler that deleted first and counted after — or that ended
                // the credential's sessions on the way past — would answer 409 while having already
                // signed the person out of the very passkey the response says they still hold.
                //
                // KNOWN GAP, accepted rather than solved, and recorded in
                // docs/business-logic/passkeys.md: this count and the delete below are not serialized
                // against each other, so two concurrent revocations of an account's last two passkeys
                // can each read two and each delete, leaving zero — an account that can never
                // re-authenticate and therefore can never even erase itself. Closing it needs a row
                // lock on `users` held across the count and the delete, for which EF Core offers no
                // first-class API and whose raw-SQL spelling is a compile error under
                // BannedSymbols.txt. It was weighed, not missed.
                //
                // THE MANIFEST PROMOTION NARROWS THAT GAP AND DOES NOT CLOSE IT. Read the two halves
                // separately, because only one of them is a property of this server.
                //
                // What it buys: the count above is still unserialized, so both requests still read two
                // and both still reach a delete — but a factor change now also moves the account's
                // generation, and both requests read that generation at the same N and promote from it.
                // Whichever commits first takes N + 1; the other either carries WHERE rotation_epoch = N
                // and matches nothing — a 409 through the repository, the whole delegate rolled back —
                // or, if it read the manifest after the winner committed, is refused one statement
                // earlier by FactorManifest.Promote, because the epoch its client computed is no longer
                // the stored generation plus one. A 409 or a 400 where the account used to be left with
                // no way to sign in and nothing said about it.
                //
                // What it does not buy: the epoch is the CLIENT'S number, so what the server refuses is
                // two requests promoting from the same generation, not two deletes. A caller that sends
                // a generation two greater than the one it read still clears both promotions and both
                // deletes, and nothing here can tell that request from an honest one. Closing the gap
                // against any caller still needs the row lock on `users` held across the count and the
                // delete, for the reason above, and this is not that lock wearing another name.
                if (await passkeys.CountPasskeysForUserAsync(userContext.UserId, token) <= 1)
                {
                    // A real sentence, where every other refusal on this endpoint is a byte-identical
                    // 401. That uniformity exists so an UNPROVEN caller learns nothing; past the gate
                    // the caller has proved possession of an authenticator registered to this account,
                    // so there is nobody left to enumerate about — the same argument
                    // CompleteRegistrationHandler makes for its own real sentences. It still names no
                    // count and no id, because neither would tell the person anything to act on.
                    // A kind of its own, and the only conflict in the product whose remedy is an act on
                    // a DIFFERENT resource: every other one asks the caller to change or re-send what
                    // they sent, and this one asks them to go and register a passkey first.
                    throw new ConflictException(
                        "This is the account's only passkey and removing it would leave no way to "
                        + "sign in. Register another passkey first, then revoke this one.",
                        ConflictKind.LastPasskey);
                }

                // Explicit, and the delete below would take these rows anyway by the cascade from
                // credentials — which is exactly why the revocation has to be here. The schema after
                // the request is byte-identical either way, so "the credential has no active session
                // afterwards" is green with this call deleted and proves nothing; the count is the
                // only place the evidence can live, which is what makes it a response member rather
                // than an internal return value.
                //
                // What the count proves is narrower than it looks: that the sweep RAN and MATCHED
                // rows, never that it STAMPED them. A RevokeForCredentialAsync that counted its
                // matches and skipped Session.Revoke leaves every test in this seam green, this one
                // included. The stamping is proved only by
                // SessionRepositoryTests.RevokeForCredentialAsync_RevokesOnlyThatCredentialsSessions
                // and ..._RunTwice_KeepsTheFirstRevocationInstant, which drive the sweep with no
                // credential being deleted at all and read revoked_at_utc back off the rows on a fresh
                // context — delete those two as duplicates of this count and a sweep that stamps
                // nothing goes unnoticed everywhere.
                //
                // Revocation_EndsEverySessionTheCredentialEstablishedAndReportsHowMany is red both
                // when this call goes and when it swaps places with the delete — a sweep running after
                // the cascade matches no row and reports zero, the same number its absence produces.
                //
                // Through the command handler and never straight to ISessionRepository: the handler is
                // where the clock is read, so one decision to end access is stamped as one instant.
                int sessionsEnded = await revokeSessions.HandleAsync(
                    new RevokeSessionsForCredentialCommand(credential.Id),
                    token);

                // A second discard, and it is not the first one restated. The sweep above loaded every
                // unrevoked Session of this credential into the tracker. Remove the Credential with
                // those dependents still tracked and EF cascades into the copies it can see, emitting
                // its own DELETE FROM sessions — on a table granted SELECT, INSERT,
                // UPDATE (revoked_at_utc) and deliberately no DELETE, so the request dies with 42501
                // having removed nothing. Those rows are meant to leave by the database's own cascade
                // from credentials, which runs with the referencing table owner's privileges rather
                // than this role's.
                //
                // Do not answer that 42501 with a grant on sessions: the SQLSTATE names a privilege,
                // the cause is the change tracker, and app-role-grants.sql argues that the absent
                // DELETE is what keeps a session accountable. EraseAccountHandler documents the
                // identical mechanism for `budgets`, and
                // Revocation_WhenTheCredentialHasLiveSessions_DoesNotFailOnAMissingSessionDeleteGrant
                // is the test written to name the 500 rather than a wrong row count.
                //
                // The credential survives as a detached object; Remove attaches it back as Deleted, so
                // the statement below still names that one row.
                persistenceState.DiscardTrackedEntities();

                // READ HERE AND NOWHERE ABOVE, AND THE POSITION IS THE WHOLE OF WHAT MAKES IT SURVIVE —
                // the rule IRecoveryCodeRepository.FindFactorManifestAsync states for the sibling path
                // and this one inherits whole. Two calls reach back and empty the tracker before this
                // line: the discard at the top of the delegate, which the execution strategy's replay
                // needs, and the one directly above, which the session sweep needs. Both are
                // ChangeTracker.Clear, so a manifest loaded in front of either is detached — Promote
                // would mutate an instance nothing will save, no UPDATE would be emitted, and the route
                // would answer 200 having moved no generation at all. No exception, no SQLSTATE,
                // nothing to notice. Loaded outside ExecuteAsync it is worse than silent: the row it
                // snapshotted was read before an abandoned attempt rolled back, so a replay would
                // compute its successor from a generation the database may never have held.
                //
                // Read fresh on every attempt, which is also what makes a replay converge: an abandoned
                // attempt's UPDATE went back with its transaction, so the row still holds N and the
                // surviving attempt promotes it to N + 1 exactly once.
                //
                // A MISS IS AN INTEGRITY VIOLATION AND IS RAISED, NEVER BRANCHED ON AND NEVER REPAIRED
                // HERE. Registration has written a manifest for every account since the table existed,
                // so there is no account this can legitimately find nothing for. Filing a first one here
                // would let a revocation establish the account's factor set under an epoch and a blob
                // nothing upstream agreed to, and skipping the promotion would leave the account's only
                // statement of its factor set naming the very passkey this request removes — the silent
                // half of the state this path exists to make unreachable. It is not a
                // ValidationException: nothing the caller sent is wrong, so there is no member to key a
                // 400 on.
                FactorManifest factorManifest =
                    await passkeys.FindFactorManifestAsync(userContext.UserId, token)
                    ?? throw new InvalidOperationException(
                        "The account holds no factor manifest, so there is no generation to promote.");

                // Loaded, mutated, saved — never FactorManifest.For(user, bytes, epoch + 1) and an
                // Update. That shape hands EF a detached row whose original values are its current ones,
                // so the statement carries WHERE rotation_epoch = <the new value>: it matches nothing
                // against the row it was computed from, and it MATCHES against a row a racing promotion
                // has already moved to N + 1 — the one statement the concurrency token exists to refuse.
                // FactorManifest's own remarks spend a paragraph on it.
                //
                // The epoch stored is the client's number, because it is bound into the manifest's
                // associated data; what the server owes is the refusal of anything that is not stored
                // plus one, and that refusal is Promote's — a 400 keyed on the member, and a different
                // answer from the 409 the repository raises for a caller whose epoch was right when it
                // was read.
                factorManifest.Promote(manifestBytes, command.RotationEpoch);

                // A lost delete race — another revocation of the same credential committing between
                // this request's lookup and its save — arrives here as the same NotFoundException the
                // lookup above throws, carrying the same message, because IPasskeyRepository promises
                // that and PasskeyRepository is where the persistence failure is translated. Naming
                // EF's exception here instead would put the EF assembly on Application.csproj, against
                // a dependency direction that runs Infrastructure → Application, to catch a type this
                // port never surfaces.
                //
                // THE PROMOTED MANIFEST IS NAMED ALTHOUGH EF WOULD FLUSH IT EITHER WAY — it is tracked
                // and Modified, so the UPDATE joins whichever save runs next — because "the factor left
                // and the manifest naming the set moved with it" must be visible in a signature rather
                // than be a fact about the change tracker. It bites harder here than on the two paths
                // that add a factor: this delegate already ran a save of its own, the session sweep's,
                // and both commit together, so a reader could conclude the promotion is atomic wherever
                // it is written. That is true of the commit — a delete refused after a promotion still
                // rolls the promotion back with it — and it is not true of the statement order, which is
                // what the token is read against and what a reader of this file can check.
                //
                // A concurrent change to this account's factors that moved the generation between the
                // load above and this save loses on that token, and the repository answers it as a
                // FactorSetMoved conflict — the kind registration and issuing already raise, because a
                // revocation losing that race is the same fact about the caller and asks the same thing
                // of them.
                await passkeys.DeletePasskeyAsync(credential, factorManifest, token);

                // The sessions THIS call ended, excluding any a concurrent sweep ended first — the
                // semantics ISessionRepository.RevokeForCredentialAsync already documents, now a
                // published response contract, so narrowing it later is breaking.
                return new PasskeyRevocation(sessionsEnded);
            },
            cancellationToken);
    }

    /// <summary>
    /// What is required of <c>manifest</c>, said whole rather than split into which part of it was
    /// wrong.
    /// </summary>
    /// <remarks>
    /// <b>A range where the two envelope sentences on a factor's own key material state a width, and
    /// this one has to say so.</b> Those name one legal size each because their plaintexts are
    /// fixed-width keys; a manifest's plaintext grows with the number of factors it names, so the only
    /// bounds that exist are the framing's floor and the column's cap. The same sentence stands on every
    /// other path that accepts a manifest, and every number in all of them is read off the type that
    /// refuses a row against it rather than written out — a message carrying its own copy goes on being
    /// confident after the real bound has moved.
    /// </remarks>
    private static string MalformedManifest() =>
        "manifest must be base64url text decoding to between "
        + $"{CiphertextEnvelope.MinimumLength} and {FactorManifest.MaximumBytes} bytes carrying AEAD "
        + $"framing version {CiphertextEnvelope.Version}.";

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it. Keyed on the member
    // the caller can correct, the shape GenerateRecoveryCodesHandler raises its own manifest refusal
    // through.
    private static Domain.Common.ValidationException Refused(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
