using Application.Abstractions;
using Application.Passkeys;
using Application.Users.EnsureUser;
using Domain.Sessions;
using Domain.Users;

namespace Application.RecoveryCodes.RedeemRecoveryCode;

/// <summary>
/// Signs in with one recovery code and spends it.
/// </summary>
/// <remarks>
/// <para>
/// The order of the steps below is the security property, not an implementation detail, and each one
/// carries the reason it sits where it does. It is <c>CompleteAssertionHandler</c>'s sequence step for
/// step, and it inherits that handler's reasoning wholesale — read it beside this one.
/// </para>
/// <para>
/// <b>This handler never reads <see cref="IUserContext"/> for the account.</b> The route is anonymous,
/// so <c>UserProvisioningMiddleware</c> returns on the route's marker and publishes nobody whatever
/// token accompanied the request — but the account is still taken from the matched code rather than
/// from the request, which is what keeps the property from depending on where the middleware's
/// anonymous arm sits. A handler that reached for the request's identity anyway would find nothing on
/// a genuine recovery sign-in and <em>something</em> on a request from a browser whose interceptor
/// attaches a bearer to everything, and the something is the wrong account.
/// </para>
/// <para>
/// <b>Every refusal below is the same refusal.</b> A caller able to tell one from another on this route
/// is a caller learning what is stored — see <see cref="RecoveryCodeRedemptionException"/>, which is
/// the only exception this handler raises for a rejected code, and which is deliberately not the
/// passkey one.
/// </para>
/// </remarks>
public sealed class RedeemRecoveryCodeHandler(
    IRecoveryCodeRepository recoveryCodes,
    IRecoveryCodeReadService readService,
    ISessionRepository sessionRepository,
    IUserContextWriter userContextWriter,
    ITransactionalExecutor transactionalExecutor,
    IPersistenceState persistenceState,
    TimeProvider timeProvider) : ICommandHandler<RedeemRecoveryCodeCommand, RedeemedRecoveryCode>
{
    /// <summary>
    /// How long a recovery-code sign-in lasts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Product policy, and it lives here for the reason <c>CompleteAssertionHandler.SessionLifetime</c>
    /// argues at length: <see cref="Session.Establish"/> takes an expiry instead of computing one,
    /// because how long a session lasts is policy and the domain holds invariants, and ADR 0002 keeps
    /// policy above the invariants because the bottom is the most expensive layer to change.
    /// </para>
    /// <para>
    /// <b>The same interval a passkey sign-in gets, and equality is the rule rather than a
    /// coincidence.</b> Both credentials open a <see cref="SessionKind.Full"/> session — a set of
    /// recovery codes is the secret the account's keys are wrapped under, so it reaches exactly as much
    /// as an authenticator does — and a session that expired sooner here would quietly tell somebody
    /// who has just lost their device that the way back in they were issued is worth less than the one
    /// they lost. Restated rather than shared because each handler owns the policy for the sign-in it
    /// performs; the two numbers differing is a defect, not a decision.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    public async Task<RedeemedRecoveryCode> HandleAsync(
        RedeemRecoveryCodeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. Bounded, and the ceiling is refused before the text is validated or decoded — the shape
        //    PasskeyPayloadLimits exists for, on the application's other anonymous surface. Unlike
        //    every number there, this bound is EXACT rather than padded: a verifier is a fixed
        //    RecoveryCodeHash.VerifierLength bytes, so there is no conforming client whose value is
        //    larger and nothing to leave slack for. PasskeyEncoding.TryDecode judges the encoded
        //    length before it validates the alphabet or allocates, which is what keeps an oversized
        //    body from costing what the ceiling exists to refuse.
        //
        //    The length is re-checked after the decode as well, and the second check is not the first
        //    restated: the ceiling bounds the ENCODED text, which four characters per three bytes
        //    admits a value of 30, 31 or 32 bytes, and a short verifier is a shorter secret than the
        //    design claims. GenerateRecoveryCodesHandler pairs the same two checks for the same reason.
        //
        //    The ceiling is RecoveryCodeHash.VerifierLength itself and not a constant of this layer's
        //    own. A second name for one width is a way for the two to disagree, and if they ever did,
        //    every code this server issued would stop redeeming with nothing to say why.
        //
        //    Oversized is not a distinct answer, and neither is absent, mis-encoded or short. They all
        //    land on the refusal below, for the reason RecoveryCodeRedemptionException carries.
        if (!PasskeyEncoding.TryDecode(command.Verifier, RecoveryCodeHash.VerifierLength, out byte[]? verifier)
            || verifier.Length != RecoveryCodeHash.VerifierLength)
        {
            throw new RecoveryCodeRedemptionException(
                "The presented verifier was not base64url text of the required width.");
        }

        // The hash the row is stored under, computed by the domain's own HashOf so the two spellings
        // of "the hash of a verifier" cannot drift. If they ever did, every code in the system would
        // simply stop matching, silently.
        ReadOnlyMemory<byte> verifierHash = RecoveryCodeHash.HashOf(verifier);

        // 2. The discovery read — a statement that runs with no identity on the connection and names no
        //    owner, the shape the passkey discovery lookup and the challenge consume also have. The
        //    route is anonymous and user provisioning returns on that marker before it resolves anyone,
        //    so app.current_user_id is still '' whatever token accompanied the request. This may
        //    therefore touch no policed table, and making this read safe is what recovery_code_hashes is
        //    exempt from row-level security for (ADR 0016). The verifier is all the caller supplied; the
        //    answer is what will establish who is asking.
        RecoveryCodeHash? presented = await recoveryCodes.FindByVerifierHashAsync(verifierHash, cancellationToken);
        if (presented is null)
        {
            throw new RecoveryCodeRedemptionException("No unredeemed code hashes to the presented verifier.");
        }

        // 3. Only now is the account published. Publishing before a row matched would mean trusting an
        //    account an unauthenticated caller named, and every policed statement after it would run as
        //    whoever they named. Nothing about the request said who this is — the row did.
        userContextWriter.ResolveUser(presented.UserId);

        // Read before the transaction opens so a retried attempt is stamped with one instant rather
        // than drifting with each replay.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        // 4. Only then is a transaction opened, and this call site is load-bearing in its position: the
        //    transaction opens a connection, and opening a connection is when SessionContextInterceptor
        //    runs its set_config. Open it before ResolveUser above and app.current_user_id reaches the
        //    database as '', so every policed statement inside fails with 22P02 — and sessions is
        //    policed by user_isolation, so the insert below is exactly such a statement. ADR 0011
        //    states that precondition in the abstract; CompleteAssertionHandler was the first code path
        //    that could violate it and this is the second.
        return await transactionalExecutor.ExecuteAsync(
            async token =>
            {
                // REPLAY HYGIENE, and that is the whole of what this line is. The API's
                // EnrichNpgsqlDbContext installs NpgsqlRetryingExecutionStrategy, so a transient
                // failure anywhere below replays the whole delegate — against a database that rolled
                // the abandoned attempt back, and a change tracker that did not. The code the
                // abandoned attempt marked Deleted and the session it queued are both the tracker's,
                // not the database's, and discarding them is cheaper than reasoning about which
                // survived.
                //
                // Nothing read before the transaction opened is needed as a tracked entity: the code
                // above is read only for the account id it carries, and a detached instance still
                // carries it.
                //
                // THERE IS NO SECOND DISCARD HERE, unlike in GenerateRecoveryCodesHandler, and the
                // absence is a fact worth stating rather than an omission. That handler needs one
                // because its revocation sweep loads the set's Sessions into the tracker, and removing
                // the Credential with those dependents tracked makes EF cascade into the copies it can
                // see and emit its own DELETE FROM sessions — on a table holding no DELETE grant. The
                // row spent below is a recovery_code_hashes row, and nothing in the schema references
                // one: it is a leaf, so there is no cascade for EF to imitate and nothing to discard.
                persistenceState.DiscardTrackedEntities();

                // Read again, inside the transaction and after the discard, because the entity spent
                // below has to be one this unit of work is tracking — the third of ADR 0014's three
                // legs, which asks that the read producing the entity and the write removing it share
                // one transaction. It is also where a code redeemed between the discovery read and
                // this line is noticed, and it is noticed as the same refusal as every other.
                //
                // A DIFFERENT MEMBER FROM THE DISCOVERY READ, AND THE OWNER IS WHY. The read above may
                // omit an owner because it is the statement that establishes one; this one runs after
                // the account has been published, so recovery_code_hashes being exempt from row-level
                // security means it must carry its own predicate (ADR 0011) — an exempt table scopes
                // nothing, and the entity this produces goes straight to a DELETE that nothing beneath
                // the application bounds.
                //
                // The owner is presented.UserId — the account the discovery read resolved and step 3
                // published — so the row spent and the account signed in are one account's by
                // construction. The immutability of credentials.user_id and a 256-bit primary key are
                // what make the two agree today; neither of them is in this handler, and the failure if
                // they ever stopped agreeing is a session for one account over a code deleted from
                // another.
                //
                // Finding nothing here has two causes and one answer — the code was spent between the
                // two reads, or it is not this account's — and the caller cannot tell them apart, any
                // more than they can tell either from a verifier no row ever answered to. "That code is
                // real, but not yours" is a fact about what is stored and about somebody else's account
                // at once.
                RecoveryCodeHash code =
                    await recoveryCodes.FindOwnedByVerifierHashAsync(presented.UserId, verifierHash, token)
                    ?? throw new RecoveryCodeRedemptionException(
                        "No unredeemed code of the resolved account hashes to the presented verifier "
                        + "inside the transaction.");

                // THE CODE IS SPENT BEFORE THE SESSION IS ESTABLISHED, AND THE ASYMMETRY IS THE
                // REASON. That ordering is FR-054. A consumed code with no session is a retryable
                // inconvenience — the person uses the next code on the card. A session opened over a
                // code that was not consumed is a replay window, because the same value opens a second
                // one, and a recovery code is a full-session credential: that is an unbounded number
                // of sign-ins from one intercepted verifier. Swap these two and every other assertion
                // about this route still passes.
                await recoveryCodes.ConsumeAsync(code, token);

                // The credential itself, read rather than reconstructed, and scoped by owner AND type.
                // Session.Establish derives the session's kind from a Credential, and that derivation
                // is what makes "a federated sign-in reaching budget content" unrepresentable; a
                // factory taking a CredentialType instead would let a caller name the kind and
                // dissolve the rule. credentials is exempt from row-level security, so the predicate
                // inside FindRecoveryCodeCredentialAsync is the only thing narrowing this read — and
                // the owner it names is the one the matched code established, never the request's.
                //
                // The id is not restated: the composite foreign key on recovery_code_hashes compares
                // (credential_id, user_id, credential_type) against credentials(id, user_id, type), and
                // IX_credentials_user_id_recovery_codes permits one set per account, so the account's
                // recovery-code credential IS this code's credential.
                //
                // NOTE WHAT THIS HANDLER HOLDS AND MUST NEVER CALL: IRecoveryCodeRepository also
                // offers DeleteSetAsync. Redeeming the last code of a set leaves an empty set, which
                // looks like a row with no purpose, and removing it here would cascade away the
                // session established two lines below — sessions references credentials(id, user_id,
                // type) ON DELETE CASCADE — signing the person straight back out, and it would be an
                // ANONYMOUS request deleting a credentials row on a table nothing beneath the
                // application polices. An empty set is the correct end state.
                Credential set = await recoveryCodes.FindRecoveryCodeCredentialAsync(code.UserId, token)
                    ?? throw new RecoveryCodeRedemptionException(
                        "The matched code names a set whose credential is not there.");

                Session session = Session.Establish(set, now, now + SessionLifetime);
                await sessionRepository.AddAsync(session, token);

                // Counted after the consume, inside the same transaction, so the number is what the
                // card is worth now rather than what it was worth when the request arrived.
                //
                // Through the read service rather than by subtracting one from a count taken earlier:
                // a second way to count is a second answer that can disagree with the first, and this
                // one names the owner, which is what scopes a read of a table no policy narrows.
                int remaining = await readService.CountRemainingForUserAsync(code.UserId, token);

                return new RedeemedRecoveryCode(session.Kind, session.ExpiresAtUtc, remaining);
            },
            cancellationToken);
    }
}
