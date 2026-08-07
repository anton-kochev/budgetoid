using Domain.Budgets;

namespace UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IBudgetRepository"/> that reproduces the two database behaviours the tests
/// around it depend on: the documented ordering of <see cref="FindFirstForUserAsync"/>, and a unique
/// index on <c>(UserId, Name)</c> on a case-insensitive collation that makes
/// <see cref="TryAddAsync"/> report failure instead of throwing. The modelled index treats two
/// nameless budgets as colliding, matching <c>NULLS NOT DISTINCT</c> on the real index.
/// </summary>
/// <remarks>
/// Provisioning no longer reaches <see cref="TryAddAsync"/> at all — the default budget goes in with
/// the user and its credential, in one save through <c>IUserRepository</c> — so what a test asserts
/// about this method now is that it was <b>not</b> called. The collision is still modelled because
/// <see cref="IBudgetRepository.TryAddAsync"/> remains the seam every other budget insert goes
/// through, and <c>InMemoryBudgetRepositoryTests</c> is what keeps the modelling honest.
/// </remarks>
public sealed class InMemoryBudgetRepository : IBudgetRepository
{
    private readonly List<Budget> _budgets = [];
    private readonly List<Guid> _identityWhenFindFirstWasEntered = [];
    private RecordingUserContextWriter? _observedWriter;

    public int FindFirstCallCount { get; private set; }
    public int AddCallCount { get; private set; }

    public IReadOnlyList<Budget> Budgets => _budgets;

    /// <summary>
    /// Stores a budget directly, as if it had been persisted by an earlier request — or, on the
    /// provisioning path, by the one save that carried it in alongside its owner.
    /// </summary>
    public void Seed(Budget budget) => _budgets.Add(budget);

    /// <summary>
    /// Arms the recording of <see cref="IdentityWhenFindFirstWasEntered"/> against
    /// <paramref name="writer"/>.
    /// </summary>
    /// <remarks>
    /// Off unless a test asks for it, so the ordinary tests are not paying for a snapshot nothing
    /// reads — and so that a test which does read it has said so in its own Arrange block.
    /// </remarks>
    public void ObservePublicationsDuring(RecordingUserContextWriter writer) => _observedWriter = writer;

    /// <summary>
    /// The identity the session carried at the instant each <see cref="FindFirstForUserAsync"/> call
    /// was entered — the last id published, or <see cref="Guid.Empty"/> when nothing had been
    /// published yet. Empty list unless <see cref="ObservePublicationsDuring"/> armed it.
    /// </summary>
    /// <remarks>
    /// The last id and not the whole list, because the last one is what the database would see:
    /// <c>app.current_user_id</c> holds one value, and <c>user_isolation</c> on <c>budgets</c> reads
    /// exactly that. <see cref="Guid.Empty"/> for "nothing yet" is the same story — an unset setting
    /// reaches the policy as <c>''::uuid</c>, which names nobody.
    /// </remarks>
    public IReadOnlyList<Guid> IdentityWhenFindFirstWasEntered => _identityWhenFindFirstWasEntered;

    public Task<Budget?> FindFirstForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        FindFirstCallCount++;

        // Before the read, not after: this stands in for the identity the statement would have run
        // under, and a value sampled once the call had returned would include a publication the
        // statement never saw.
        if (_observedWriter is not null)
        {
            _identityWhenFindFirstWasEntered.Add(
                _observedWriter.Published.Count > 0 ? _observedWriter.Published[^1] : Guid.Empty);
        }

        Budget? budget = _budgets
            .Where(budget => budget.UserId == userId)
            .OrderBy(budget => budget.CreatedAtUtc)
            .ThenBy(budget => budget.Id)
            .FirstOrDefault();

        return Task.FromResult(budget);
    }

    /// <summary>
    /// Reports <see langword="false"/> for a budget the modelled unique index would reject, the way
    /// the real repository translates PostgreSQL's <c>23505</c>. Two null names count as a
    /// collision: <see cref="string.Equals(string?, string?, StringComparison)"/> answers
    /// <see langword="true"/> for a pair of nulls, which is exactly the <c>NULLS NOT DISTINCT</c>
    /// semantics the real index is declared with.
    /// </summary>
    public Task<bool> TryAddAsync(Budget budget, CancellationToken cancellationToken = default)
    {
        AddCallCount++;

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
