using Application.Abstractions;
using Domain.Common;
using Domain.Transactions;

namespace Application.Transactions.DeleteTransaction;

public sealed class DeleteTransactionHandler(ITransactionRepository repository)
    : ICommandHandler<DeleteTransactionCommand>
{
    public async Task HandleAsync(
        DeleteTransactionCommand command,
        CancellationToken cancellationToken = default)
    {
        // The lookup runs through the budget query filter, so a transaction belonging to another
        // budget is indistinguishable from one that never existed — both end here as a 404.
        Transaction? transaction = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (transaction is null)
        {
            throw new NotFoundException("Transaction was not found.");
        }

        await repository.DeleteAsync(transaction, cancellationToken);
    }
}
