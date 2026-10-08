using Application.Transactions;
using Domain.Transactions;

namespace UnitTests.Fakes;

public sealed class InMemoryTransactionRepository : ITransactionRepository, ITransactionReadService
{
    private readonly List<Transaction> _transactions = [];
    private readonly Dictionary<Guid, string> _payeeNames = [];
    private readonly Dictionary<Guid, CategoryProjection> _categoryProjections = [];
    private readonly Dictionary<Guid, string> _accountNames = [];

    public int AddCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }

    /// <summary>
    /// The rows still here. Exposed so a collaborator can be given the database's own referential
    /// rule — this table is the child of four of the owned graph's five <c>ON DELETE RESTRICT</c>
    /// edges, to <c>budgets</c>, <c>accounts</c>, <c>categories</c> and <c>payees</c>, so a user
    /// delete is refused while any of these survive. See <c>InMemoryUserRepository</c>.
    /// </summary>
    public IReadOnlyList<Transaction> Transactions => _transactions;

    public void SetPayeeProjection(Guid payeeId, string payeeName) =>
        _payeeNames[payeeId] = payeeName;

    public void SetCategoryProjection(
        Guid categoryId,
        string categoryName,
        Guid categoryGroupId,
        string categoryGroupName) =>
        _categoryProjections[categoryId] = new CategoryProjection(
            categoryName,
            categoryGroupId,
            categoryGroupName);

    public void SetAccountProjection(Guid accountId, string accountName) =>
        _accountNames[accountId] = accountName;

    public Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        AddCallCount++;
        _transactions.Add(transaction);
        return Task.CompletedTask;
    }

    // No budget filter: this fake is not constructed with a budget id, so it can only match on the
    // transaction id. Budget isolation is exercised against the real EF query filters instead.
    public Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Transaction? transaction = _transactions.SingleOrDefault(transaction => transaction.Id == id);
        return Task.FromResult(transaction);
    }

    // Nothing to store: callers mutate the instance handed back by GetByIdAsync, which is the very
    // instance held in the list, so the edit is already visible here. The real repository has the
    // same shape — it saves changes to an entity the context is already tracking. The counter is
    // what a test can assert on to prove the save was asked for at all.
    public Task UpdateAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        UpdateCallCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        DeleteCallCount++;
        _transactions.Remove(transaction);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Empties the fake, standing in for a delete the <c>BudgetIsolation</c> query filter scopes to
    /// one budget. There is no budget to scope to here: this fake holds one tenant's rows and is not
    /// constructed with a budget id, so clearing it is the whole of the behaviour there is to stand
    /// in for. That the real method scopes to the ambient budget is proved against PostgreSQL in
    /// <c>TransactionRepositoryTests</c>.
    /// </summary>
    public Task DeleteAllForAmbientBudgetAsync(CancellationToken cancellationToken = default)
    {
        _transactions.Clear();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Transaction>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Transaction> results = OrderedTransactions().ToList();
        return Task.FromResult(results);
    }

    public Task<IReadOnlyList<TransactionDto>> GetAllWithPayeeAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TransactionDto> results = OrderedTransactions()
            .Select(transaction =>
            {
                CategoryProjection? category = transaction.CategoryId is { } categoryId
                                               && _categoryProjections.TryGetValue(categoryId, out CategoryProjection? value)
                    ? value
                    : null;

                return TransactionDto.FromTransaction(
                    transaction,
                    _accountNames.TryGetValue(transaction.AccountId, out string? accountName)
                        ? accountName
                        : "Account",
                    "USD",
                    "$",
                    transaction.PayeeId is { } payeeId
                    && _payeeNames.TryGetValue(payeeId, out string? payeeName)
                        ? payeeName
                        : null,
                    category?.CategoryName,
                    category?.CategoryGroupId,
                    category?.CategoryGroupName);
            })
            .ToList();

        return Task.FromResult(results);
    }

    // Projects through the list query and picks the id out of it, so a single transaction can never
    // read differently here than it does in the list — the same property the real read service gets
    // from sharing its join shape between the two queries.
    public async Task<TransactionDto?> GetByIdWithPayeeAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TransactionDto> transactions = await GetAllWithPayeeAsync(cancellationToken);
        return transactions.SingleOrDefault(transaction => transaction.Id == id);
    }

    private IOrderedEnumerable<Transaction> OrderedTransactions() => _transactions
        .OrderByDescending(transaction => transaction.Date)
        .ThenByDescending(transaction => transaction.CreatedAtUtc);

    private sealed record CategoryProjection(
        string CategoryName,
        Guid CategoryGroupId,
        string CategoryGroupName);
}
