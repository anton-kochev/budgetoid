using Application.Passkeys;
using Domain.CategoryGroups;

namespace Application.CategoryGroups;

/// <summary>
/// One category group as every category-group-reading route hands it back.
/// </summary>
/// <param name="Id">
/// The row's identifier — also the associated data <paramref name="Name"/> and
/// <paramref name="Description"/> were both sealed against, which is why it is on a read at all rather
/// than only on the write that chose it.
/// </param>
/// <param name="Name">The <b>sealed</b> name as unpadded base64url; see the remarks.</param>
/// <param name="Description">
/// The <b>sealed</b> note as unpadded base64url, or <see langword="null"/> where the group has none.
/// </param>
/// <param name="Position">Where the group sits in the person's own ordering.</param>
/// <remarks>
/// <para>
/// <b><see cref="Name"/> and <see cref="Description"/> are still <see cref="string"/> and neither holds
/// text</b> — the treatment <c>AccountDto.Name</c>, <c>PayeeDto.Name</c> and <c>ExportedBudget.Name</c>
/// already carry. Both columns are AEAD envelopes this server cannot open, so what ships is those
/// envelopes in the one alphabet every binary member of this API crosses JSON in: unpadded base64url,
/// which the client's strict decoder already reads. Not <c>System.Text.Json</c>'s own
/// <see cref="byte"/><c>[]</c> handling, which emits padded standard base64 — two spellings that
/// disagree the first time somebody decodes one with the other.
/// </para>
/// <para>
/// <b><see cref="Description"/> stays <see cref="string"/><c>?</c> and must never gain a
/// <c>?? string.Empty</c>.</b> <c>TransactionDto</c> coerces a null description to the empty string
/// because a screen has to render something; here the member is an envelope, <c>""</c> is not a legal
/// one, and a client cannot tell the coercion from a value it is expected to decode. The distinction
/// being carried is a real one: <see langword="null"/> is a note nobody wrote, and a twenty-nine-byte
/// envelope is a note somebody wrote and then emptied.
/// </para>
/// <para>
/// <b>The blind index is on no read, and its absence is a decision rather than an omission.</b> The
/// requirement says the index is stored, not returned: a client recomputes it from the name it just
/// decrypted, under a key only it holds, and needs it solely to write. A member nobody reads is a
/// standing surface with no reason, and this one would be a deterministic per-account fingerprint of
/// every group name — enough to tell which two accounts label their spending the same way, with no key
/// anywhere in the exchange.
/// </para>
/// </remarks>
public sealed record CategoryGroupDto(
    Guid Id,
    string Name,
    string? Description,
    int Position)
{
    /// <summary>
    /// Shapes the entity a write path has just produced into the body that path answers with.
    /// </summary>
    /// <remarks>
    /// <b>A factory rather than a constructor call at each write site, so the 201 body and the body
    /// <c>GET /api/category-groups/{id}</c> answers cannot encode differently.</b> Both members leave
    /// through <see cref="PasskeyEncoding.Encode"/> — the one base64url implementation in this codebase,
    /// reached for despite its name because a second encoder beside it is exactly the drift its
    /// neighbours argue against. A call site that reached for <c>System.Text.Json</c>'s own
    /// <see cref="byte"/><c>[]</c> handling instead would compile, ship padded standard base64, and be
    /// visible only to a case that compares one route's spelling against another's.
    /// </remarks>
    public static CategoryGroupDto FromCategoryGroup(CategoryGroup categoryGroup) => new(
        categoryGroup.Id,
        PasskeyEncoding.Encode(categoryGroup.Name.Envelope.Span),

        // Null stays null: the member is absent on a group filing no note, which is not the same as an
        // envelope over an empty one.
        categoryGroup.Description is null
            ? null
            : PasskeyEncoding.Encode(categoryGroup.Description.Envelope.Span),
        categoryGroup.Position);
}

public sealed record CategoryGroupListResponse(IReadOnlyList<CategoryGroupDto> Items);
