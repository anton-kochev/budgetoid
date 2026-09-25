using Application.Abstractions;
using Application.Currencies;
using Application.Security;
using Domain.Accounts;
using Domain.Common;
using Domain.Security;
// Domain.Common.ValidationException by name, because Application.Abstractions declares one too and only
// the Domain's is what ValidationExceptionHandler turns into a 400 carrying the field errors.
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Accounts.UpdateAccount;

public sealed class UpdateAccountHandler(IAccountRepository repository, ICurrencyReadService currencies)
    : ICommandHandler<UpdateAccountCommand>
{
    public async Task HandleAsync(
        UpdateAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        Account? account = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (account is null)
        {
            throw new NotFoundException("Account was not found.");
        }

        // Resolved after the load because the currency to validate against is the account's own -
        // Update never changes it. The RESTRICT foreign key on accounts.currency_code guarantees the
        // row exists, so a miss is corruption rather than user error.
        CurrencyDto currency = await currencies.GetByCodeAsync(account.CurrencyCode, cancellationToken)
                               ?? throw new InvalidOperationException(
                                   $"Currency '{account.CurrencyCode}' for account '{account.Id}' was not found.");

        // The two members this server cannot read, judged for shape and nothing else. No identifier among
        // them: the route parameter stays {id:guid} and needs no canonical check, because a client
        // re-sealing a rename binds the row's EXISTING id — read back from this API in the one form Guid
        // renders — and never the text it happened to put in the URL. CreateAccountCommand's remarks carry
        // the whole argument.
        //
        // EVERY MEMBER IS ATTEMPTED AND EVERY FAILURE IS REPORTED — the idiom Account.ValidateOrThrow
        // already uses one ring in, and the reason CreateAccountHandler gives for its own three: the pair
        // is produced by one piece of client code, so a caller that got both wrong would otherwise learn
        // about the second only after fixing the first and sending everything again.
        var errors = new Dictionary<string, string[]>();

        // ONE SENTENCE PER MEMBER, STATING THE WHOLE REQUIREMENT. The cap and the version are read off the
        // types that own them and never written out here; IndexedName.Of names the same cap for itself, so
        // a ceiling mistyped on this line is refused there as a defect in this codebase rather than stored.
        if (!CiphertextEnvelopeText.TryDecode(
                command.Name, NarrativeFieldLimits.NameBytes, out byte[]? envelope))
        {
            errors[nameof(command.Name)] =
            [
                "The account name must be base64url text decoding to a sealed envelope of at most "
                + $"{NarrativeFieldLimits.NameBytes} bytes carrying envelope version "
                + $"{CiphertextEnvelope.Version}.",
            ];
        }

        // Its own type rather than the envelope's, which is the near miss: a blind index is a keyed digest
        // with no version byte to satisfy the framing rules with. The width is the only shape check this
        // side can make — recomputing the index needs the account's index key, which lives in a browser.
        if (!BlindIndexText.TryDecode(command.NameKey, out byte[]? blindIndex))
        {
            errors[nameof(command.NameKey)] =
            [
                "The account name index must be base64url text decoding to exactly "
                + $"{IndexedName.BlindIndexLength} bytes.",
            ];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        // Suppressed rather than re-checked, for the reason CreateAccountHandler gives: an empty dictionary
        // IS the statement that both decodes succeeded, and a second null test would be a rule with two
        // owners.
        //
        // One IndexedName and never two arguments: Update has no spelling for half a name, which is what
        // stops a rename writing new ciphertext under the previous name's index — a row where every
        // constraint is satisfied, the uniqueness rule polices a name the row no longer holds, and nothing
        // on this side can compute either half to notice.
        account.Update(
            IndexedName.Of(envelope!, blindIndex!),
            command.Type,
            command.OpeningBalance,
            currency.MinorUnit);

        await repository.UpdateAsync(account, cancellationToken);
    }
}
