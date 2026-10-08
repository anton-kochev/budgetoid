using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;

namespace UnitTests.Fakes;

/// <summary>
/// The five sets a rotation chunk loads through, over five dictionaries.
/// </summary>
/// <remarks>
/// <para>
/// <b>There are five arms and there is no budget arm, which is the shape of the port rather than an
/// omission here.</b> <c>Budget.ResealName</c> is <see langword="internal" /> and <c>Domain.csproj</c>
/// grants its internals to <c>Infrastructure</c> alone, so no caller in the Application ring can invoke
/// it; and widening the <c>budgets</c> grant to carry <c>rotation_id</c> would break the rule that the
/// role holds <c>UPDATE</c> on <c>budgets.name</c> and no other column. Every budget row is nameless
/// today and the completeness gate is presence-aware, so none is ever outstanding.
/// <c>docs/business-logic/key-rotation.md</c> argues both halves.
/// </para>
/// <para>
/// <b>A miss is a miss and never a null entity.</b> The five sets carry the <c>BudgetIsolation</c> query
/// filter in production, so a row belonging to another budget is <em>invisible</em> rather than
/// forbidden — the adapter answers a dictionary that simply does not hold that key. Modelled here by
/// holding only what a test seeded: a fake that manufactured an entity on demand would let a handler
/// under test reseal rows the real one could never have loaded.
/// </para>
/// <para>
/// <b>It holds no budget id and applies no scoping of its own.</b> Every entity seeded here was built
/// with the one budget the fixture uses, so there is nothing for a predicate to separate. Budget
/// isolation is exercised against the real query filters in the integration tier, which is the split
/// <see cref="InMemoryPayeeRepository" /> records for the same reason.
/// </para>
/// <para>
/// <b><see cref="OverAnswers" /> is the one seam here that models a <em>broken</em> adapter on
/// purpose, and it exists because without it a whole class of handler defect is unobservable.</b> A
/// fake that answers exactly the ids it was handed makes "iterate the command" and "iterate the
/// dictionary the port returned" the same program — so a handler written the second way, which is the
/// shorter line and reads perfectly well, cannot be told from one written the first way. Switched on,
/// a list call answers <b>every row of that kind this fake holds</b>, which is what a query that lost
/// its identifier predicate really does, and the two programs come apart. Every other case leaves it
/// off, so the ordinary arrangement stays the honest one.
/// </para>
/// </remarks>
public sealed class InMemoryNarrativeResealRepository : INarrativeResealRepository
{
    private readonly Dictionary<Guid, Account> _accounts = [];
    private readonly Dictionary<Guid, Payee> _payees = [];
    private readonly Dictionary<Guid, CategoryGroup> _categoryGroups = [];
    private readonly Dictionary<Guid, Category> _categories = [];
    private readonly Dictionary<Guid, Transaction> _transactions = [];

    /// <summary>
    /// How many times the chunk asked for its work to be persisted.
    /// </summary>
    /// <remarks>
    /// Zero is the assertion every refusal case makes beside its exception: a handler that mutated the
    /// entities and then threw has left the tracked graph rewritten, and on a real context the next save
    /// anybody makes flushes it. "It threw" is not "it rewrote nothing".
    /// </remarks>
    public int SaveCallCount { get; private set; }

    /// <summary>
    /// Read at the moment the save arrives, so a test can say which side of the unit of work it landed
    /// on. <see cref="ObservedTransactionalExecutor" /> argues the device.
    /// </summary>
    public Func<bool>? ObserveAtSave { get; set; }

    /// <inheritdoc cref="ObserveAtSave" />
    public bool? ObservationAtSave { get; private set; }

    /// <summary>
    /// Whether a list call answers <b>every</b> row of its kind this fake holds rather than the ones it
    /// was asked for — the shape a query that lost its identifier predicate produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, and only one case turns it on.</b> It is a deliberately wrong port, so an
    /// arrangement that switched it on without saying why would be measuring a system that does not
    /// exist — the real adapter is expected to scope its query, and
    /// <c>ResealChunkTests.ResealChunk_LoadsOnlyTheRowsTheChunkNames</c> is what says it does.
    /// </para>
    /// <para>
    /// <b>What it buys is a property of the <em>handler</em> rather than of the port: a chunk rewrites
    /// the rows the command names and no others, whatever the port hands back.</b> That is defence in
    /// depth and it is worth having on this path specifically, because the thing being written is a
    /// stamp the destructive completion step trusts — a row rewritten because it happened to be in the
    /// answer carries a claim the client never made.
    /// </para>
    /// </remarks>
    public bool OverAnswers { get; set; }

    public void Seed(Account account) => _accounts[account.Id] = account;

    public void Seed(Payee payee) => _payees[payee.Id] = payee;

    public void Seed(CategoryGroup categoryGroup) => _categoryGroups[categoryGroup.Id] = categoryGroup;

    public void Seed(Category category) => _categories[category.Id] = category;

    public void Seed(Transaction transaction) => _transactions[transaction.Id] = transaction;

    public Task<IReadOnlyDictionary<Guid, Account>> ListAccountsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Matching(_accounts, ids));

    public Task<IReadOnlyDictionary<Guid, Payee>> ListPayeesAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Matching(_payees, ids));

    public Task<IReadOnlyDictionary<Guid, CategoryGroup>> ListCategoryGroupsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Matching(_categoryGroups, ids));

    public Task<IReadOnlyDictionary<Guid, Category>> ListCategoriesAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Matching(_categories, ids));

    public Task<IReadOnlyDictionary<Guid, Transaction>> ListTransactionsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Matching(_transactions, ids));

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        SaveCallCount++;
        ObservationAtSave ??= ObserveAtSave?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The seeded rows whose identifiers <paramref name="ids" /> names — or, under
    /// <see cref="OverAnswers" />, every row of that kind this fake holds.
    /// </summary>
    /// <remarks>
    /// An id this fake does not hold is simply absent from the answer, which is what the query filter
    /// produces for a row of another budget — the whole reason the port answers a dictionary rather than
    /// a list the caller has to re-search. <paramref name="ids" /> is still read under
    /// <see cref="OverAnswers" />, so a caller passing a null list is a defect either way rather than a
    /// defect the seam hides.
    /// </remarks>
    private IReadOnlyDictionary<Guid, TEntity> Matching<TEntity>(
        Dictionary<Guid, TEntity> held,
        IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (OverAnswers)
        {
            return new Dictionary<Guid, TEntity>(held);
        }

        Dictionary<Guid, TEntity> found = [];
        foreach (Guid id in ids)
        {
            if (held.TryGetValue(id, out TEntity? entity))
            {
                found[id] = entity;
            }
        }

        return found;
    }
}
