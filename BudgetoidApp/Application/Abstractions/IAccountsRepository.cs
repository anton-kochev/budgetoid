using Domain.Entities;

namespace Application.Abstractions;

public interface IAccountsRepository
{
    Task<IEnumerable<Account>> GetAccountsByUserId(Guid userId, CancellationToken cancellationToken);
    Task<Account?> GetAccountById(Guid accountId, Guid userId, CancellationToken cancellationToken);
}
