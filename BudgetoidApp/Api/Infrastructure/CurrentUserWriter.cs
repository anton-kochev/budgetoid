using Application.Users.EnsureUser;

namespace Api.Infrastructure;

/// <summary>
/// Writes into the same scoped <see cref="CurrentUser"/> that <see cref="HttpContextUserContext"/> and
/// <see cref="HttpContextBudgetContext"/> read. Kept a type of its own rather than merged with either
/// reader so that only what is injected <see cref="IUserContextWriter"/> can name the request's
/// identity or its tenant.
/// </summary>
/// <remarks>
/// This is the only type that assigns <see cref="CurrentUser"/>. The two readers above are the only
/// others that take it at all, and both are read-only projections, so every publication in the
/// application passes through the two methods below — which is what makes the clearing rule in
/// <see cref="ResolveUser"/> a rule rather than a habit of whichever caller remembered it.
/// </remarks>
public sealed class CurrentUserWriter(CurrentUser currentUser) : IUserContextWriter
{
    public void ResolveUser(Guid userId)
    {
        currentUser.UserId = userId;

        // The budget is cleared with it, because SessionContextInterceptor writes app.current_user_id
        // and app.current_budget_id together at connection open: a user republished mid-request would
        // otherwise keep the budget resolved for the account the request arrived as, pairing one
        // account's id with another's tenant. budget_isolation is FOR ALL, so the first budget-scoped
        // statement added below such a republication would be scoped to a stranger, match nothing and
        // report success. Unresolved, IBudgetContext.BudgetId throws there instead. Provisioning calls
        // ResolveBudget after the id, never before, so it is unaffected.
        currentUser.BudgetId = null;
    }

    // The identity is left alone here, deliberately: this is the second half of a pair whose first half
    // has already named the account, and re-asserting it would give a caller that reversed the order a
    // way to end up with a coherent-looking pairing it never established.
    public void ResolveBudget(Guid budgetId) => currentUser.BudgetId = budgetId;
}
