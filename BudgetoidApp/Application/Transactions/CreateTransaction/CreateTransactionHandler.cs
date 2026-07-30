using Application.Abstractions;
using Application.Currencies;
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

            return TransactionDto.FromTransaction(
                transaction,
                account.Name,
                account.CurrencyCode,
                currency.Symbol,
                payee?.Name,
                category?.Name,
                categoryGroup?.Id,
                categoryGroup?.Name);
        }
    }
}
