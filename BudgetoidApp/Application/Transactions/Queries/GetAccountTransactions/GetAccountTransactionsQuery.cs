using Application.Abstractions;
using Application.Transactions.Queries.GetTransaction;
using AutoMapper;
using MediatR;

namespace Application.Transactions.Queries.GetAccountTransactions;

public record GetAccountTransactionsQuery(Guid UserId, Guid AccountId) : IRequest<IList<TransactionDto>>;

public sealed class GetAccountTransactionsHandler(ITransactionsRepository transactionsRepository, IMapperBase mapper)
    : IRequestHandler<GetAccountTransactionsQuery, IList<TransactionDto>>
{
    public async Task<IList<TransactionDto>> Handle(GetAccountTransactionsQuery request,
        CancellationToken cancellationToken)
    {
        return
            (await transactionsRepository
                .GetAccountTransactionsAsync(request.UserId, request.AccountId, cancellationToken))
            .Select(mapper.Map<TransactionDto>)
            .ToList();
    }
}
