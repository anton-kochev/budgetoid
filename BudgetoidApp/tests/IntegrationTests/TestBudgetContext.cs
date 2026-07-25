using Application.Abstractions;

namespace IntegrationTests;

/// <summary>
/// Supplies a fixed ambient budget to a hand-built <c>BudgetoidDbContext</c>, standing in for the
/// budget the provisioning middleware resolves per request.
/// </summary>
public sealed class TestBudgetContext(Guid budgetId) : IBudgetContext
{
    public Guid BudgetId { get; } = budgetId;
}
