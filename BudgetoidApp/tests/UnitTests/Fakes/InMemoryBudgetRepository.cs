using Domain.Budgets;

namespace UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IBudgetRepository"/> that reproduces the two database behaviours the
/// provisioning flow depends on: the documented ordering of <see cref="FindFirstForUserAsync"/>,
/// and a unique index on <c>(UserId, Name)</c> on a case-insensitive collation that makes
/// <see cref="TryAddAsync"/> report failure instead of throwing.
/// </summary>
public sealed class InMemoryBudgetRepository : IBudgetRepository
{
    private readonly List<Budget> _budgets = [];
    private bool _failNextAdd;
    private Budget? _raceWinner;

    public int FindFirstCallCount { get; private set; }
    public int AddCallCount { get; private set; }

    public IReadOnlyList<Budget> Budgets => _budgets;

    /// <summary>
    /// Stores a budget directly, as if it had been persisted by an earlier request.
    /// </summary>
    public void Seed(Budget budget) => _budgets.Add(budget);

    /// <summary>
    /// Makes the next <see cref="TryAddAsync"/> call report a unique violation. The rejected budget
    /// is not stored. When <paramref name="insertedByConcurrentRequest"/> is supplied it is stored
    /// instead, modelling the row that won the race between our read and our write — which is what
    /// makes the caller's re-read path observable.
    /// </summary>
    public void FailNextAdd(Budget? insertedByConcurrentRequest = null)
    {
        _failNextAdd = true;
        _raceWinner = insertedByConcurrentRequest;
    }

    public Task<Budget?> FindFirstForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        FindFirstCallCount++;

        Budget? budget = _budgets
            .Where(budget => budget.UserId == userId)
            .OrderBy(budget => budget.CreatedAtUtc)
            .ThenBy(budget => budget.Id)
            .FirstOrDefault();

        return Task.FromResult(budget);
    }

    public Task<bool> TryAddAsync(Budget budget, CancellationToken cancellationToken = default)
    {
        AddCallCount++;

        if (_failNextAdd)
        {
            _failNextAdd = false;
            if (_raceWinner is not null)
            {
                _budgets.Add(_raceWinner);
                _raceWinner = null;
            }

            return Task.FromResult(false);
        }

        bool violatesUniqueName = _budgets.Any(existing =>
            existing.UserId == budget.UserId &&
            string.Equals(existing.Name, budget.Name, StringComparison.OrdinalIgnoreCase));

        if (violatesUniqueName)
        {
            return Task.FromResult(false);
        }

        _budgets.Add(budget);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Always reports no transactions. This fake stores budgets only — it has no ambient budget and
    /// no transactions to scope to one — so the same answer is the honest one for every budget it
    /// knows about. The real behaviour depends on the <c>BudgetIsolation</c> query filter and is
    /// covered against PostgreSQL in <c>BudgetRepositoryTests</c>.
    /// </summary>
    public Task<bool> HasTransactionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
