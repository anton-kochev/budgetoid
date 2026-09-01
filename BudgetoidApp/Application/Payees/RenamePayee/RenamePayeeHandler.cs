using Application.Abstractions;
using Application.Security;
using Domain.Common;
using Domain.Payees;
using Domain.Security;
// Domain.Common.ValidationException by name, because Application.Abstractions declares one too and only
// the Domain's is what ValidationExceptionHandler turns into a 400 carrying the field errors.
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Payees.RenamePayee;

public sealed class RenamePayeeHandler(IPayeeRepository repository)
    : ICommandHandler<RenamePayeeCommand>
{
    public async Task HandleAsync(
        RenamePayeeCommand command,
        CancellationToken cancellationToken = default)
    {
        // The lookup runs through the budget query filter, so a payee belonging to another budget is
        // indistinguishable from one that never existed — both end here as a 404.
        Payee? payee = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (payee is null)
        {
            throw new NotFoundException("Payee was not found.");
        }

        // The two members this server cannot read, judged for shape and nothing else. No identifier among
        // them: the route parameter stays {id:guid} and needs no canonical check, because a client
        // re-sealing a rename binds the row's EXISTING id — read back from this API in the one form Guid
        // renders — and never the text it happened to put in the URL. RenamePayeeCommand's remarks carry
        // the argument.
        //
        // EVERY MEMBER IS ATTEMPTED AND EVERY FAILURE IS REPORTED, for the reason CreatePayeeHandler gives
        // for its own three: the pair is produced by one piece of client code, so a caller that got both
        // wrong would otherwise learn about the second only after fixing the first and sending everything
        // again.
        var errors = new Dictionary<string, string[]>();

        // ONE SENTENCE PER MEMBER, STATING THE WHOLE REQUIREMENT. The cap and the version are read off the
        // types that own them and never written out here; IndexedName.Of names the same cap for itself, so
        // a ceiling mistyped on this line is refused there as a defect in this codebase rather than stored.
        if (!CiphertextEnvelopeText.TryDecode(
                command.Name, NarrativeFieldLimits.NameBytes, out byte[]? envelope))
        {
            errors[nameof(command.Name)] =
            [
                "The payee name must be base64url text decoding to a sealed envelope of at most "
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
                "The payee name index must be base64url text decoding to exactly "
                + $"{IndexedName.BlindIndexLength} bytes.",
            ];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        // Suppressed rather than re-checked, for the reason CreatePayeeHandler gives: an empty dictionary
        // IS the statement that both decodes succeeded, and a second null test would be a rule with two
        // owners.
        //
        // One IndexedName and never two arguments: Rename has no spelling for half a name. On this table
        // that matters more than anywhere else — half a rename leaves a payee the client can neither find
        // nor re-create, and the budget gains a second row for one counterparty the next time somebody
        // names it. Payee.Rename spells both failures out.
        payee.Rename(IndexedName.Of(envelope!, blindIndex!));

        // A collision on the blind index comes back from here as a 400 keyed on Name, and the same
        // collision on a create comes back as a 409. PayeeRepository argues the split at both members;
        // it is a decision about the remedy, not an inconsistency to harmonise away.
        await repository.UpdateAsync(payee, cancellationToken);
    }
}
