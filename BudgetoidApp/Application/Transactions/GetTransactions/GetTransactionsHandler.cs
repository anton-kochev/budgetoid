using Application.Abstractions;

namespace Application.Transactions.GetTransactions;

public sealed class GetTransactionsHandler(ITransactionReadService readService)
    : IQueryHandler<GetTransactionsQuery, TransactionListResponse>
{
    public async Task<TransactionListResponse> HandleAsync(
        GetTransactionsQuery query,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TransactionDto> transactions = await readService.GetAllWithPayeeAsync(cancellationToken);

        return new TransactionListResponse(transactions);
    }
}
