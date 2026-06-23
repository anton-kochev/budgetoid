using Application.Abstractions;
using Domain.Transactions;

namespace Application.Transactions.CreateTransaction;

public sealed class CreateTransactionHandler(
    ITransactionRepository repository,
    IUserContext userContext,
    TimeProvider timeProvider)
    : ICommandHandler<CreateTransactionCommand, TransactionDto>
{
    public async Task<TransactionDto> HandleAsync(
        CreateTransactionCommand command,
        CancellationToken cancellationToken = default)
    {
        Transaction transaction = Transaction.Create(
            userContext.UserId,
            command.Amount,
            command.Date,
            command.Description,
            timeProvider.GetUtcNow().UtcDateTime);

        await repository.AddAsync(transaction, cancellationToken);

        return TransactionDto.FromTransaction(transaction);
    }
}
