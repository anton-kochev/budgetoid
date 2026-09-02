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

        Payee? payee = null;
        if (command.PayeeId is { } payeeId)
        {
            // Reading it through the repository is the whole cross-budget check, the same reasoning the
            // account and the category carry: the BudgetIsolation query filter makes another budget's
            // payee read as null, so it lands on the same 400 as an id matching no row anywhere. Without
            // this read the id would reach the database as a foreign key nothing satisfies, and a 23503
            // is a 500 — a defect report for what is a bad request.
            payee = await payees.GetByIdAsync(payeeId, cancellationToken);
            if (payee is null)
            {
                throw new DomainValidationException(new Dictionary<string, string[]>
                {
                    [nameof(command.PayeeId)] = ["Payee was not found."],
                });
            }

            transaction.AssignPayee(payee.Id);
        }

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

        // NO TRANSACTION BOUNDARY, BECAUSE THERE IS ONLY ONE WRITE LEFT. This method used to commit two
        // rows — a payee it created from a name, and the transaction that needed it — and wrapped them so
        // that a failure between the two committed neither. The payee write is gone: the server can no
        // longer resolve a name to a row, so a payee is created by a request of its own before this one,
        // and everything above this line is reads and domain validation. An ITransactionalExecutor around
        // a single SaveChanges commits exactly what the save commits and reads to the next author as
        // though something here needed atomicity.
        //
        // What that boundary used to prevent is now reachable from the other side, and it is accepted. A
        // successful POST /api/payees followed by a failing POST /api/transactions leaves a payee row no
        // transaction names, on a table with no DELETE grant, so nothing in the application can remove
        // it. Every alternative is worse: keeping both writes in one server transaction needs the server
        // to create the payee, which needs it to look a name up; an inline payee on this body would give
        // payees a second creating path and destroy the one property that change bought; and a
        // compensating delete needs a grant app-role-grants.sql withholds and argues against. The blast
        // radius is one extra row in an autocomplete list.
        await repository.AddAsync(transaction, cancellationToken);

        // THIS BROKE BECAUSE IT WAS ALWAYS A READ OF THE ACCOUNT'S NAME, AND THE FIX IS NOT TO OPEN
        // IT. accounts.name is an AEAD envelope this server holds no key for; what the row carries is
        // handed on untouched, in the alphabet every binary member of this API crosses JSON in, and
        // the browser that asked for it is what turns it back into a name. Decoding here would need a
        // key on this side, which is the design the product exists to avoid — and a placeholder
        // string would be a lie the screen renders. payees.name and category_groups.name are the second
        // and third such columns, and both cross the same way.
        //
        // category.Name is the one name here that is still text: categories.name is not sealed yet, and
        // it is the next column to be. Do not fold the four into one rule in either direction.
        return TransactionDto.FromTransaction(
            transaction,
            PasskeyEncoding.Encode(account.Name.Envelope.Span),
            account.CurrencyCode,
            currency.Symbol,
            payee is null ? null : PasskeyEncoding.Encode(payee.Name.Envelope.Span),
            category?.Name,
            categoryGroup?.Id,
            categoryGroup is null ? null : PasskeyEncoding.Encode(categoryGroup.Name.Envelope.Span));
    }
}
