using Domain.Payees;

namespace Application.Payees;

public interface IPayeeRepository
{
    Task<IReadOnlyList<Payee>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Payee> GetOrCreateAsync(string name, CancellationToken cancellationToken = default);
}
