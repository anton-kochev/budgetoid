using Application.Abstractions;

namespace Api.Infrastructure;

public sealed class HttpContextBudgetContext(CurrentUser currentUser) : IBudgetContext
{
    public Guid? ResolvedBudgetId => currentUser.BudgetId;
}
