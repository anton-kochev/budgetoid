using Application.RecoveryCodes;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class RecoveryCodeReadService(BudgetoidDbContext dbContext) : IRecoveryCodeReadService
{
    /// <inheritdoc />
    public Task<int> CountRemainingForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // The owner predicate is the ONLY thing scoping this read. recovery_code_hashes is exempt from
        // row-level security — a redemption arrives anonymous and adopts the user_id it finds on the
        // row, so a policy keyed on an identity the request has not established yet could not run — so
        // no policy narrows it, no query filter narrows it, and the grant is on the whole table.
        // Without it every caller is told how many unredeemed codes the whole installation holds —
        // which nothing notices unless a test seeds a second account and then reads the first's count.
        // RemainingCount_ForASecondAccount_CountsThatAccountsCodesAndNotTheFirsts is written that way,
        // and a suite that counts only one account's codes stays green with the predicate deleted.
        //
        // The set's credential is deliberately not read first and not joined to. Rows are what is
        // counted, so an account holding no set counts zero rather than having no set to count the
        // codes of — which is what keeps this read incapable of expressing a 404.
        //
        // Counted in the database rather than materialised and counted here: the projection is one
        // integer either way, and a ToListAsync would pull every stored hash of the account into this
        // process to measure the length of the list.
        //
        // No tracking, because nothing mutates what this returns — and on this table it buys more than
        // usual: a tracked RecoveryCodeHash is a copy EF would cascade into on a later delete of the
        // set's credential, taking the rows by the application instead of by the database's own
        // cascade. AsNoTracking on a Count is not load-bearing today, since a Count materialises no
        // entity; it is here so that a reader who later turns this into a projection does not have to
        // rediscover the rule. See IRecoveryCodeRepository.DeleteSetAsync.
        dbContext.RecoveryCodeHashes
            .AsNoTracking()
            .CountAsync(hash => hash.UserId == userId, cancellationToken);
}
