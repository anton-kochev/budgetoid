namespace Domain.Budgets;

public interface IBudgetRepository
{
    /// <summary>
    /// Returns the user's earliest budget, ordered by <see cref="Budget.CreatedAtUtc"/> and then by
    /// <see cref="Budget.Id"/> as a deterministic tiebreaker, or <see langword="null"/> when the
    /// user owns no budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An account that exists owns a budget</b>, so on a resolved user the <see langword="null"/>
    /// means a broken invariant rather than a state the product can produce. All three rows of an
    /// account — the user, its credential and this budget — go in one save through
    /// <c>IUserRepository.TryAddAsync</c>, and nothing else inserts a default budget. A caller meeting
    /// the <see langword="null"/> should therefore fail loudly with an
    /// <see cref="InvalidOperationException"/>, not invent a budget and not answer 404: the account is
    /// there, it is the application's own guarantee that broke. Repairing it here was a heal charged
    /// to every authenticated request, and it is gone.
    /// </para>
    /// <para>
    /// The caller must have published <paramref name="userId"/> as the request's identity before this
    /// runs: <c>budgets</c> is policed by <c>user_isolation</c>, so a read issued under any other id
    /// comes back empty and the invariant above would be reported broken about an account that is
    /// intact.
    /// </para>
    /// <para>
    /// The ordering is part of the contract, not an implementation detail: UUID v7 sorts by creation
    /// time under PostgreSQL's <c>uuid</c> byte order but not under .NET's
    /// <see cref="Guid.CompareTo(Guid)"/>, so ordering by <see cref="Budget.CreatedAtUtc"/> first is
    /// what keeps an in-memory implementation and the database-backed one in agreement.
    /// </para>
    /// </remarks>
    Task<Budget?> FindFirstForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts <paramref name="budget"/>, returning <see langword="true"/> when the row was written and
    /// <see langword="false"/> when the uniqueness rule over
    /// (<see cref="Budget.UserId"/>, <see cref="Budget.Name"/>) refused it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No production caller.</b> The default budget is written by registration, into the same save
    /// as the account and its three credentials, so nothing in the application reaches
    /// this method — the same status <see cref="HasTransactionsAsync"/> already has. It stays because
    /// it is the seam that pins two rules nothing else can reach: the <c>NULLS NOT DISTINCT</c>
    /// semantics of the unique index, and the constraint-name attribution practice every repository
    /// in this codebase follows. Delete it and <c>BudgetRepositoryTests</c> and
    /// <c>RepositoryConstraintAttributionTests</c> lose their subject.
    /// </para>
    /// <para>
    /// <see langword="false"/> means that one rule and nothing else, because a caller answers it by
    /// re-reading the owner's budget and only a collision on that rule guarantees a winning row is
    /// there to be read. Every other rejection propagates — including one raised by an unrelated row
    /// the same unit of work was tracking, which a broader <see langword="false"/> would turn into a
    /// silent hunt for a budget that was never inserted.
    /// </para>
    /// </remarks>
    Task<bool> TryAddAsync(Budget budget, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <see langword="true"/> when the ambient budget holds at least one transaction, which
    /// is what makes it undeletable — recorded money movement is never discarded with the budget
    /// that holds it.
    /// </summary>
    /// <remarks>
    /// Deliberately takes no budget id. Tenancy comes from the <c>BudgetIsolation</c> query filter
    /// via <c>IBudgetContext</c>, which is the only authorization mechanism this system has, so a
    /// caller-supplied budget id would be a tenancy parameter with no ownership check to pair with
    /// it: the answer would be about whichever budget the caller named rather than the one it is
    /// entitled to.
    /// </remarks>
    Task<bool> HasTransactionsAsync(CancellationToken cancellationToken = default);
}
