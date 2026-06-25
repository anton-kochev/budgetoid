namespace Application.Transactions;

public interface ITransactionReadService
{
    Task<IReadOnlyList<TransactionDto>> GetAllWithPayeeAsync(CancellationToken cancellationToken = default);
}
