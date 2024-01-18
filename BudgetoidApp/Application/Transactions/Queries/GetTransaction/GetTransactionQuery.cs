using Application.Abstractions;
using AutoMapper;
using Domain.Entities;
using MediatR;

namespace Application.Transactions.Queries.GetTransaction;

public record GetTransactionQuery(Guid UserId, Guid TransactionId) : IRequest<TransactionDto?>;

public sealed class GetTransactionHandler(ITransactionsRepository transactionsRepository, IMapperBase mapper)
    : IRequestHandler<GetTransactionQuery, TransactionDto?>
{
    public async Task<TransactionDto?> Handle(GetTransactionQuery request, CancellationToken cancellationToken)
    {
        Transaction? entity =
            await transactionsRepository.GetAsync(request.TransactionId, request.UserId, cancellationToken);

        return entity is not null
            ? mapper.Map<TransactionDto>(entity)
            : null;
    }
}
