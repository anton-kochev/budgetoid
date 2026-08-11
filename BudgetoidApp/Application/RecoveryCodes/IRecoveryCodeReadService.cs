namespace Application.RecoveryCodes;

/// <summary>
/// Reads about an account's recovery codes that are for showing, not for deciding.
/// </summary>
/// <remarks>
/// <para>
/// A read service rather than a method on <see cref="Domain.Users.IRecoveryCodeRepository"/>, the same
/// split <see cref="Application.Users.ICredentialReadService"/> states: the repository loads the
/// aggregate a rule is then applied to, and this projects a number for a response. <b>Nothing that
/// decides anything may read through here.</b> The redemption path — which has to find one row by its
/// hash and remove it — belongs on the repository, and a "does this account still have codes?" gate
/// keyed on this projection would be a rule keyed on a value chosen for display, which is precisely
/// what the split exists to prevent.
/// </para>
/// <para>
/// Nothing here returns a hash, a verifier or an id. A stored hash is the value a redemption is matched
/// against, and an id in a response body is an id in a client log.
/// </para>
/// </remarks>
public interface IRecoveryCodeReadService
{
    /// <summary>
    /// How many unredeemed recovery codes <paramref name="userId"/> still holds — zero when the account
    /// has never been issued a set, which is the same answer and deliberately not a distinct one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner argument is the only thing that scopes this read.</b> <c>recovery_code_hashes</c> is
    /// exempt from row-level security — a redemption arrives anonymous and adopts the <c>user_id</c> it
    /// finds on the row, so a policy keyed on an identity the request has not established yet could not
    /// run — so no policy narrows it, no query filter narrows it, and the grant is on the whole table.
    /// Drop the predicate and every caller is told how many codes the whole installation holds.
    /// </para>
    /// <para>
    /// It counts <b>rows</b> rather than reading a set: an account with no set holds no rows and
    /// answers zero, so this cannot express the 404 that a "find the set, then count its codes" shape
    /// reaches for. "You have no codes" and "you have zero left" are the same actionable fact.
    /// </para>
    /// </remarks>
    Task<int> CountRemainingForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
