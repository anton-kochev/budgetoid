namespace Api.Infrastructure;

public sealed class CurrentUser
{
    public Guid? UserId { get; set; }

    /// <summary>
    /// The ambient budget resolved at provisioning. Populated here but not yet consumed by any query
    /// filter; ownership is still scoped by user until the tenancy re-scope lands.
    /// </summary>
    public Guid? BudgetId { get; set; }
}
