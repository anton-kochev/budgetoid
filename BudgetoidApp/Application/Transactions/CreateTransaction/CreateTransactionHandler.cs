using Application.Abstractions;
using Application.Currencies;
using Application.Passkeys;
using Application.Security;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Security;
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
        // The two members this server cannot read, judged for shape and nothing else, ABOVE every lookup
        // because base64url and the spelling of a uuid are wire concerns: by the time bytes reach
        // NarrativeField.SealedOrAbsent they are a buffer, and that factory throws ArgumentException
        // rather than the exception that becomes a 400.
        //
        // BOTH ARE ATTEMPTED AND BOTH FAILURES ARE REPORTED, the idiom every decode-at-the-edge in this
        // codebase keeps: they are produced by one piece of client code, so a caller that got both wrong
        // would otherwise learn about the second only after fixing the first and sending everything
        // again. The description is the one at risk of losing that, because it alone sits behind a
        // branch.
        //
        // The account, payee and category resolutions stay BELOW this block. They cost round trips and
        // report about rows; a malformed body has nothing to do with a row and should not wait on one.
        var errors = new Dictionary<string, string[]>();

        // The identifier first, because it is what the note was sealed against: a client that sent a
        // spelling this API cannot reproduce has a note nothing will ever open, whatever the envelope
        // beside it looks like. The rule is CanonicalIdentifier's and is shared with every other
        // client-minted id this API takes; only the sentence is this route's.
        if (!CanonicalIdentifier.TryParse(command.Id, out Guid id))
        {
            errors[nameof(command.Id)] =
            [
                "The transaction id must be a uuid in the lower-case 36-character hyphenated form with "
                + "no surrounding whitespace, and not the all-zero uuid.",
            ];
        }

        // THE ABSENCE TEST IS `is null` AND THE SPELLING IS THE WHOLE OF A RULE. Never
        // string.IsNullOrEmpty and never string.IsNullOrWhiteSpace: PasskeyEncoding.TryDecode, which
        // CiphertextEnvelopeText delegates to, refuses null and "" identically, so the decoder underneath
        // cannot make this distinction and it has to live here. Either forgiving spelling folds a
        // malformed "" into "absent" and stores NULL where a 400 was owed - a legal row, a 201 on the
        // wire, and a note the person typed silently gone.
        //
        // DescriptionBytes and NOT NameBytes: the two are field CLASSES with different caps, and this
        // line shares the ONLY ownership of that number on this path with the SealedOrAbsent call below.
        // Widen it and an over-cap note travels the whole ring to land on
        // CK_transactions_description_length, reaching the caller as a 23514 nothing translates.
        //
        // Carried as ReadOnlyMemory<byte>? and never as byte[]?: a null array converts to a NON-NULL,
        // zero-length ReadOnlyMemory<byte>?, which SealedOrAbsent judges as a supplied value and refuses
        // with an ArgumentException a caller cannot act on.
        ReadOnlyMemory<byte>? descriptionEnvelope = null;
        if (command.Description is not null)
        {
            if (CiphertextEnvelopeText.TryDecode(
                    command.Description,
                    NarrativeFieldLimits.DescriptionBytes,
                    out byte[]? decodedDescription))
            {
                descriptionEnvelope = new ReadOnlyMemory<byte>(decodedDescription);
            }
            else
            {
                errors[nameof(command.Description)] =
                [
                    "The transaction description must be base64url text decoding to a sealed envelope of "
                    + $"at most {NarrativeFieldLimits.DescriptionBytes} bytes carrying envelope version "
                    + $"{CiphertextEnvelope.Version}.",
                ];
            }
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

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

        // The id is the caller's, suppressed rather than re-checked: an empty errors dictionary IS the
        // statement that CanonicalIdentifier.TryParse succeeded, and a second test here would be a rule
        // with two owners.
        //
        // The description's null is NOT suppressed, because here it is a value rather than a failure: a
        // transaction with no note reaches SealedOrAbsent as null and comes back null. That member is the
        // one door for it - Sealed would judge default(ReadOnlyMemory<byte>) as an envelope and refuse a
        // row that is legal.
        Transaction transaction = Transaction.Create(
            id,
            budgetContext.BudgetId,
            command.AccountId,
            command.Amount,
            currency.MinorUnit,
            command.Date,
            NarrativeField.SealedOrAbsent(descriptionEnvelope, NarrativeFieldLimits.DescriptionBytes),
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

        // FIVE NARRATIVE VALUES CROSS THIS RETURN AND ONE RULE COVERS EVERY ONE OF THEM. accounts.name,
        // payees.name, categories.name and category_groups.name are AEAD envelopes this server holds no
        // key for, and so is the transaction's own note, which FromTransaction encodes off the entity;
        // what each row carries is handed on untouched, in the alphabet every binary member of this API
        // crosses JSON in, and the browser that asked for it is what turns it back into text. Decoding
        // any of them here would need a key on this side, which is the design the product exists to
        // avoid — and a placeholder string would be a lie the screen renders.
        //
        // This block used to say that category.Name was the one name still text and that a reader must
        // not fold the four into one rule. It is folded now; there is nothing left to keep apart.
        return TransactionDto.FromTransaction(
            transaction,
            PasskeyEncoding.Encode(account.Name.Envelope.Span),
            account.CurrencyCode,
            currency.Symbol,
            payee is null ? null : PasskeyEncoding.Encode(payee.Name.Envelope.Span),
            category is null ? null : PasskeyEncoding.Encode(category.Name.Envelope.Span),
            categoryGroup?.Id,
            categoryGroup is null ? null : PasskeyEncoding.Encode(categoryGroup.Name.Envelope.Span));
    }
}
