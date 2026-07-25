using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// The ambient budget for a unit-tested handler. Handlers only ever read the id, so a fixed value
/// is enough; the request-time resolution of that id is covered by the provisioning tests.
/// </summary>
public sealed class StubBudgetContext(Guid budgetId) : IBudgetContext
{
    public Guid BudgetId { get; } = budgetId;
}
