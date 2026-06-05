using Application.Abstractions;
using Application.Transactions.Queries.GetTransaction;
using Domain.Entities;
using MediatR;

namespace Application.Transactions.Commands.UpdateTransaction;

public record UpdateTransactionCommand(Guid UserId, TransactionDto TransactionDto) : IRequest<Unit>;

public sealed class UpdateTransactionCommandHandler(ITransactionsRepository transactionsRepository)
    : IRequestHandler<UpdateTransactionCommand, Unit>
{
    public async Task<Unit> Handle(UpdateTransactionCommand request, CancellationToken cancellationToken)
    {
        Transaction transaction = request.TransactionDto.ToEntity(request.UserId);

        return await transactionsRepository.UpdateAsync(transaction, cancellationToken);
    }
}
