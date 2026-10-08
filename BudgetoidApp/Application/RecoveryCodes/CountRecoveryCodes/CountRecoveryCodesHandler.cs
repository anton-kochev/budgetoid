using Application.Abstractions;

namespace Application.RecoveryCodes.CountRecoveryCodes;

/// <summary>
/// Answers how many recovery codes the signed-in account has left.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes no <c>ILogger</c>, and must never take one</b>, for the reason
/// <c>ListCredentialsHandler</c> gives about the identifiers on its own path: what this handler holds
/// is a user id and a count of an account's remaining ways back in, and a log line copies both into a
/// sink with a different retention policy and a different audience from the table they came from. The
/// count is also a fact worth harvesting on its own — an account down to its last code is an account
/// worth attacking now — and no gate anywhere reads a log line from here, so there is nothing to trade
/// against.
/// </para>
/// <para>
/// <b>It runs behind no re-authentication gate</b>, and that is a decision rather than an omission. A
/// count is not destructive: it names no code, unlocks nothing, and tells a caller a fact about their
/// own account that the settings screen must have before it can render at all. The screen reads it on
/// load, so a gate would mint a re-authentication nonce on every page view — a live nonce for a
/// ceremony nobody intends to complete, behind a WebAuthn prompt the person did not ask for.
/// </para>
/// <para>
/// <b>Zero is an answer, never a refusal.</b> An account that has never been issued a set holds no rows
/// and is told it has zero left. There is no lookup of the set here to miss, deliberately: "you have no
/// codes" and "you have zero left" are the same actionable fact — generate a set — and a 404 would make
/// the client branch on a distinction it cannot use.
/// </para>
/// </remarks>
public sealed class CountRecoveryCodesHandler(
    IUserContext userContext,
    IRecoveryCodeReadService readService)
    : IQueryHandler<CountRecoveryCodesQuery, RecoveryCodeCount>
{
    public async Task<RecoveryCodeCount> HandleAsync(
        CountRecoveryCodesQuery query,
        CancellationToken cancellationToken = default)
    {
        // The request's own identity, never a value the query carried: see CountRecoveryCodesQuery.
        Guid userId = userContext.UserId;

        return new RecoveryCodeCount(await readService.CountRemainingForUserAsync(userId, cancellationToken));
    }
}
