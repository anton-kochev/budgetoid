namespace Application.Transactions;

public interface ITransactionReadService
{
    Task<IReadOnlyList<TransactionDto>> GetAllWithPayeeAsync(CancellationToken cancellationToken = default);

    Task<TransactionDto?> GetByIdWithPayeeAsync(Guid id, CancellationToken cancellationToken = default);
}
