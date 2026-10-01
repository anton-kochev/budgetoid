using Application.Abstractions;

namespace Application.Passkeys.BeginAssertion;

/// <summary>
/// Issues an authentication challenge and the request options bound to it.
/// </summary>
/// <remarks>
/// Anonymous, and the first write path in the system an unauthenticated caller can reach: it inserts
/// a challenge row. What bounds that is the challenge's five-minute lifetime and the store's
/// opportunistic sweep, not rate limiting, which the product does not have — an accepted gap recorded
/// in ADR 0012.
/// </remarks>
public sealed class BeginAssertionHandler(
    IWebAuthnChallengeStore challengeStore,
    IPasskeyCeremonyPolicy policy,
    TimeProvider timeProvider) : ICommandHandler<BeginAssertionCommand, PasskeyRequestOptions>
{
    public async Task<PasskeyRequestOptions> HandleAsync(
        BeginAssertionCommand command,
        CancellationToken cancellationToken = default)
    {
        IssuedChallenge issued = await challengeStore.IssueAsync(WebAuthnCeremony.Authentication, cancellationToken);

        // Nothing about the caller reaches this answer. Two calls a second apart differ only in the
        // nonce, whether or not the caller has an account, which is what makes the endpoint say
        // nothing about who exists.
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
