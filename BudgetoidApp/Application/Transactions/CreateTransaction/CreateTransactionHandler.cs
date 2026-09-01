using Application.Abstractions;
using Application.Currencies;
using Application.Passkeys;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Transactions;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Transactions.CreateTransaction;

public sealed class CreateTransactionHandler(
    ITransactionRepository repository,
    IAccountRepository accounts,
    ICurrencyReadService currencies,
    IPayeeRepository payees,
    ICategoryRepository categories,
    ICategoryGroupRepository categoryGroups,
    IBudgetContext budgetContext,
    TimeProvider timeProvider,
    ITransactionalExecutor transactionalExecutor)
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
                [nameof(command.AccountId)] = ["Account was not found."],
            });
        }

        CurrencyDto currency = await currencies.GetByCodeAsync(account.CurrencyCode, cancellationToken)
                               ?? throw new InvalidOperationException(
                                   $"Currency '{account.CurrencyCode}' for account '{account.Id}' was not found.");

        Transaction transaction = Transaction.Create(
            budgetContext.BudgetId,
            command.AccountId,
            command.Amount,
            currency.MinorUnit,
            command.Date,
            command.Description,
            timeProvider.GetUtcNow().UtcDateTime);

        Category? category = null;
        CategoryGroup? categoryGroup = null;
        if (command.CategoryId is { } categoryId)
        {
            category = await categories.GetByIdAsync(categoryId, cancellationToken);
            if (category is null)
            {
                throw new DomainValidationException(new Dictionary<string, string[]>
                {
                    [nameof(command.CategoryId)] = ["Category was not found."],
                });
            }

            categoryGroup = await categoryGroups.GetByIdAsync(category.CategoryGroupId, cancellationToken)
                            ?? throw new InvalidOperationException(
                                $"Category group '{category.CategoryGroupId}' for category '{category.Id}' was not found.");
            transaction.AssignCategory(category.Id);
        }

        // The transaction boundary starts here rather than at the top of the method. Everything
        // above is reads and domain validation, which commit nothing and would only widen the window
        // the transaction holds its connection and locks for. Everything below is the two writes —
        // the payee and the transaction that needed it — and they are one logical operation: a
        // payee committed without its transaction is permanent litter, since nothing deletes payees.
        return await transactionalExecutor.ExecuteAsync(WriteAsync, cancellationToken);

        async Task<TransactionDto> WriteAsync(CancellationToken token)
        {
            Payee? payee = null;
            if (!string.IsNullOrWhiteSpace(command.PayeeName))
            {
                payee = await payees.GetOrCreateAsync(command.PayeeName, token);
                transaction.AssignPayee(payee.Id);
            }

            await repository.AddAsync(transaction, token);

            // THIS BROKE BECAUSE IT WAS ALWAYS A READ OF THE ACCOUNT'S NAME, AND THE FIX IS NOT TO OPEN
            // IT. accounts.name is an AEAD envelope this server holds no key for; what the row carries is
            // handed on untouched, in the alphabet every binary member of this API crosses JSON in, and
            // the browser that asked for it is what turns it back into a name. Decoding here would need a
            // key on this side, which is the design the product exists to avoid — and a placeholder
            // string would be a lie the screen renders.
            return TransactionDto.FromTransaction(
                transaction,
                PasskeyEncoding.Encode(account.Name.Envelope.Span),
                account.CurrencyCode,
                currency.Symbol,
                payee?.Name,
                category?.Name,
                categoryGroup?.Id,
                categoryGroup?.Name);
        }
    }
}
