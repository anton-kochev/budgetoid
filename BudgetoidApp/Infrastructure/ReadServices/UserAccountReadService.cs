using Application.Users;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class UserAccountReadService(BudgetoidDbContext dbContext) : IUserAccountReadService
{
    /// <inheritdoc />
    public async Task<string?> FindEmailAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // The id predicate is not what scopes this — users carries a user_isolation policy, so another
        // person's id comes back empty rather than theirs — but naming it is what makes the statement
        // an index seek rather than a scan the policy then filters.
        //
        // Selected as the Email value object rather than as Email.Value: the property carries a value
        // converter, so the provider translates the property itself and the unwrapping happens here.
        //
        // No tracking, because nothing mutates what this returns.
        Email? email = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);

        return email?.Value;
    }
}
