using Application.Abstractions;
using MediatR;

namespace Application.Transactions.Commands.DeleteTransaction;

public record DeleteTransactionCommand(Guid TransactionId) : IRequest<Unit>;

public sealed class DeleteTransactionHandler(ITransactionsRepository transactionsRepository)
    : IRequestHandler<DeleteTransactionCommand, Unit>
{
    public async Task<Unit> Handle(DeleteTransactionCommand request, CancellationToken cancellationToken)
    {
        Guid userId = Guid.Empty; // TODO: Get user id from claims

        return await transactionsRepository.DeleteAsync(request.TransactionId, userId, cancellationToken);
    }
}
