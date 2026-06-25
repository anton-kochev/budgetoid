using Application.Transactions;
using Domain.Transactions;

namespace UnitTests.Fakes;

public sealed class InMemoryTransactionRepository : ITransactionRepository, ITransactionReadService
{
    private readonly List<Transaction> _transactions = [];
    private readonly Dictionary<Guid, string> _payeeNames = [];

    public int AddCallCount { get; private set; }

    public void SetPayeeProjection(Guid payeeId, string payeeName) => _payeeNames[payeeId] = payeeName;

    public Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        AddCallCount++;
        _transactions.Add(transaction);
        return Task.CompletedTask;
    }

    // The real repository relies on the EF global query filter for per-user scoping; this fake
    // has no such filter and returns everything it holds. Cross-user isolation is verified in
    // IntegrationTests/TransactionIsolationTests, not here.
    public Task<IReadOnlyList<Transaction>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Transaction> results = OrderedTransactions().ToList();

        return Task.FromResult(results);
    }

    public Task<IReadOnlyList<TransactionDto>> GetAllWithPayeeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TransactionDto> results = OrderedTransactions()
            .Select(transaction => TransactionDto.FromTransaction(
                transaction,
                transaction.PayeeId is { } payeeId && _payeeNames.TryGetValue(payeeId, out string? name)
                    ? name
                    : null))
            .ToList();

        return Task.FromResult(results);
    }

    private IOrderedEnumerable<Transaction> OrderedTransactions() => _transactions
        .OrderByDescending(transaction => transaction.Date)
        .ThenByDescending(transaction => transaction.CreatedAtUtc);
}

public sealed class StubUserContext(Guid userId) : Application.Abstractions.IUserContext
{
    public Guid UserId { get; } = userId;
}
