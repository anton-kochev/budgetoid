using Application.Payees;
using Domain.Payees;

namespace UnitTests.Fakes;

public sealed class InMemoryPayeeRepository(Guid budgetId, TimeProvider timeProvider) : IPayeeRepository, IPayeeReadService
{
    private readonly List<Payee> _payees = [];

    public int GetOrCreateCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }

    public Task<IReadOnlyList<PayeeDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PayeeDto> payees = _payees
            .OrderBy(payee => payee.Name)
            .Select(payee => new PayeeDto(payee.Id, payee.Name))
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

        Payee payee = Payee.Create(budgetId, normalizedName, timeProvider.GetUtcNow().UtcDateTime);
        _payees.Add(payee);
        return Task.FromResult(payee);
    }

    // No budget filter: every payee this fake holds was stamped with the one budget id it was
    // constructed with, so it can only match on the payee id. Budget isolation is exercised against
    // the real EF query filters instead.
    public Task<Payee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Payee? payee = _payees.SingleOrDefault(payee => payee.Id == id);
        return Task.FromResult(payee);
    }

    // Nothing to store: callers rename the instance handed back by GetByIdAsync, which is the very
    // instance held in the list, so the new name is already visible here. The real repository has the
    // same shape — it saves changes to an entity the context is already tracking. The counter is
    // what a test can assert on to prove the save was asked for at all.
    public Task UpdateAsync(Payee payee, CancellationToken cancellationToken = default)
    {
        UpdateCallCount++;
        return Task.CompletedTask;
    }
}
