using Application.Abstractions;

namespace Api.Infrastructure;

public sealed class HttpContextBudgetContext(CurrentUser currentUser) : IBudgetContext
{
    public Guid BudgetId => currentUser.BudgetId
        ?? throw new InvalidOperationException("The current application user has not been resolved.");
}
