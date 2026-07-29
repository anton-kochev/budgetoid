using Application.Abstractions;
using Application.Currencies;
using Domain.Accounts;
using Domain.Categories;
using Domain.Common;
using Domain.Payees;
using Domain.Transactions;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Transactions.UpdateTransaction;

public sealed class UpdateTransactionHandler(
    ITransactionRepository repository,
    IAccountRepository accounts,
    ICurrencyReadService currencies,
    IPayeeRepository payees,
    ICategoryRepository categories,
    ITransactionalExecutor transactionalExecutor)
    : ICommandHandler<UpdateTransactionCommand>
{
    public async Task HandleAsync(
        UpdateTransactionCommand command,
        CancellationToken cancellationToken = default)
    {
        // The lookup runs through the budget query filter, so a transaction belonging to another
        // budget is indistinguishable from one that never existed — both end here as a 404.
        Transaction? transaction = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (transaction is null)
        {
            throw new NotFoundException("Transaction was not found.");
        }

        // Resolve the account the transaction will end up on, which is the one the caller named or
        // the one it already has. Reading it through the repository is the whole cross-budget check:
        // the query filter makes another budget's account read as null, so it lands on the same
        // "account was not found" 400 as an id that matches no row anywhere.
        Guid accountId = command.AccountId.OrElse(transaction.AccountId);
        Account? account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.AccountId)] = ["Account was not found."],
            });
        }

        // The account decides the currency, and the currency decides how many decimal places the
        // amount may carry — so this has to follow the account resolution, not precede it.
        CurrencyDto currency = await currencies.GetByCodeAsync(account.CurrencyCode, cancellationToken)
                               ?? throw new InvalidOperationException(
                                   $"Currency '{account.CurrencyCode}' for account '{account.Id}' was not found.");

        Category? category = null;
        if (command.CategoryId is { IsSet: true, Value: { } categoryId })
        {
            // Same filtered-read reasoning as the account: another budget's category reads as null.
            category = await categories.GetByIdAsync(categoryId, cancellationToken);
            if (category is null)
            {
                throw new DomainValidationException(new Dictionary<string, string[]>
                {
                    [nameof(command.CategoryId)] = ["Category was not found."],
                });
            }
        }

        // Every read and every validation is above this line, mirroring CreateTransactionHandler:
        // nothing should mutate the transaction, and nothing should commit a payee, for an edit that
        // then turns out to be invalid.
        transaction.Update(
            accountId,
            command.Amount.OrElse(transaction.Amount),
            currency.MinorUnit,
            command.Date.OrElse(transaction.Date),
            command.Description.OrElse(transaction.Description));

        if (command.CategoryId.IsSet)
        {
            if (category is null)
            {
                transaction.ClearCategory();
            }
            else
            {
                transaction.AssignCategory(category.Id);
            }
        }

        // The transaction boundary starts here for the same reason it does in
        // CreateTransactionHandler: below it are the two writes — the payee and the transaction that
        // needed it — and a payee committed without the edit that named it is permanent litter,
        // since nothing deletes payees.
        await transactionalExecutor.ExecuteAsync(WriteAsync, cancellationToken);

        async Task WriteAsync(CancellationToken token)
        {
            if (command.PayeeName.IsSet)
            {
                // Blank is treated as no payee exactly as it is on create, so " " and null mean the
                // same thing rather than minting a whitespace-named payee.
                string? payeeName = command.PayeeName.Value;
                if (string.IsNullOrWhiteSpace(payeeName))
                {
                    transaction.ClearPayee();
                }
                else
                {
                    Payee payee = await payees.GetOrCreateAsync(payeeName, token);
                    transaction.AssignPayee(payee.Id);
                }
            }

            await repository.UpdateAsync(transaction, token);
        }
    }
}
