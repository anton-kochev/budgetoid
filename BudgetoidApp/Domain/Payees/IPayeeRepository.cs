namespace Domain.Payees;

public interface IPayeeRepository
{
    Task<Payee> GetOrCreateAsync(string name, CancellationToken cancellationToken = default);
}
