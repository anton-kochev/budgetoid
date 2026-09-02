using Application.Abstractions;
using Application.Security;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Security;
// Domain.Common.ValidationException by name, because Application.Abstractions declares one too and only
// the Domain's is what ValidationExceptionHandler turns into a 400 carrying the field errors.
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.CategoryGroups.UpdateCategoryGroup;

public sealed class UpdateCategoryGroupHandler(ICategoryGroupRepository repository)
    : ICommandHandler<UpdateCategoryGroupCommand>
{
    public async Task HandleAsync(
        UpdateCategoryGroupCommand command,
        CancellationToken cancellationToken = default)
    {
        // THE DECODE BLOCK SITS ABOVE THE LOOKUP, AND RenamePayeeHandler DOES THE OPPOSITE. That
        // neighbour looks its row up first and judges the body second; this one judges first. The order
        // is a real choice and neither leaks existence - a caller with a malformed body learns nothing
        // about the row either way, because it is refused before anything is read. What decides it is
        // that this is a shape check on the request needing nothing from the database, and that the
        // answer a caller can act on is the one naming the member they sent: run below the lookup, a
        // malformed body against an unknown id answers 404, which tells the caller to go looking for a
        // row when the fault is in their own request. Run above, they are told which member to fix.
        //
        // The second reason is this ring's own: the entity the repository hands back is the TRACKED
        // instance in production, so a handler that mutated it and only then threw would leave a dirty
        // entity for the next SaveChanges on that context to commit - a rename and a cleared description
        // nobody asked for, arriving with some later request. Judging first means there is nothing to
        // leave behind.
        //
        // EVERY MEMBER IS ATTEMPTED AND EVERY FAILURE IS REPORTED, for the reason
        // CreateCategoryGroupHandler gives for its own four: the three are produced by one piece of
        // client code, so a caller that got two of them wrong would otherwise learn about the second only
        // after fixing the first and sending everything again. No identifier among them - the route
        // parameter stays {id:guid} and needs no canonical check, because a client re-sealing an update
        // binds the row's EXISTING id. UpdateCategoryGroupCommand's remarks carry that argument.
        var errors = new Dictionary<string, string[]>();

        // ONE SENTENCE PER MEMBER, STATING THE WHOLE REQUIREMENT. The cap and the version are read off
        // the types that own them and never written out here; IndexedName.Of names the same cap for
        // itself, so a ceiling mistyped on this line is refused there as a defect in this codebase rather
        // than stored.
        if (!CiphertextEnvelopeText.TryDecode(
                command.Name, NarrativeFieldLimits.NameBytes, out byte[]? envelope))
        {
            errors[nameof(command.Name)] =
            [
                "The category group name must be base64url text decoding to a sealed envelope of at most "
                + $"{NarrativeFieldLimits.NameBytes} bytes carrying envelope version "
                + $"{CiphertextEnvelope.Version}.",
            ];
        }

        // Its own type rather than the envelope's, which is the near miss: a blind index is a keyed
        // digest with no version byte to satisfy the framing rules with. The width is the only shape
        // check this side can make - recomputing the index needs the account's index key, which lives in
        // a browser.
        if (!BlindIndexText.TryDecode(command.NameKey, out byte[]? blindIndex))
        {
            errors[nameof(command.NameKey)] =
            [
                "The category group name index must be base64url text decoding to exactly "
                + $"{IndexedName.BlindIndexLength} bytes.",
            ];
        }

        // THE ABSENCE TEST IS `is null` AND THE SPELLING IS THE WHOLE OF A RULE. Never
        // string.IsNullOrEmpty and never string.IsNullOrWhiteSpace: the decoder underneath refuses null
        // and "" identically, so the distinction cannot live down there. Under a forgiving spelling this
        // route answers 204 and CLEARS a note the caller never asked to remove; under `is null` a "" is a
        // malformed member and a 400 the caller can act on. The two readings differ by an entire column
        // of somebody's data.
        //
        // DescriptionBytes and NOT NameBytes, and this line shares the ONLY ownership of that number on
        // this path with the SealedOrAbsent call below. The name's cap is stated twice - here and inside
        // IndexedName.Of - so a mistyped name ceiling is refused by the domain. The description's is not,
        // so widening it lets an over-cap value travel the whole ring and land on
        // CK_category_groups_description_length as a 23514 nothing translates.
        //
        // Carried as ReadOnlyMemory<byte>? and never as byte[]?: a null array converts to a NON-NULL,
        // zero-length ReadOnlyMemory<byte>?, which SealedOrAbsent judges as a supplied value and refuses.
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
                    "The category group description must be base64url text decoding to a sealed "
                    + $"envelope of at most {NarrativeFieldLimits.DescriptionBytes} bytes carrying "
                    + $"envelope version {CiphertextEnvelope.Version}.",
                ];
            }
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        // The lookup runs through the budget query filter, so a group belonging to another budget is
        // indistinguishable from one that never existed - both end here as a 404.
        CategoryGroup? categoryGroup = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (categoryGroup is null)
        {
            throw new NotFoundException("Category group was not found.");
        }

        // Suppressed rather than re-checked, for the reason CreateCategoryGroupHandler gives: an empty
        // dictionary IS the statement that both decodes succeeded, and a second null test would be a rule
        // with two owners.
        //
        // One IndexedName and never two arguments: Update has no spelling for half a name. The
        // description is a separate value because it is a separate column with no index to be half of,
        // and it reaches the entity through SealedOrAbsent - the one member that reads "nothing was
        // supplied" as a value rather than as a malformed envelope.
        categoryGroup.Update(
            IndexedName.Of(envelope!, blindIndex!),
            NarrativeField.SealedOrAbsent(descriptionEnvelope, NarrativeFieldLimits.DescriptionBytes));

        // A collision on the blind index comes back from here as a 400 keyed on Name, which is the same
        // status a duplicate name gets on the create leg of this table - and deliberately not the payees
        // split, where a create answers 409. CategoryGroupRepository argues it at both members.
        await repository.UpdateAsync(categoryGroup, cancellationToken);
    }
}
