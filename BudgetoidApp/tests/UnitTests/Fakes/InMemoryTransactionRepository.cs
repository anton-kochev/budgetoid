using Application.Transactions;
using Domain.Transactions;

namespace UnitTests.Fakes;

public sealed class InMemoryTransactionRepository : ITransactionRepository
{
    private readonly List<Transaction> _transactions = [];

    public int AddCallCount { get; private set; }

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
        IReadOnlyList<Transaction> results = _transactions
            .OrderByDescending(transaction => transaction.Date)
            .ThenByDescending(transaction => transaction.CreatedAtUtc)
            .ToList();

        return Task.FromResult(results);
    }
}

public sealed class StubUserContext(Guid userId) : Application.Abstractions.IUserContext
{
    public Guid UserId { get; } = userId;
}
