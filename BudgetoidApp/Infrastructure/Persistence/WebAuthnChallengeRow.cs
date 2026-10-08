using Application.Abstractions;

namespace Infrastructure.Persistence;

/// <summary>
/// One outstanding WebAuthn challenge, kept only until it is spent or expires.
/// </summary>
/// <remarks>
/// <para>
/// A persistence-layer row rather than a domain entity, and deliberately so: a protocol nonce is not
/// something the product's rules are written about. It exists because WebAuthn is a two-message
/// exchange over a stateless transport and the server has to recognise, on the second message, a
/// value it issued on the first. Nothing in <c>Domain</c> should have to know that.
/// </para>
/// <para>
/// Internal to this assembly for the same reason. Whatever the application layer needs from it is a
/// question — "was this challenge issued for this ceremony, and is it still live?" — not a row, so
/// the row never has to leave.
/// </para>
/// </remarks>
internal sealed class WebAuthnChallengeRow
{
    public Guid Id { get; init; }

    // byte[] rather than the ReadOnlyMemory<byte> the passkey entities carry: those are domain
    // properties whose buffers a caller might still own, while this row is written and read by
    // infrastructure alone and never handed to anybody who could mutate it from a distance. The array
    // is what bytea maps to, so this also spares the row a value converter and a comparer.
    public byte[] Challenge { get; init; } = [];

    public WebAuthnCeremony Ceremony { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime ExpiresAtUtc { get; init; }
}
