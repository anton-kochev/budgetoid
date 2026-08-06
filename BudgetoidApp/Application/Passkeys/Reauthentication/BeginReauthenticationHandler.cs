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
/// The timeout is the shorter of the ceremony timeout and what is left of the challenge's life, and it
/// is load-bearing here for a reason it does not carry elsewhere: the five-minute freshness window
/// this ceremony enforces <b>is</b> the challenge's lifetime, so a client prompt allowed to outlive it
/// would be a prompt whose answer is refused after the person has already touched their authenticator.
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
