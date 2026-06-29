using Application.Abstractions;
using Application.Accounts;
using Application.Currencies;
using Application.Payees;
using Domain.Accounts;
using Domain.Payees;
using Domain.Transactions;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Transactions.CreateTransaction;

public sealed class CreateTransactionHandler(
    ITransactionRepository repository,
    IAccountRepository accounts,
    ICurrencyReadService currencies,
    IPayeeRepository payees,
    IUserContext userContext,
    TimeProvider timeProvider)
    : ICommandHandler<CreateTransactionCommand, TransactionDto>
{
    public async Task<TransactionDto> HandleAsync(
        CreateTransactionCommand command,
        CancellationToken cancellationToken = default)
    {
        Account? account = await accounts.GetByIdAsync(command.AccountId, cancellationToken);
        if (account is null)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.AccountId)] = ["Account was not found."]
            });
        }

        // Derive the symbol from the same seeded currencies table the read services join against,
        // so the create-response and the list-view never disagree. The accounts -> currencies FK
        // (Restrict) guarantees this row exists; a null here means the schema invariant is broken,
        // so fail loudly rather than silently falling back to a guessed symbol.
        CurrencyDto currency = await currencies.GetByCodeAsync(account.CurrencyCode, cancellationToken)
                               ?? throw new InvalidOperationException(
                                   $"Currency '{account.CurrencyCode}' for account '{account.Id}' was not found.");

        Transaction transaction = Transaction.Create(
            userContext.UserId,
            command.AccountId,
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

        return TransactionDto.FromTransaction(transaction, account.Name, account.CurrencyCode, currency.Symbol,
            payee?.Name);
    }
}
