using Application.Abstractions;
using Application.Passkeys.Reauthentication;
using Application.Sessions.RevokeSessionsForCredential;
using Domain.Common;
using Domain.Users;

namespace Application.Passkeys.RevokePasskey;

/// <summary>
/// Removes one passkey of the signed-in account, and everything the database hangs off it.
/// </summary>
/// <remarks>
/// <para>
/// Deleting the <c>credentials</c> row is what removes the passkey: its public key, its signature
/// counter and the sessions it opened all leave by <c>ON DELETE CASCADE</c>, which runs with the
/// privileges of the referencing table's owner rather than this role's. The role holds no
/// <c>DELETE</c> on any of them and must not be granted one — see
/// <c>docs/decisions/0014-scope-the-credential-delete-in-the-application.md</c>, which is also where
/// the argument for the delete being scoped in the application rather than by a policy lives.
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

                // A lost delete race — another revocation of the same credential committing between
                // this request's lookup and its save — arrives here as the same NotFoundException the
                // lookup above throws, carrying the same message, because IPasskeyRepository promises
                // that and PasskeyRepository is where the persistence failure is translated. Naming
                // EF's exception here instead would put the EF assembly on Application.csproj, against
                // a dependency direction that runs Infrastructure → Application, to catch a type this
                // port never surfaces.
                await passkeys.DeletePasskeyAsync(credential, token);

                // The sessions THIS call ended, excluding any a concurrent sweep ended first — the
                // semantics ISessionRepository.RevokeForCredentialAsync already documents, now a
                // published response contract, so narrowing it later is breaking.
                return new PasskeyRevocation(sessionsEnded);
            },
            cancellationToken);
    }
}
