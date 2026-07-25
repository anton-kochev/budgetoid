namespace Application.Abstractions;

/// <summary>
/// The budget every request operates inside — the unit of tenancy. Resolved eagerly at provisioning,
/// because the global query filters read it synchronously while a query is being built.
/// </summary>
public interface IBudgetContext
{
    Guid BudgetId { get; }
}
