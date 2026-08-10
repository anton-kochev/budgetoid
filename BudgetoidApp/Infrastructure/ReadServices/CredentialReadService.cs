using Application.Users;
using Application.Users.ListCredentials;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class CredentialReadService(BudgetoidDbContext dbContext) : ICredentialReadService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CredentialSummary>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // The owner predicate is the ONLY thing scoping this read. credentials is exempt from
        // row-level security — it is the table a request is resolved out of — so no policy narrows it,
        // no query filter narrows it, and the grant is on the whole table. Without it every caller is
        // handed every account's sign-in inventory, and
        // Credentials_ForASecondAccount_ListThatAccountsCredentialsAndNotTheFirsts is the only thing in
        // the codebase that would notice; data-isolation.md names it by that spelling.
        //
        // CreatedAtUtc ascending is the contract; the Id tiebreaker only makes ties deterministic here
        // and is deliberately not promised — see ICredentialReadService.
        //
        // No tracking, because nothing mutates what this returns.
        await dbContext.Credentials
            .AsNoTracking()
            .Where(credential => credential.UserId == userId)
            .OrderBy(credential => credential.CreatedAtUtc)
            .ThenBy(credential => credential.Id)
            .Select(credential => new CredentialSummary(
                credential.Id,
                credential.Type,
                credential.CreatedAtUtc))
            .ToListAsync(cancellationToken);
}
