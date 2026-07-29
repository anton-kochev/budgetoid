using Application.Abstractions;

namespace Application.Transactions.GetTransaction;

public sealed class GetTransactionHandler(ITransactionReadService readService)
    : IQueryHandler<GetTransactionQuery, TransactionDto?>
{
    public async Task<TransactionDto?> HandleAsync(
        GetTransactionQuery query,
        CancellationToken cancellationToken = default)
    {
        return await readService.GetByIdWithPayeeAsync(query.Id, cancellationToken);
    }
}
