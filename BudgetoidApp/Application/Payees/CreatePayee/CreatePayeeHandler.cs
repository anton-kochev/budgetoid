using Application.Abstractions;
using Application.Security;
using Domain.Payees;
using Domain.Security;
// Domain.Common.ValidationException by name, because Application.Abstractions declares one too and only
// the Domain's is what ValidationExceptionHandler turns into a 400 carrying the field errors.
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Payees.CreatePayee;

/// <summary>
/// Writes the payee row a client decided is new, having resolved the counterparty against a list only it
/// can read.
/// </summary>
/// <remarks>
/// <b>This handler is the whole of what replaced find-or-create, and it does not look anything up.</b>
/// The server holds no index key and cannot fold a name's case, so it cannot ask whether this
/// counterparty is already on the list; the client, which holds both, asks that question before it posts.
/// What remains on this side is a shape check, an insert, and
/// <c>IX_payees_budget_id_name_key</c> — see <see cref="IPayeeRepository"/> for why no member here may
/// grow to take a name.
/// </remarks>
public sealed class CreatePayeeHandler(
    IPayeeRepository repository,
    IBudgetContext budgetContext,
    TimeProvider timeProvider)
    : ICommandHandler<CreatePayeeCommand, PayeeDto>
{
    public async Task<PayeeDto> HandleAsync(
        CreatePayeeCommand command,
        CancellationToken cancellationToken = default)
    {
        // The three members this server cannot read, judged for shape and nothing else, above the entity
        // because base64url and the spelling of a uuid are wire concerns: by the time bytes reach
        // IndexedName.Of they are a Guid and two buffers, and that factory throws ArgumentException rather
        // than the exception that becomes a 400.
        //
        // EVERY MEMBER IS ATTEMPTED AND EVERY FAILURE IS REPORTED — the idiom Payee.ValidateOrThrow keeps
        // one ring in, and CreateAccountHandler keeps for the same three members. They arrive together,
        // are produced by one piece of client code and are opaque to this side in the same way, so a
        // client that got two of them wrong would otherwise learn about the second only after fixing the
        // first and sending everything again.
        var errors = new Dictionary<string, string[]>();

        // The identifier first, because it is what the name was sealed against: a client that sent a
        // spelling this API cannot reproduce has a name nothing will ever open, whatever the envelope
        // beside it looks like. The rule is CanonicalIdentifier's and is shared with every other
        // client-minted id this API takes; only the sentence is this route's, and it names the member a
        // caller can correct.
        if (!CanonicalIdentifier.TryParse(command.Id, out Guid id))
        {
            errors[nameof(command.Id)] =
            [
                "The payee id must be a uuid in the lower-case 36-character hyphenated form with no "
                + "surrounding whitespace, and not the all-zero uuid.",
            ];
        }

        // ONE SENTENCE PER MEMBER, STATING THE WHOLE REQUIREMENT, the shape every decode-at-the-edge in
        // this codebase keeps: splitting "not base64url" from "too long" from "unknown version" would tell
        // a caller which half of an opaque value it got wrong. The cap and the version are read off the
        // types that own them and never written out here, so a message cannot go on being confident after
        // the real bound has moved.
        //
        // NameBytes is named here and named again by IndexedName.Of, which picks its own cap. That is not
        // a duplicate to fold away: this one turns an over-long envelope into a 400 the caller can act on,
        // and the domain's is what refuses a ceiling mistyped on this line — loudly, as a defect in this
        // codebase rather than in the request.
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

        // Judged by its own type and deliberately not by the one above, which is the near miss: a blind
        // index is a keyed digest with no version byte, so the envelope's framing rules would refuse
        // roughly 255 values in 256 as malformed. What the two share is the alphabet, and BlindIndexText
        // is that shared decoder plus the one shape check this side can make — the width. Nothing here can
        // say the index is the index OF the name beside it; that needs the account's index key, which
        // lives in a browser. On this table the width is therefore the whole of the defence: a wrong 32
        // bytes keys perfectly, never collides, and matches no payee the client will ever look for.
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

        // Suppressed rather than re-checked: an empty dictionary is exactly the statement that both
        // decodes above succeeded, and a second null test here would be a rule with two owners — the one
        // a later reader edited would decide what happens, and the other would go on looking correct.
        //
        // The budget and the instant come from here and not from the repository, which lost both when it
        // lost find-or-create: that method minted rows on the server's own initiative and needed them.
        // A create the caller asked for is an ordinary use case, and its ambient budget and clock belong
        // in the ring that owns use cases.
        Payee payee = Payee.Create(
            id,
            budgetContext.BudgetId,
            IndexedName.Of(envelope!, blindIndex!),
            timeProvider.GetUtcNow().UtcDateTime);

        await repository.AddAsync(payee, cancellationToken);
        return PayeeDto.FromPayee(payee);
    }
}
