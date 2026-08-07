using Application.Users.EnsureUser;

namespace Api.Infrastructure;

/// <summary>
/// Writes into the same scoped <see cref="CurrentUser"/> that <see cref="HttpContextUserContext"/>
/// reads. Kept a type of its own rather than merged with the reader so that only what is injected
/// this interface can name the request's identity.
/// </summary>
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
        // report success. Unresolved, IBudgetContext.BudgetId throws there instead. Provisioning
        // publishes the budget after the id, never before, so it is unaffected.
        currentUser.BudgetId = null;
    }
}
