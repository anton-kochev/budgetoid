namespace Api.Infrastructure;

/// <summary>
/// The problem-document member a refusal names itself in, for a client that has to act on which proof
/// was found wanting — or, on the locked sign-in's 404, on there being no account to sign in to.
/// </summary>
/// <remarks>
/// Spelled here rather than derived, for the reason <see cref="ConflictExceptionHandler" /> spells
/// <c>conflictKind</c>: extension keys are serialized verbatim, so no naming policy reaches this string.
/// The words written under it belong to whoever writes the refusal —
/// <see cref="PasskeyVerificationExceptionHandler.Refusal" />, <see cref="ProviderAuthorizationGate" />
/// and <c>SessionEndpoints.NoAccountRefusal</c>.
/// </remarks>
internal static class RefusalMember
{
    public const string Name = "refusal";
}
