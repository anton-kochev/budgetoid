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
