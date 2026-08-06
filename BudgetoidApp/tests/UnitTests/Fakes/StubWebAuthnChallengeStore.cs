using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// A challenge store holding one issued nonce, spendable once.
/// </summary>
/// <remarks>
/// Single use is modelled rather than assumed: a store that answered the same bytes twice would let
/// a handler that consumes inside a retried unit of work look correct, and consuming inside one is
/// precisely what must not happen.
/// </remarks>
public sealed class StubWebAuthnChallengeStore(byte[] challenge, WebAuthnCeremony ceremony)
    : IWebAuthnChallengeStore
{
    private bool _spent;

    /// <summary>How many times a spend was attempted, whatever the answer was.</summary>
    public int ConsumeCallCount { get; private set; }

    public Task<IssuedChallenge> IssueAsync(
        WebAuthnCeremony issuedFor,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This stub answers a challenge it was handed; it issues none.");

    public Task<WebAuthnCeremony?> ConsumeAsync(
        ReadOnlyMemory<byte> presented,
        CancellationToken cancellationToken = default)
    {
        ConsumeCallCount++;

        if (_spent || !presented.Span.SequenceEqual(challenge))
        {
            return Task.FromResult<WebAuthnCeremony?>(null);
        }

        _spent = true;

        return Task.FromResult<WebAuthnCeremony?>(ceremony);
    }
}
