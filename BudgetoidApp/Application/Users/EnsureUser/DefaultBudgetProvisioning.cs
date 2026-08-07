using Domain.Budgets;

namespace Application.Users.EnsureUser;

/// <summary>
/// The one rule for how a user comes to own its default budget, shared by both halves of provisioning.
/// </summary>
/// <remarks>
/// <see cref="ResolveUserHandler" /> runs it to heal an account whose budget insert was lost;
/// <see cref="EnsureUserHandler" /> runs it to give a freshly minted account its first budget. Two
/// copies of this would be two places for the ordering and the conflict re-read to drift apart, and the
/// drift would not surface as an error — it would surface as a signed-in person whose budget-scoped
/// queries all come back empty. It takes the repository and the clock as arguments rather than being a
/// collaborator of its own so that neither handler's constructor has to grow a dependency the tests
/// pinning those constructors do not know about.
/// </remarks>
internal static class DefaultBudgetProvisioning
{
    /// <summary>
    /// Returns the id of <paramref name="userId" />'s default budget, inserting it when there is none.
    /// </summary>
    /// <remarks>
    /// The caller must have published <paramref name="userId" /> as the request's identity first:
    /// <c>budgets</c> is policed by <c>user_isolation</c>, so a read issued before the publication runs
    /// against an <c>app.current_user_id</c> that names nobody.
    /// </remarks>
    internal static async Task<Guid> EnsureDefaultBudgetIdAsync(
        IBudgetRepository budgetRepository,
        TimeProvider timeProvider,
        Guid userId,
        CancellationToken cancellationToken)
    {
        Budget? existing = await budgetRepository.FindFirstForUserAsync(userId, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        Budget budget = Budget.CreateDefault(userId, timeProvider.GetUtcNow().UtcDateTime);
        if (await budgetRepository.TryAddAsync(budget, cancellationToken))
        {
            return budget.Id;
        }

        // A concurrent request won the unique insert; re-read to adopt its row.
        Budget? concurrentExisting = await budgetRepository.FindFirstForUserAsync(userId, cancellationToken);

        return concurrentExisting?.Id
               ?? throw new InvalidOperationException("Unique budget insert failed but budget could not be re-read.");
    }
}
