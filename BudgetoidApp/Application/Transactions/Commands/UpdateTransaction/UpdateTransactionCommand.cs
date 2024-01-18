using Application.Abstractions;
using Application.Transactions.Queries.GetTransaction;
using AutoMapper;
using Domain.Entities;
using MediatR;

namespace Application.Transactions.Commands.UpdateTransaction;

public record UpdateTransactionCommand(TransactionDto TransactionDto) : IRequest<Unit>;

public sealed class UpdateTransactionCommandHandler(ITransactionsRepository transactionsRepository, IMapperBase mapper)
    : IRequestHandler<UpdateTransactionCommand, Unit>
{
    public async Task<Unit> Handle(UpdateTransactionCommand request, CancellationToken cancellationToken)
    {
        Transaction transaction = mapper.Map<Transaction>(request.TransactionDto);

        return await transactionsRepository.UpdateAsync(transaction, cancellationToken);
    }
}
