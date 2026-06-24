using Application.Abstractions;
using Application.Payees;
using Domain.Payees;
using Domain.Transactions;

namespace Application.Transactions.CreateTransaction;

public sealed class CreateTransactionHandler(
    ITransactionRepository repository,
    IPayeeRepository payees,
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

        Payee? payee = null;
        if (!string.IsNullOrWhiteSpace(command.PayeeName))
        {
            payee = await payees.GetOrCreateAsync(command.PayeeName, cancellationToken);
            transaction.AssignPayee(payee.Id);
        }

        await repository.AddAsync(transaction, cancellationToken);

        return TransactionDto.FromTransaction(transaction, payee?.Name);
    }
}
