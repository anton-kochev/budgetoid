using Application.Abstractions;

namespace Application.Transactions.GetTransactions;

public sealed class GetTransactionsHandler(ITransactionRepository repository)
    : IQueryHandler<GetTransactionsQuery, TransactionListResponse>
{
    public async Task<TransactionListResponse> HandleAsync(
        GetTransactionsQuery query,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TransactionDto> transactions = await repository.GetAllWithPayeeAsync(cancellationToken);

        return new TransactionListResponse(transactions);
    }
}
