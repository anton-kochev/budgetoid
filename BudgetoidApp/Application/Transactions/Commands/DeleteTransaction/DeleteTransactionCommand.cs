using Application.Abstractions;
using MediatR;

namespace Application.Transactions.Commands.DeleteTransaction;

public record DeleteTransactionCommand(Guid UserId, Guid TransactionId) : IRequest<Unit>;

public sealed class DeleteTransactionHandler(ITransactionsRepository transactionsRepository)
    : IRequestHandler<DeleteTransactionCommand, Unit>
{
    public async Task<Unit> Handle(DeleteTransactionCommand request, CancellationToken cancellationToken)
    {
        return await transactionsRepository.DeleteAsync(request.TransactionId, request.UserId, cancellationToken);
    }
}
