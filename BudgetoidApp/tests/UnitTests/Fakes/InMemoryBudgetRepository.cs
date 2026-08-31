using Domain.Budgets;
using Domain.Security;

namespace UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IBudgetRepository"/> that reproduces the two database behaviours the tests
/// around it depend on: the documented ordering of <see cref="FindFirstForUserAsync"/>, and a unique
/// index on <c>(UserId, Name)</c> that makes <see cref="TryAddAsync"/> report failure instead of
/// throwing. The modelled index treats two nameless budgets as colliding, matching
/// <c>NULLS NOT DISTINCT</c> on the real index.
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
    /// the real repository translates PostgreSQL's <c>23505</c>. Two nameless budgets count as a
    /// collision, which is exactly the <c>NULLS NOT DISTINCT</c> semantics the real index is declared
    /// with and the half of it that is load-bearing.
    /// </summary>
    /// <remarks>
    /// The comparison is over bytes because the column is <c>bytea</c>, and the case-insensitive
    /// collation that used to make <c>"Household"</c> collide with <c>"household"</c> went with the
    /// text type — a collation is not a thing <c>bytea</c> has. What survives is the nameless half:
    /// two rows a client sealed from the same name hold different envelopes anyway, since every seal
    /// draws a fresh nonce, so this fake models a duplicate-name refusal no index performs any more.
    /// Refusing duplicate names is a blind index's job and this column has none.
    /// </remarks>
    public Task<bool> TryAddAsync(Budget budget, CancellationToken cancellationToken = default)
    {
        AddCallCount++;

        bool violatesUniqueName = _budgets.Any(existing =>
            existing.UserId == budget.UserId && HasSameName(existing.Name, budget.Name));

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

    // Two nulls are one value to the modelled index, which is what NULLS NOT DISTINCT says; anything
    // else is compared as the bytes the column holds, because that is all a bytea index can compare.
    private static bool HasSameName(NarrativeField? left, NarrativeField? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.Envelope.Span.SequenceEqual(right.Envelope.Span);
}
