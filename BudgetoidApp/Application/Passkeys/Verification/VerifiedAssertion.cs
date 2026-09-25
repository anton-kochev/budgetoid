namespace Application.Passkeys.Verification;

/// <summary>
/// What an accepted assertion yields.
/// </summary>
/// <remarks>
/// The reported counter is returned, not judged. Whether it may follow the stored one is a domain
/// rule that lives with the counter, not with the signature check.
/// </remarks>
public sealed record VerifiedAssertion
{
    public required uint SignCount { get; init; }
}
