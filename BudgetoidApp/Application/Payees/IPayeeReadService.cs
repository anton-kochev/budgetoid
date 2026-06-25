namespace Application.Payees;

public interface IPayeeReadService
{
    Task<IReadOnlyList<PayeeDto>> GetAllAsync(CancellationToken cancellationToken = default);
}
