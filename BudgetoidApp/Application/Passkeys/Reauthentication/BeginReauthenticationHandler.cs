using Application.Abstractions;
using Application.Passkeys.BeginAssertion;

namespace Application.Passkeys.Reauthentication;

/// <summary>
/// Issues a re-authentication challenge and the request options bound to it.
/// </summary>
/// <remarks>
/// <para>
/// Authenticated, unlike the sign-in leg it otherwise mirrors, and that is what the third nonce pool
/// is for. If this leg were anonymous anyone could mint the nonce that authorizes an erasure, and
/// refusing the other two pools on the erasure path would buy nothing at all.
/// </para>
/// <para>
/// Nothing about the caller reaches the answer, even though the caller is known here. The response is
/// <see cref="PasskeyRequestOptions"/> unchanged — no <c>allowCredentials</c>. Enumeration is not the
/// argument this time, since the account is already established: handing the account's credential
/// handles back to whoever holds the bearer token would give the stolen-session adversary something it
/// did not arrive with. The authenticator is asked for whatever discoverable credential it holds for
/// this relying party, exactly as at sign-in.
/// </para>
/// <para>
/// The timeout is the shorter of the configured ceremony timeout and what is left of the challenge's
/// life — the same call the sign-in and registration legs make, on the same policy value, against an
/// expiry all three pools take from the store's one challenge lifetime. It carries no extra weight
/// here, and trimming it from either of the other two would break the same rule it keeps on this one:
/// never promise a prompt that outlives the nonce behind it, or a person completes a ceremony that was
/// already refused before they touched their authenticator. How fresh <em>this</em> ceremony's proof
/// is is enforced by <c>ConsumeAsync</c> refusing an expired nonce, never by what the client was told.
/// </para>
/// <para>
/// The clamp binds only where the configured ceremony timeout is the longer of the two, which is to
/// say only where <c>Authentication:Passkey:CeremonyTimeoutSeconds</c> is set above the challenge
/// lifetime. The default ceremony timeout is already well under it, so on a default configuration this
/// call returns that timeout unchanged on every leg.
/// </para>
/// </remarks>
public sealed class BeginReauthenticationHandler(
    IWebAuthnChallengeStore challengeStore,
    IPasskeyCeremonyPolicy policy,
    TimeProvider timeProvider) : ICommandHandler<BeginReauthenticationCommand, PasskeyRequestOptions>
{
    public async Task<PasskeyRequestOptions> HandleAsync(
        BeginReauthenticationCommand command,
        CancellationToken cancellationToken = default)
    {
        // The pool is written here, in the one place a caller cannot reach.
        IssuedChallenge issued = await challengeStore.IssueAsync(
            WebAuthnCeremony.Reauthentication,
            cancellationToken);

        return new PasskeyRequestOptions
        {
            Challenge = PasskeyEncoding.Encode(issued.Challenge.Span),
            RpId = policy.RelyingPartyId,
            Timeout = PasskeyCeremonyTimeout.Milliseconds(
                policy.CeremonyTimeout,
                issued.ExpiresAtUtc,
                timeProvider),
            UserVerification = PasskeyCeremonyConstants.RequiredUserVerification,
        };
    }
}
