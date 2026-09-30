namespace Api.Infrastructure;

/// <summary>
/// The problem-document member a 401 names its refusal in, for a client that has to act on which
/// proof was found wanting.
/// </summary>
/// <remarks>
/// Spelled here rather than derived, for the reason <see cref="ConflictExceptionHandler" /> spells
/// <c>conflictKind</c>: extension keys are serialized verbatim, so no naming policy reaches this string.
/// The words written under it belong to whoever writes the refusal —
/// <see cref="PasskeyVerificationExceptionHandler.Refusal" /> and <see cref="ProviderAuthorizationGate" />.
/// </remarks>
internal static class RefusalMember
{
    public const string Name = "refusal";
}
