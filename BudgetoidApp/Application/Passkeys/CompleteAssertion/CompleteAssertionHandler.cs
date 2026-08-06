using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys.Verification;
using Application.Users.EnsureUser;
using Domain.Common;
using Domain.Sessions;
using Domain.Users;

namespace Application.Passkeys.CompleteAssertion;

/// <summary>
/// Verifies a sign-in ceremony and establishes the session it opens.
/// </summary>
/// <remarks>
/// <para>
/// The order of the steps below is the security property, not an implementation detail, and each one
/// carries the reason it sits where it does. ADR 0012 records the same sequence.
/// </para>
/// <para>
/// This handler never reads <see cref="IUserContext"/> for the account. A caller may present a valid
/// provider bearer token <i>and</i> call this endpoint, in which case user provisioning has already
/// put that provider account on the request — and the account this ceremony signs in to is whichever
/// one the verified passkey belongs to, which need not be the same one. The account is taken from the
/// credential and published over whatever provisioning left.
/// </para>
/// </remarks>
public sealed class CompleteAssertionHandler(
    IWebAuthnChallengeStore challengeStore,
    IPasskeyRepository passkeyRepository,
    ISessionRepository sessionRepository,
    IUserContextWriter userContextWriter,
    IPasskeyCeremonyPolicy policy,
    ITransactionalExecutor transactionalExecutor,
    IPersistenceState persistenceState,
    TimeProvider timeProvider) : ICommandHandler<CompleteAssertionCommand, EstablishedSession>
{
    /// <summary>
    /// How long a passkey sign-in lasts.
    /// </summary>
    /// <remarks>
    /// Here rather than in Domain, and deliberately: <see cref="Session.Establish"/> takes an expiry
    /// instead of computing one, because how long a session lasts is product policy and the domain
    /// holds invariants. ADR 0002 keeps policy above the invariants for exactly this reason — the
    /// bottom is the most expensive layer to change, and this number is one somebody will want to
    /// change. It is not on <see cref="IPasskeyCeremonyPolicy"/> either: a session that expires after
    /// a different interval in one environment than another is a difference nobody meant.
    /// </remarks>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    public async Task<EstablishedSession> HandleAsync(
        CompleteAssertionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Bounded, and the ceiling is refused before the text is validated or decoded. This is the
        // application's anonymous surface: everything below this line — the discovery read that would
        // reject an unknown handle in microseconds included — is reached only after four members have
        // been decoded, so an unbounded decode here would let a caller name how much memory a refusal
        // costs. The limits are in PasskeyPayloadLimits with the protocol reasoning for each.
        //
        // Oversized is not a distinct answer. It lands on the same refusal as text that was not
        // base64url at all, for the reason PasskeyVerificationException carries: a caller able to tell
        // one refusal from another on this leg is a caller learning something about what is stored.
        if (!PasskeyEncoding.TryDecode(
                command.ClientDataJson,
                PasskeyPayloadLimits.ClientDataJsonBytes,
                out byte[]? clientDataJson)
            || !PasskeyEncoding.TryDecode(
                command.AuthenticatorData,
                PasskeyPayloadLimits.AssertionAuthenticatorDataBytes,
                out byte[]? authenticatorData)
            || !PasskeyEncoding.TryDecode(
                command.Signature,
                PasskeyPayloadLimits.SignatureBytes,
                out byte[]? signature)
            || !PasskeyEncoding.TryDecode(
                command.CredentialId,
                PasskeyPayloadLimits.CredentialIdBytes,
                out byte[]? webAuthnCredentialId))
        {
            throw new PasskeyVerificationException("A member of the assertion was not base64url text.");
        }

        // 1. Parse the client data and spend the challenge BEFORE anything is verified. A failed
        //    attempt burns the nonce, so a caller cannot grind responses against one issued challenge
        //    — which is what an oracle needs to be worth attacking. Consuming afterwards would leave
        //    every refusal replayable.
        if (!CollectedClientData.Parse(clientDataJson).TryGetValue(out CollectedClientData? clientData, out _))
        {
            throw new PasskeyVerificationException("clientDataJSON was not the JSON object a ceremony produces.");
        }

        WebAuthnCeremony? ceremony = await challengeStore.ConsumeAsync(clientData.Challenge, cancellationToken);
        if (ceremony is not WebAuthnCeremony.Authentication)
        {
            // Required to be Authentication, not merely live: a registration nonce is issued to a
            // signed-in person, and one spendable here would let a caller who obtained one assert an
            // existing credential with it.
            throw new PasskeyVerificationException("The challenge is not a live authentication challenge.");
        }

        // 2. The discovery read — the one statement in the system that runs with no identity on the
        //    connection. app.current_user_id is still '', so this may touch no policed table, and
        //    that is precisely why passkey_public_keys is exempt from row-level security (ADR 0012).
        //    The handle is all the caller supplied; the answer is what will establish who is asking.
        PasskeyPublicKey? publicKey = await passkeyRepository.FindByWebAuthnCredentialIdAsync(
            webAuthnCredentialId,
            cancellationToken);
        if (publicKey is null)
        {
            throw new PasskeyVerificationException("No passkey is registered under the presented credential id.");
        }

        // 3. A user handle the authenticator returned has to name the account the credential is filed
        //    under. Present-and-wrong is a refusal — it means the response was assembled from parts of
        //    two ceremonies. Absent is tolerated, because a conforming authenticator may omit it and
        //    the handle proves nothing the signature does not already prove.
        RequireMatchingUserHandle(command.UserHandle, publicKey.UserId);

        // 4. The signature, against the STORED key and the STORED algorithm. Reading either out of
        //    the request would let a caller name the algorithm whose verification it can satisfy, and
        //    supply the key it holds the private half of.
        PasskeyAssertionExpectations expectations = new()
        {
            // The bytes the store just confirmed it issued and had not yet spent, so the verifier's
            // own challenge equality is already satisfied. What makes the nonce mean anything is the
            // ConsumeAsync above, not this comparison.
            Challenge = clientData.Challenge,
            AllowedOrigins = policy.AllowedOrigins,
            RelyingPartyId = policy.RelyingPartyId,
            CoseKey = publicKey.CoseKey,
            Algorithm = publicKey.Algorithm,
        };

        if (!PasskeyAssertionVerifier.Verify(clientDataJson, authenticatorData, signature, expectations)
                .TryGetValue(out VerifiedAssertion? assertion, out PasskeyVerificationFailure failure))
        {
            throw new PasskeyVerificationException(failure);
        }

        // 5. Only now is the account published. Publishing before the signature verified would mean
        //    trusting a credential id an unauthenticated caller chose, and every policed statement
        //    after it would run as whoever they named.
        userContextWriter.ResolveUser(publicKey.UserId);

        // Read before the transaction opens so a retried attempt is stamped with one instant rather
        // than drifting with each replay.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        // 6. Only then is a transaction opened. This call site is load-bearing in its position: the
        //    transaction opens a connection, and opening a connection is when SessionContextInterceptor
        //    runs its set_config. Open it before ResolveUser above and app.current_user_id reaches the
        //    database as '', so every policed statement inside the transaction fails with 22P02.
        //    ADR 0011 states that precondition in the abstract; this is the first code path that can
        //    violate it.
        return await transactionalExecutor.ExecuteAsync(
            async token =>
            {
                // This delegate is idempotent under retry, and this line is what makes it so. The
                // API's EnrichNpgsqlDbContext installs NpgsqlRetryingExecutionStrategy, so a transient
                // failure anywhere below replays the whole delegate — against a database that rolled
                // the abandoned attempt back, and a change tracker that did not. Two things would
                // survive that rollback and neither is visible from here: the counter this attempt
                // advanced in memory, which the identity map would hand straight back to FindCounterAsync
                // so that Accept sees its own value and refuses a valid sign-in with the 401 a
                // regression gets; and the session the abandoned attempt queued, which the next save
                // would insert alongside the new one. Discarding both is cheaper than reasoning about
                // which of them survived.
                //
                // Nothing read before the transaction opened is needed as a tracked entity — the
                // public key is only read for the two ids it carries, and a detached instance still
                // carries them.
                persistenceState.DiscardTrackedEntities();

                // Counter first, session second, and the asymmetry is the reason. An advanced counter
                // with no session is a retryable inconvenience — the person tries again. A session
                // established over a counter that was never advanced is a replay window, because the
                // same assertion would open a second one.
                PasskeySignatureCounter counter =
                    await passkeyRepository.FindCounterAsync(publicKey.CredentialId, token)
                    ?? throw new PasskeyVerificationException(
                        "The passkey has a public key but no signature counter.");

                if (AcceptCounter(counter, assertion.SignCount))
                {
                    // Written only when the value moved. A synced authenticator reports zero every
                    // time, and writing an unchanged row would be an UPDATE per sign-in that records
                    // nothing.
                    await passkeyRepository.SaveCounterAsync(counter, token);
                }

                // The credential itself, read rather than reconstructed. Session.Establish derives the
                // session's kind from a Credential, and that derivation is what makes "a provider
                // sign-in reaching budget content" unrepresentable; a factory taking a CredentialType
                // instead would let a caller name the kind and dissolve the rule.
                Credential credential = await passkeyRepository.FindPasskeyCredentialAsync(
                        publicKey.CredentialId,
                        publicKey.UserId,
                        token)
                    ?? throw new PasskeyVerificationException(
                        "The passkey has a public key but no credential.");

                Session session = Session.Establish(credential, now, now + SessionLifetime);
                await sessionRepository.AddAsync(session, token);

                return new EstablishedSession(session.Kind, session.ExpiresAtUtc);
            },
            cancellationToken);
    }

    private static void RequireMatchingUserHandle(string? userHandle, Guid userId)
    {
        if (userHandle is null)
        {
            return;
        }

        if (!PasskeyEncoding.TryDecode(userHandle, PasskeyPayloadLimits.UserHandleBytes, out byte[]? decoded))
        {
            throw new PasskeyVerificationException("The user handle was not base64url text.");
        }

        // FixedTimeEquals returns false rather than throwing on a length mismatch, so a handle of the
        // wrong size is a refusal like any other. Fixed time because the comparand identifies an
        // account: a byte-at-a-time exit would let a caller walk a guessed handle towards a real one.
        if (!CryptographicOperations.FixedTimeEquals(decoded, PasskeyEncoding.ToUserHandle(userId)))
        {
            throw new PasskeyVerificationException("The user handle did not name the credential's account.");
        }
    }

    // The domain reports a counter regression by throwing, which is right for a rule about the
    // aggregate and wrong for this response: a caller able to tell "that counter went backwards" from
    // "no such credential" has learned the handle is real. Translated here so the client sees the one
    // refusal every other failure produces, while the log keeps the distinction.
    private static bool AcceptCounter(PasskeySignatureCounter counter, uint reported)
    {
        try
        {
            return counter.Accept(reported);
        }
        // Domain.Common.ValidationException by name: both layers declare one, and the counter's rule
        // lives in the domain.
        catch (Domain.Common.ValidationException)
        {
            throw new PasskeyVerificationException("The reported signature counter did not advance.");
        }
    }
}
