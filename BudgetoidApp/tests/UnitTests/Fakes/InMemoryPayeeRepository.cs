using Application.Payees;
using Domain.Payees;

namespace UnitTests.Fakes;

public sealed class InMemoryPayeeRepository(Guid userId, TimeProvider timeProvider) : IPayeeRepository
{
    private readonly List<Payee> _payees = [];

    public int GetOrCreateCallCount { get; private set; }

    public Task<IReadOnlyList<Payee>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Payee> payees = _payees
            .OrderBy(payee => payee.Name)
            .ToList();

        return Task.FromResult(payees);
    }

    public Task<Payee> GetOrCreateAsync(string name, CancellationToken cancellationToken = default)
    {
        GetOrCreateCallCount++;
        string normalizedName = name.Trim();
        Payee? existing = _payees.SingleOrDefault(payee =>
            string.Equals(payee.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return Task.FromResult(existing);
        }

        Payee payee = Payee.Create(userId, normalizedName, timeProvider.GetUtcNow().UtcDateTime);
        _payees.Add(payee);
        return Task.FromResult(payee);
    }
}
