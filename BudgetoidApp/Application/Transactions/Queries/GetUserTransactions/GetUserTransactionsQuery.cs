using Application.Abstractions;
using Application.Transactions.Queries.GetTransaction;
using MediatR;

namespace Application.Transactions.Queries.GetUserTransactions;

public record GetUserTransactionsQuery(Guid UserId) : IRequest<IList<TransactionDto>>;

public sealed class GetUserTransactionsHandler(ITransactionsRepository transactionsRepository)
    : IRequestHandler<GetUserTransactionsQuery, IList<TransactionDto>>
{
    public async Task<IList<TransactionDto>> Handle(GetUserTransactionsQuery request,
        CancellationToken cancellationToken)
    {
        return
            (await transactionsRepository.GetUserTransactionsAsync(request.UserId, cancellationToken))
            .ToDto()
            .ToList();
    }
}
