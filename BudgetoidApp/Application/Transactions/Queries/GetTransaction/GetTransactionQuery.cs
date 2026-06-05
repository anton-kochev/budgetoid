using Application.Abstractions;
using Domain.Entities;
using MediatR;

namespace Application.Transactions.Queries.GetTransaction;

public record GetTransactionQuery(Guid UserId, Guid TransactionId) : IRequest<TransactionDto?>;

public sealed class GetTransactionHandler(ITransactionsRepository transactionsRepository)
    : IRequestHandler<GetTransactionQuery, TransactionDto?>
{
    public async Task<TransactionDto?> Handle(GetTransactionQuery request, CancellationToken cancellationToken)
    {
        Transaction? entity =
            await transactionsRepository.GetAsync(request.TransactionId, request.UserId, cancellationToken);

        return entity?.ToDto();
    }
}
