namespace Application.Abstractions;

/// <summary>
/// The budget every request operates inside — the unit of tenancy. Resolved eagerly at provisioning,
/// because the global query filters read it synchronously while a query is being built.
/// </summary>
public interface IBudgetContext
{
    /// <summary>
    /// The ambient budget, or <see langword="null"/> when the current request has not resolved one —
    /// user provisioning runs before a budget id exists, and infrastructure scopes such as health
    /// checks never have a request principal at all. This is the accessor for callers that must
    /// tolerate that state; <see cref="BudgetId"/> stays the strict one the query filters read, and a
    /// caller that has a tenant-scoped query to run wants the throw, not a null.
    /// </summary>
    Guid? ResolvedBudgetId { get; }

    /// <summary>
    /// The ambient budget, throwing when the current request has not resolved one. The global query
    /// filters read this: a filter that silently compared against a null budget would match nothing
    /// or — worse, on the write side — scope nothing, so failing loudly is the only safe answer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ambient budget has not been resolved.</exception>
    // Implemented here rather than left to implementers: this form is definitionally
    // ResolvedBudgetId with null rejected, so there is no decision for an implementation to make,
    // and every hand-written copy would be this same rejection. It also keeps the two accessors
    // agreeing by default, which matters once the RLS session variable reads one and the query
    // filters read the other — two separately written members could name different budgets and
    // nothing would fail.
    Guid BudgetId => ResolvedBudgetId
        ?? throw new InvalidOperationException("The ambient budget for the current request has not been resolved.");
}
