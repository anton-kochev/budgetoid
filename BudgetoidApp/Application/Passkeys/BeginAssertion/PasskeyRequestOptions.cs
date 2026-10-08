namespace Application.Passkeys.BeginAssertion;

/// <summary>
/// The <c>publicKey</c> member of a WebAuthn <c>navigator.credentials.get()</c> call.
/// </summary>
/// <remarks>
/// There is no <c>allowCredentials</c> member, and its absence is the design rather than an omission.
/// Sending one means the server first decided which credentials belong to the person signing in,
/// which means the request had to name them — and an endpoint that answers "here are that account's
/// credentials" for one address and nothing for another is an account-enumeration oracle. The
/// authenticator is asked to offer whatever discoverable credential it holds for this relying party
/// instead, which is why registration insists on a discoverable one.
/// </remarks>
public sealed record PasskeyRequestOptions
{
    public required string Challenge { get; init; }

    public required string RpId { get; init; }

    /// <summary>Milliseconds, matching WebAuthn's own <c>timeout</c>.</summary>
    public required long Timeout { get; init; }

    public required string UserVerification { get; init; }
}
