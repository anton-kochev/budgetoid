namespace Application.Abstractions;

/// <summary>
/// Issues the nonce a WebAuthn ceremony is bound to and spends it exactly once.
/// </summary>
/// <remarks>
/// <para>
/// The port lives here rather than in <c>Domain</c> because a protocol nonce is not a domain concept:
/// nothing in the product's rules is written about it, and it exists only because WebAuthn is a
/// two-message exchange over a stateless transport, so the server has to recognise on the second
/// message a value it issued on the first. <see cref="ITransactionalExecutor"/> is the existing
/// precedent for that split — an application-layer contract over something purely infrastructural.
/// </para>
/// <para>
/// What crosses the boundary is a question and its answer, never the row: "was this challenge issued
/// for a ceremony, and is it still live?"
/// </para>
/// </remarks>
public interface IWebAuthnChallengeStore
{
    /// <summary>
    /// Issues a fresh challenge bound to <paramref name="ceremony"/>, and returns it together with
    /// the instant it stops being spendable.
    /// </summary>
    Task<IssuedChallenge> IssueAsync(WebAuthnCeremony ceremony, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends <paramref name="challenge"/> and returns the ceremony it was issued for, or
    /// <see langword="null"/> when no live challenge answers to those bytes.
    /// </summary>
    /// <remarks>
    /// Single use is the whole point of the type: whatever the first caller does with the answer, a
    /// second call with the same bytes returns <see langword="null"/>. A challenge that was never
    /// issued, one already spent, and one that has expired are deliberately indistinguishable — the
    /// caller has no decision that depends on telling them apart, and an attacker replaying bytes
    /// would.
    /// </remarks>
    Task<WebAuthnCeremony?> ConsumeAsync(
        ReadOnlyMemory<byte> challenge,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A newly issued challenge: the bytes to send to the client, and when they stop being spendable.
/// </summary>
/// <remarks>
/// The expiry is returned rather than left implicit because the caller puts it in the credential
/// options it sends back — the client is told how long it has, and the number it is told has to be the
/// number the store will enforce.
/// </remarks>
public sealed record IssuedChallenge(ReadOnlyMemory<byte> Challenge, DateTime ExpiresAtUtc);
