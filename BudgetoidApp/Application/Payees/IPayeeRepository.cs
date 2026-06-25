using Domain.Payees;

namespace Application.Payees;

public interface IPayeeRepository
{
    Task<Payee> GetOrCreateAsync(string name, CancellationToken cancellationToken = default);
}
