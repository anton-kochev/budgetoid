using Application.Abstractions;
using Domain.Transactions;

namespace Application.Transactions.GetTransactions;

public sealed class GetTransactionsHandler(ITransactionRepository repository)
    : IQueryHandler<GetTransactionsQuery, TransactionListResponse>
{
    public async Task<TransactionListResponse> HandleAsync(
        GetTransactionsQuery query,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Transaction> transactions = await repository.GetAllAsync(cancellationToken);

        return new TransactionListResponse([.. transactions.Select(TransactionDto.FromTransaction)]);
    }
}
