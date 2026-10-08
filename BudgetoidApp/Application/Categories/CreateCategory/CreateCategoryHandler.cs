using Application.Abstractions;
using Application.Passkeys;
using Application.Security;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Security;
// Domain.Common.ValidationException by name, because Application.Abstractions declares one too and only
// the Domain's is what ValidationExceptionHandler turns into a 400 carrying the field errors.
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Categories.CreateCategory;

/// <summary>
/// Writes the category a client minted, sealed and indexed, and appends it to the person's own ordering
/// inside the group it names.
/// </summary>
/// <remarks>
/// <b>Five members arrive and this server can read exactly two of them.</b> The identifier is checked for
/// a spelling it can reproduce and the group id is a foreign key it resolves; the other three are opaque
/// and judged for shape alone. The position is the one value the server still computes, because it is the
/// one value it can still read.
/// </remarks>
public sealed class CreateCategoryHandler(
    ICategoryRepository repository,
    ICategoryGroupRepository categoryGroups,
    IBudgetContext budgetContext,
    TimeProvider timeProvider)
    : ICommandHandler<CreateCategoryCommand, CategoryDto>
{
    public async Task<CategoryDto> HandleAsync(
        CreateCategoryCommand command,
        CancellationToken cancellationToken = default)
    {
        // The members this server cannot read, judged for shape and nothing else, ABOVE the group lookup
        // because base64url and the spelling of a uuid are wire concerns: by the time bytes reach
        // IndexedName.Of and NarrativeField.SealedOrAbsent they are a Guid and two buffers, and those
        // factories throw ArgumentException rather than the exception that becomes a 400. Run below the
        // lookup, a malformed body naming an unknown group would answer about the group and the caller
        // would never hear which member they got wrong.
        //
        // EVERY MEMBER IS ATTEMPTED AND EVERY FAILURE IS REPORTED - the idiom CreateCategoryGroupHandler,
        // CreatePayeeHandler and CreateAccountHandler keep for their own members. They arrive together,
        // are produced by one piece of client code and are opaque to this side in the same way, so a
        // client that got two of them wrong would otherwise learn about the second only after fixing the
        // first and sending everything again. The description is the member most at risk of losing that
        // property, because it alone sits behind a branch: judged inside an early return, or after the
        // throw below, it would never be reported alongside the others.
        var errors = new Dictionary<string, string[]>();

        // The identifier first, because it is what BOTH narrative members were sealed against: a client
        // that sent a spelling this API cannot reproduce has a name and a note nothing will ever open,
        // whatever the envelopes beside it look like. The rule is CanonicalIdentifier's and is shared with
        // every other client-minted id this API takes; only the sentence is this route's.
        if (!CanonicalIdentifier.TryParse(command.Id, out Guid id))
        {
            errors[nameof(command.Id)] =
            [
                "The category id must be a uuid in the lower-case 36-character hyphenated form with no "
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
        // and the domain's is what refuses a ceiling mistyped on this line - loudly, as a defect in this
        // codebase rather than in the request.
        if (!CiphertextEnvelopeText.TryDecode(
                command.Name, NarrativeFieldLimits.NameBytes, out byte[]? envelope))
        {
            errors[nameof(command.Name)] =
            [
                "The category name must be base64url text decoding to a sealed envelope of at most "
                + $"{NarrativeFieldLimits.NameBytes} bytes carrying envelope version "
                + $"{CiphertextEnvelope.Version}.",
            ];
        }

        // Judged by its own type and deliberately not by the one above, which is the near miss: a blind
        // index is a keyed digest with no version byte, so the envelope's framing rules would refuse
        // roughly 255 values in 256 as malformed. What the two share is the alphabet, and BlindIndexText
        // is that shared decoder plus the one shape check this side can make - the width. Nothing here can
        // say the index is the index OF the name beside it; that needs the account's index key, which
        // lives in a browser, so a wrong 32 bytes keys perfectly, never collides, and stands for a name
        // this row does not hold.
        if (!BlindIndexText.TryDecode(command.NameKey, out byte[]? blindIndex))
        {
            errors[nameof(command.NameKey)] =
            [
                "The category name index must be base64url text decoding to exactly "
                + $"{IndexedName.BlindIndexLength} bytes.",
            ];
        }

        // THE ABSENCE TEST IS `is null` AND THE SPELLING IS THE WHOLE OF A RULE. Never
        // string.IsNullOrEmpty and never string.IsNullOrWhiteSpace: PasskeyEncoding.TryDecode, which
        // CiphertextEnvelopeText delegates to, refuses null and "" identically, so the decoder underneath
        // cannot make this distinction and it has to live here. Either forgiving spelling folds a
        // malformed "" into "absent" and stores NULL where a 400 was owed - a legal row, a 201 on the
        // wire, and a note the person typed silently gone.
        //
        // DescriptionBytes and NOT NameBytes, and this line is the ONLY owner of that number on this
        // path. The name's cap is stated twice - here and again inside IndexedName.Of - so a mistyped
        // name ceiling is refused by the domain as a defect in this codebase. The description's is stated
        // here and at SealedOrAbsent below, with the same constant and no literal between them: widen it
        // and an over-cap description travels the whole ring to land on
        // CK_categories_description_length, reaching the caller as a 23514 nothing translates rather than
        // as the 400 it should have been.
        //
        // Carried as ReadOnlyMemory<byte>? and never as byte[]?, because the two are not interchangeable
        // at SealedOrAbsent's parameter: a null array converts to a NON-NULL, zero-length
        // ReadOnlyMemory<byte>?, which that member judges as a supplied value and refuses with an
        // ArgumentException a caller cannot act on. The nullable struct is what carries "nothing was
        // supplied" all the way to the one member that asks.
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
                    "The category description must be base64url text decoding to a sealed envelope of at "
                    + $"most {NarrativeFieldLimits.DescriptionBytes} bytes carrying envelope version "
                    + $"{CiphertextEnvelope.Version}.",
                ];
            }
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        CategoryGroup? categoryGroup = await categoryGroups.GetByIdAsync(
            command.CategoryGroupId,
            cancellationToken);
        if (categoryGroup is null)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.CategoryGroupId)] = ["Category group was not found."],
            });
        }

        // Suppressed rather than re-checked: an empty dictionary is exactly the statement that every
        // decode above succeeded, and a second null test here would be a rule with two owners - the one a
        // later reader edited would decide what happens, and the other would go on looking correct.
        //
        // The description's null is NOT suppressed, because here it is a value rather than a failure: a
        // category with no note reaches SealedOrAbsent as null and comes back null. That member is the one
        // door for it - Sealed would judge default(ReadOnlyMemory<byte>) as an envelope and refuse a row
        // that is legal.
        //
        // Position is the server's own and the append rule lives here: it is the one member of this row
        // this side can still read.
        int position = await repository.GetNextPositionAsync(categoryGroup.Id, cancellationToken);
        Category category = Category.Create(
            id,
            budgetContext.BudgetId,
            categoryGroup.Id,
            IndexedName.Of(envelope!, blindIndex!),
            NarrativeField.SealedOrAbsent(descriptionEnvelope, NarrativeFieldLimits.DescriptionBytes),
            position,
            timeProvider.GetUtcNow().UtcDateTime);

        await repository.AddAsync(category, cancellationToken);

        // FOUR VALUES CROSS THIS RETURN AND THREE OF THEM ARE ENVELOPES THIS SERVER HOLDS NO KEY FOR.
        // categories.name, categories.description and category_groups.name are all handed on untouched,
        // in the alphabet every binary member of this API crosses JSON in, and the browser that asked for
        // them is what turns them back into text. Decoding any of them here would need a key on this side,
        // which is the design the product exists to avoid - and a placeholder string would be a lie the
        // screen renders. This return used to carry the category's own name and description as text and
        // said so; there is no such member left, and the three are now one rule.
        //
        // PasskeyEncoding despite its name, because it is this codebase's only base64url implementation
        // and a second encoder beside it is exactly the drift its neighbours argue against. Not
        // System.Text.Json's own byte[] handling, which emits padded standard base64 the client's strict
        // decoder refuses.
        return new CategoryDto(
            category.Id,
            PasskeyEncoding.Encode(category.Name.Envelope.Span),

            // Null stays null: the member is absent on a category filing no note, which is not the same
            // as an envelope over an empty one.
            category.Description is null
                ? null
                : PasskeyEncoding.Encode(category.Description.Envelope.Span),
            category.CategoryGroupId,
            PasskeyEncoding.Encode(categoryGroup.Name.Envelope.Span),
            category.Position);
    }
}
