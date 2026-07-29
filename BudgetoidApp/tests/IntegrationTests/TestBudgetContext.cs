using Application.Abstractions;

namespace IntegrationTests;

/// <summary>
/// Supplies an always-resolved fixed ambient budget to a hand-built <c>BudgetoidDbContext</c>,
/// standing in for the budget the provisioning middleware resolves per request.
/// </summary>
/// <remarks>
/// The unresolved state — <see cref="IBudgetContext.ResolvedBudgetId" /> coming back
/// <see langword="null" /> — is deliberately not modelled here. It belongs to
/// <c>HttpContextBudgetContext</c>, which is where a request can genuinely reach the query filters
/// before a budget exists. Only <see cref="IBudgetContext.ResolvedBudgetId" /> is implemented so
/// that <c>BudgetId</c> keeps coming from the interface's own derivation: a hand-written copy here
/// would silently override it, and a double whose two accessors could name different budgets than
/// production's do is the exact drift the derivation exists to prevent.
/// </remarks>
public sealed class TestBudgetContext(Guid budgetId) : IBudgetContext
{
    public Guid? ResolvedBudgetId { get; } = budgetId;
}
