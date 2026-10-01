using Application.Abstractions;
using Application.Currencies;
using Application.Security;
using Domain.Accounts;
using Domain.Categories;
using Domain.Common;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Transactions.UpdateTransaction;

public sealed class UpdateTransactionHandler(
    ITransactionRepository repository,
    IAccountRepository accounts,
    ICurrencyReadService currencies,
    IPayeeRepository payees,
    ICategoryRepository categories)
    : ICommandHandler<UpdateTransactionCommand>
{
    public async Task HandleAsync(
        UpdateTransactionCommand command,
        CancellationToken cancellationToken = default)
    {
        // THE DESCRIPTION IS DECODED ABOVE EVERY MUTATION AND ABOVE THE LOOKUP, for the reason the
        // account, payee and category resolutions are read above theirs: the entity the repository hands
        // back is the TRACKED instance in production, so a handler that mutated it and only then threw
        // would leave a cleared or rewritten note for the next SaveChanges on that context to commit.
        //
        // FOUR STATES REACH THIS MEMBER AND ALL FOUR ARE DISTINCT ON THE WIRE. Measured against the
        // product's own Optional<T> converter under JsonSerializerDefaults.Web with the options
        // Api/Program.cs registers:
        //   member absent                  -> IsSet=false            -> leave the note alone
        //   "description": null            -> IsSet=true, Value=null -> clear the note
        //   "description": "<base64url>"   -> IsSet=true, Value=text  -> replace the note
        //   "description": ""              -> IsSet=true, Value=""    -> 400 keyed on Description
        //
        // IsSet IS THE OUTER TEST AND `Value is null` THE INNER ONE, IN THAT ORDER. Reversed - branching
        // on `Value is { } text` first - present-and-null falls through with the absent case, so the one
        // request that clears a note does nothing and answers 204. It is the same trap the PayeeId and
        // CategoryId blocks below document, and the description joins it; what makes this one worse is
        // that the payee's silent no-op leaves a visible counterparty attached, while a note that failed
        // to clear looks exactly like a note that was never touched.
        //
        // "" is refused rather than folded, and `is null` is the whole of the spelling: never
        // string.IsNullOrEmpty and never string.IsNullOrWhiteSpace. The decoder underneath refuses null
        // and "" identically, so the distinction cannot live down there, and a forgiving spelling here
        // reads "" as "clear it" - which is a fourth meaning nobody sent.
        Optional<NarrativeField?> description = default;
        if (command.Description.IsSet)
        {
            if (command.Description.Value is null)
            {
                description = new Optional<NarrativeField?>(null);
            }
            else if (CiphertextEnvelopeText.TryDecode(
                         command.Description.Value,
                         NarrativeFieldLimits.DescriptionBytes,
                         out byte[]? decodedDescription))
            {
                description = new Optional<NarrativeField?>(
                    NarrativeField.Sealed(
                        decodedDescription, NarrativeFieldLimits.DescriptionBytes));
            }
            else
            {
                throw new DomainValidationException(new Dictionary<string, string[]>
                {
                    [nameof(command.Description)] =
                    [
                        "The transaction description must be base64url text decoding to a sealed "
                        + $"envelope of at most {NarrativeFieldLimits.DescriptionBytes} bytes carrying "
                        + $"envelope version {CiphertextEnvelope.Version}.",
                    ],
                });
            }
        }

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

        // Resolved here beside the category and above every mutation, because a payee this budget does
        // not hold must refuse the edit before the edit touches the entity. Same filtered-read
        // reasoning as the account: another budget's payee reads as null and lands on the same 400 as
        // an id matching no row anywhere, rather than reaching the database as a foreign key nothing
        // satisfies — a 23503, which surfaces as a 500 for what is a bad request.
        //
        // THE READ IS FOR THE FILTER, NOT FOR THE ROW. Nothing below needs a Payee — AssignPayee is
        // handed an identifier this method already held — so the entity is not what this call is for;
        // running the BudgetIsolation query filter is. That makes the whole cross-budget check one
        // round trip that looks discardable to anybody reading for waste, and discarding it does not
        // remove a check, it moves it: the foreign key catches the same request one layer down as a
        // 23503, which leaves as a 500. A person naming a payee they do not own would be told the
        // server broke. Same for the category block below.
        Payee? payee = null;
        if (command.PayeeId is { IsSet: true, Value: { } payeeId })
        {
            payee = await payees.GetByIdAsync(payeeId, cancellationToken);
            if (payee is null)
            {
                throw new DomainValidationException(new Dictionary<string, string[]>
                {
                    [nameof(command.PayeeId)] = ["Payee was not found."],
                });
            }
        }

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
        // nothing should mutate the transaction for an edit that then turns out to be invalid.
        transaction.Update(
            accountId,
            command.Amount.OrElse(transaction.Amount),
            currency.MinorUnit,
            command.Date.OrElse(transaction.Date),

            // OrElse over the DECODED optional and never over command.Description, which holds text this
            // entity has no member for. Absent means the note the row already carries survives; that is
            // the one state the entity itself cannot express, because Update is a full assignment.
            description.OrElse(transaction.Description));

        // IsSet IS THE OUTER TEST AND Value THE INNER ONE, AND THE ORDER IS THE THREE-STATE CONTRACT.
        // Reached the other way round — branching on `Value is { } id` first — present-and-null falls
        // through with the absent case and the payee is silently left attached, so the one request that
        // asks for a payee to be detached does nothing and answers 204. The read above already refused
        // an identifier this budget does not hold, so a null `payee` here can only mean the caller sent
        // an explicit null. The category block below is the same shape for the same reason.
        if (command.PayeeId.IsSet)
        {
            if (payee is null)
            {
                transaction.ClearPayee();
            }
            else
            {
                transaction.AssignPayee(payee.Id);
            }
        }

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

        // NO TRANSACTION BOUNDARY, BECAUSE THERE IS ONLY ONE WRITE LEFT. This method used to commit a
        // payee it created from a name alongside the edit that named it, and wrapped the pair so that a
        // failure between them committed neither. The payee write is gone — the server can no longer
        // resolve a name to a row, so a payee is created by a request of its own — and an
        // ITransactionalExecutor around a single SaveChanges commits exactly what the save commits while
        // reading to the next author as though something here needed atomicity.
        //
        // The orphan that boundary prevented is now reachable from the other side and is accepted;
        // CreateTransactionHandler carries the argument in full.
        await repository.UpdateAsync(transaction, cancellationToken);
    }
}
