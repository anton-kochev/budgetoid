namespace Application.Accounts;

public interface IAccountReadService
{
    Task<IReadOnlyList<AccountDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<AccountDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
