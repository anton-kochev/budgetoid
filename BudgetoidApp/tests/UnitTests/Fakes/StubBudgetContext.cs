using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// An always-resolved ambient budget for a unit-tested handler. Handlers only ever read the id, so
/// a fixed value is enough; the request-time resolution of that id is covered by the provisioning
/// tests.
/// </summary>
/// <remarks>
/// The unresolved state — <see cref="IBudgetContext.ResolvedBudgetId" /> coming back
/// <see langword="null" /> — is deliberately not modelled here. It belongs to
/// <c>HttpContextBudgetContext</c>, which is where a request can genuinely reach a handler before a
/// budget exists. Only <see cref="IBudgetContext.ResolvedBudgetId" /> is implemented so that
/// <c>BudgetId</c> keeps coming from the interface's own derivation: a hand-written copy here would
/// silently override it, and a stub whose two accessors could name different budgets than
/// production's do is the exact drift the derivation exists to prevent.
/// </remarks>
public sealed class StubBudgetContext(Guid budgetId) : IBudgetContext
{
    public Guid? ResolvedBudgetId { get; } = budgetId;
}
