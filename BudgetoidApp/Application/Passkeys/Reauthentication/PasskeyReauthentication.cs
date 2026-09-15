using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys.Verification;
using Domain.Users;

namespace Application.Passkeys.Reauthentication;

/// <summary>
/// Proves that whoever is making this request holds an authenticator registered to the account the
/// request is authenticated as, moments ago.
/// </summary>
/// <remarks>
/// <para>
/// A concrete class with no interface, on purpose. There is no boundary here to swap, and a stubbable
/// gate would let a unit test prove that erasure works with the gate faked out — the one thing that
/// must never be provable. It is a collaborator of the handler rather than a second call from an
/// endpoint for the same reason: composed in a route lambda, the next caller of the handler skips it
/// silently.
/// </para>
/// <para>
/// <b>The account is <see cref="IUserContext.UserId"/>, always.</b> The credential's own account is
/// never published and never selects anything; it is only ever checked against. This gate must
/// therefore never call <c>IUserContextWriter</c>, which is where the sign-in handler's most memorable
/// rule does <em>not</em> transfer. Publishing an identity here would <em>clear</em> the ambient budget
/// the erasure is about to be scoped by — <c>CurrentUserWriter.ResolveUser</c> clears it with every
/// publication — and the erasure reads that budget through
/// <c>TransactionRepository.DeleteAllForAmbientBudgetAsync</c>, whose entire scoping is the query
/// filter over it. The request would die at <c>IBudgetContext.BudgetId</c> instead of erasing anything.
/// </para>
/// <para>
/// That clearing is also what now prevents the older hazard, which is the second reason not to
/// reintroduce the call: were a republished id to leave the budget standing, Alice's bearer token with
/// Bob's passkey would empty <b>Alice's</b> budget while deleting <b>Bob's</b> user row — two accounts
/// destroyed, neither as asked. A publication here paired with a <c>ResolveBudget</c> beside it puts
/// that back.
/// </para>
/// <para>
/// The steps below repeat the shape of <c>CompleteAssertionHandler</c> and each states its own reason
/// rather than borrowing that one's, because the two paths differ in exactly the way that matters:
/// sign-in <em>discovers</em> an account and publishes it, this one <em>checks against</em> one
/// already published. A shared verifier hiding that difference is precisely the abstraction that would
/// later let somebody reintroduce the unscoped discovery lookup here, so the duplication is accepted.
/// </para>
/// <para>
/// Nothing here reads a clock. How fresh the proof is is the challenge's own server-issued lifetime,
/// enforced inside <see cref="IWebAuthnChallengeStore.ConsumeAsync"/> — measured from issue, which is
/// strictly before the person touched their authenticator, so the enforced gap is shorter than the
/// window rather than longer. A <c>TimeProvider</c> on this type would be an unread dependency
/// suggesting there is a second instant somewhere that matters.
/// </para>
/// </remarks>
public sealed class PasskeyReauthentication(
    IWebAuthnChallengeStore challengeStore,
    IPasskeyRepository passkeyRepository,
    IUserContext userContext,
    IPasskeyCeremonyPolicy policy)
{
    /// <summary>
    /// Runs the ceremony and returns only if it was proved. Every refusal is a
    /// <see cref="PasskeyVerificationException"/>, and they are deliberately indistinguishable in
    /// <em>content</em>: a caller able to tell "that passkey is not yours" from "that nonce was for
    /// another ceremony" is one mapping which handles exist while holding a stolen bearer token. The
    /// response bodies are byte-identical, and <c>EveryReachableErasureRefusal_ProducesTheIdenticalResponse</c>
    /// pins them that way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Content, not time.</b> A credential id that resolves at step 4 is refused only after a full
    /// signature verification and a counter read; one that resolves nothing is refused at step 4. The
    /// difference is measurable and nothing here measures or masks it. It is an accepted residual
    /// channel rather than an oversight: a WebAuthn credential id is 32 random bytes, so a timing
    /// answer cannot be walked towards a real handle, and every probe costs an options call and burns
    /// the nonce it spends. Do not reorder the ladder to flatten it — the lookup has to precede the
    /// verification because it supplies the key verified against, and the user handle that might
    /// otherwise identify the credential earlier is one a conforming authenticator may legally omit.
    /// </para>
    /// <para>
    /// <b>It hands back the credential it verified, and that does not touch the rule above.</b> The
    /// argument this class makes at length is about never publishing an <em>account</em> — never
    /// calling <c>IUserContextWriter</c>, because a publication here clears the ambient budget the
    /// caller is about to be scoped by. Returning the credential publishes nothing: the value goes to
    /// the one caller that asked for it and the ambient identity is untouched. What it is <em>not</em>
    /// is a discovery: the row was found by <see cref="IPasskeyRepository.FindPasskeyCredentialAsync"/>
    /// under the id the verified public key named and under <see cref="IUserContext.UserId"/>, so it is
    /// by construction a passkey of the account the request is already authenticated as. The shape
    /// difference from the sign-in handler stands — that one <em>discovers</em> an account and
    /// publishes it; this one checks against one already published and reports which credential did the
    /// checking.
    /// </para>
    /// <para>
    /// <b>Why a credential rather than nothing.</b> <c>BeginKeyRotationHandler</c> needs one, and the
    /// alternatives are worse in ways that do not announce themselves. Picking one out of the account's
    /// factor listing is arbitrary the day an account holds two passkeys, and the arbitrariness is
    /// invisible — the wrong one is a perfectly good credential of the right account and the right
    /// type. Taking a factor id off the command would put the choice in the caller's hands, and a
    /// server-verified assertion is strictly stronger than any identifier a client can send.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The passkey credential whose signature was verified — always one of
    /// <see cref="IUserContext.UserId"/>'s own.
    /// </returns>
    /// <exception cref="PasskeyVerificationException">The assertion was not accepted.</exception>
    public async Task<Credential> VerifyAsync(
        ReauthenticationAssertion assertion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);

        // 1. Decode and ceiling-check every member before any I/O. This leg is authenticated, so the
        //    denial-of-service argument the anonymous one makes is weaker here — but the ceilings cost
        //    nothing, and two ceremonies reading the same wire format with different bounds is a
        //    difference nobody meant. One refusal covers all five, oversized and unparseable alike,
        //    for the reason PasskeyVerificationException carries.
        //
        //    Before ConsumeAsync, and that ordering is a rule rather than a tidiness: a member the
        //    decoder rejects must not burn the nonce, or a caller who cannot produce a signature at all
        //    could still spend an issued challenge and turn a live ceremony into a refused one.
        if (!PasskeyEncoding.TryDecode(
                assertion.ClientDataJson,
                PasskeyPayloadLimits.ClientDataJsonBytes,
                out byte[]? clientDataJson)
            || !PasskeyEncoding.TryDecode(
                assertion.AuthenticatorData,
                PasskeyPayloadLimits.AssertionAuthenticatorDataBytes,
                out byte[]? authenticatorData)
            || !PasskeyEncoding.TryDecode(
                assertion.Signature,
                PasskeyPayloadLimits.SignatureBytes,
                out byte[]? signature)
            || !PasskeyEncoding.TryDecode(
                assertion.CredentialId,
                PasskeyPayloadLimits.CredentialIdBytes,
                out byte[]? webAuthnCredentialId)
            || !TryDecodeUserHandle(assertion.UserHandle, out byte[]? userHandle))
        {
            throw new PasskeyVerificationException("A member of the assertion was not base64url text.");
        }

        // 2. Parse the client data, which is where the challenge bytes come from. Refused before the
        //    nonce is spent, for the same reason as the decode above: a body that never named a
        //    challenge cannot consume one.
        if (!CollectedClientData.Parse(clientDataJson).TryGetValue(out CollectedClientData? clientData, out _))
        {
            throw new PasskeyVerificationException("clientDataJSON was not the JSON object a ceremony produces.");
        }

        // 3. Spend the challenge BEFORE anything is verified, so a failed attempt burns it. Sign-in
        //    makes the same choice; the consequence is worse here, because an unspent nonce would be an
        //    unlimited-attempt oracle standing in front of account destruction rather than in front of
        //    a session.
        //
        //    Required to be Reauthentication, not merely live. Non-null would accept an Authentication
        //    nonce, which is minted anonymously, and a Registration nonce, which is minted for a
        //    signed-in person — the adversary this gate exists to stop. Expiry is this call's job too:
        //    the five-minute freshness window is the challenge's lifetime, and it is enforced here
        //    rather than by anything this class reads.
        WebAuthnCeremony? ceremony = await challengeStore.ConsumeAsync(clientData.Challenge, cancellationToken);
        if (ceremony is not WebAuthnCeremony.Reauthentication)
        {
            throw new PasskeyVerificationException("The challenge is not a live re-authentication challenge.");
        }

        // 4. The owner-scoped key lookup — the step with no counterpart in the sign-in handler, and the
        //    one that carries the account binding. The user id comes from the request; the handle is
        //    all the caller supplied. A handle registered to somebody else answers nothing here, so the
        //    mismatch is refused by construction rather than by a comparison a refactor can delete.
        //    Reusing the unscoped discovery lookup and comparing afterwards would verify a stranger's
        //    signature perfectly and erase this account on the strength of it.
        Guid userId = userContext.UserId;
        PasskeyPublicKey? publicKey = await passkeyRepository.FindByWebAuthnCredentialIdForUserAsync(
            userId,
            webAuthnCredentialId,
            cancellationToken);
        if (publicKey is null)
        {
            throw new PasskeyVerificationException(
                "No passkey of the account the request is authenticated as answers to the presented credential id.");
        }

        // 5. A user handle the authenticator returned has to name the account the REQUEST is
        //    authenticated as — not the credential's account, which is where this differs from sign-in
        //    and is the single thing a future reader is most likely to get backwards. Step 4 already
        //    proved the two are the same, so the comparands are interchangeable and only one of them
        //    states the rule the right way round. Absent is tolerated: a conforming authenticator may
        //    omit the handle, and it proves nothing the signature does not already prove.
        if (userHandle is not null
            // FixedTimeEquals returns false rather than throwing on a length mismatch, so a handle of
            // the wrong size is a refusal like any other. Fixed time because the comparand identifies
            // an account, and a byte-at-a-time exit would let a caller walk a guess towards a real one.
            && !CryptographicOperations.FixedTimeEquals(userHandle, PasskeyEncoding.ToUserHandle(userId)))
        {
            throw new PasskeyVerificationException(
                "The user handle did not name the account the request is authenticated as.");
        }

        // 6. The signature, against the STORED key and the STORED algorithm. Reading either out of the
        //    request would let a caller name the algorithm whose verification it can satisfy and supply
        //    the key it holds the private half of — on the request that destroys an account.
        PasskeyAssertionExpectations expectations = new()
        {
            // The bytes the store just confirmed it issued and had not yet spent, so the verifier's own
            // challenge equality is already satisfied. What makes the nonce mean anything is the
            // ConsumeAsync above, not this comparison.
            Challenge = clientData.Challenge,
            AllowedOrigins = policy.AllowedOrigins,
            RelyingPartyId = policy.RelyingPartyId,
            CoseKey = publicKey.CoseKey,
            Algorithm = publicKey.Algorithm,
        };

        if (!PasskeyAssertionVerifier.Verify(clientDataJson, authenticatorData, signature, expectations)
                .TryGetValue(out VerifiedAssertion? verified, out PasskeyVerificationFailure failure))
        {
            throw new PasskeyVerificationException(failure);
        }

        // 7. The counter, honoured rather than skipped on the grounds that the row is about to be
        //    cascaded away anyway: an assertion has to mean the same thing on every path it is accepted
        //    on, and a clone detector that one ceremony quietly opts out of is not a clone detector.
        //    passkey_signature_counters is policed by user_isolation and the request's identity was
        //    published while its cookie was authenticated, long before this line, so the read and the
        //    write are scoped by the database.
        PasskeySignatureCounter counter =
            await passkeyRepository.FindCounterAsync(publicKey.CredentialId, cancellationToken)
            ?? throw new PasskeyVerificationException("The passkey has a public key but no signature counter.");

        if (AcceptCounter(counter, verified.SignCount))
        {
            // Written only when the value moved. A synced authenticator reports zero every time, and
            // writing an unchanged row would be an UPDATE per ceremony that records nothing.
            await passkeyRepository.SaveCounterAsync(counter, cancellationToken);
        }

        // 8. The credential row behind the key that was just verified, read LAST and never earlier.
        //    Every step above is a refusal, and the ladder's order is a security property this class
        //    argues at length — a read inserted before the signature verification would move work in
        //    front of a refusal and change which refusals are cheap.
        //
        //    FindPasskeyCredentialAsync and never a lookup keyed by id alone: credentials is exempt from
        //    row-level security, so nothing beneath this call narrows it and the owner in the predicate
        //    is the only thing scoping the read. The owner is IUserContext.UserId — the same value step
        //    4 scoped the key lookup by — so this cannot answer with another account's row, and the
        //    type filter inside it cannot answer with a federated or recovery-codes credential.
        //
        //    A miss is impossible through the schema — passkey_public_keys.credential_id is a foreign
        //    key onto the row this is asking for — so it is a broken invariant rather than a caller's
        //    mistake, and it is refused with the identical sentence every other refusal on this path
        //    produces, for the reason the counter's own miss gives: a caller able to tell one refusal
        //    from another is one mapping which handles exist while holding a stolen bearer token.
        return await passkeyRepository.FindPasskeyCredentialAsync(
                publicKey.CredentialId,
                userId,
                cancellationToken)
            ?? throw new PasskeyVerificationException(
                "The passkey has a public key but no credential row.");
    }

    /// <summary>
    /// Decodes the optional user handle, treating absence as success with nothing decoded.
    /// </summary>
    /// <remarks>
    /// Folded into the decode chain above so a handle that is present and not base64url produces the
    /// one refusal every other malformed member does. Sign-in answers that case with a sentence of its
    /// own; here it must not, because the handle is checked against the request's account and a caller
    /// able to separate "your handle was gibberish" from "your handle named somebody else" learns which
    /// of the two happened.
    /// </remarks>
    private static bool TryDecodeUserHandle(string? value, out byte[]? decoded)
    {
        if (value is null)
        {
            decoded = null;
            return true;
        }

        return PasskeyEncoding.TryDecode(value, PasskeyPayloadLimits.UserHandleBytes, out decoded);
    }

    // The domain reports a counter regression by throwing, which is right for a rule about the
    // aggregate and wrong for this response: a caller able to tell "that counter went backwards" from
    // "no such credential" has learned the handle it presented is real. Translated here so an erasure
    // is refused with the one answer every other refusal produces, while the log keeps the distinction.
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
