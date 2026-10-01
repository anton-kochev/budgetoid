namespace Api.Infrastructure;

public sealed class CurrentUser
{
    public Guid? UserId { get; set; }

    /// <summary>
    /// The ambient budget, resolved while the request authenticates — the tenant every query filter
    /// scopes to, exposed to the rest of the app through <c>IBudgetContext</c>.
    /// </summary>
    public Guid? BudgetId { get; set; }
}
