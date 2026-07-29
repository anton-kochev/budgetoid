namespace Domain.Payees;

public interface IPayeeRepository
{
    Task<Payee> GetOrCreateAsync(string name, CancellationToken cancellationToken = default);

    Task<Payee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(Payee payee, CancellationToken cancellationToken = default);
}
