namespace Application.Categories;

/// <summary>
/// One category as every category-reading route hands it back, with the group it belongs to already
/// denormalized onto it.
/// </summary>
/// <remarks>
/// <para>
/// <b>All three narrative members are envelopes and none of them holds text.</b> <see cref="Name"/>,
/// <see cref="Description"/> and <see cref="CategoryGroupName"/> are still typed <see cref="string"/> —
/// the treatment <c>AccountDto.Name</c>, <c>PayeeDto.Name</c>, <c>CategoryGroupDto.Name</c> and
/// <c>ExportedBudget.Name</c> already carry — because each is its column's AEAD envelope as unpadded
/// base64url and this server holds no key for any of them. Not <c>System.Text.Json</c>'s own
/// <see cref="byte"/><c>[]</c> handling, which emits padded standard base64: two spellings that disagree
/// the first time somebody decodes one with the other. This record used to carry a sealed name and a
/// plaintext name side by side and warn that a reader must not fold them into one rule; there is nothing
/// left to fold, and the rule now covers every member.
/// </para>
/// <para>
/// <b><see cref="Description"/> stays <see cref="string"/><c>?</c> and must never gain a
/// <c>?? string.Empty</c>.</b> <c>""</c> is not a legal envelope, and a client cannot tell the coercion
/// from a value it is expected to decode. The distinction being carried is a real one:
/// <see langword="null"/> is a note nobody wrote, and a twenty-nine-byte envelope is a note somebody
/// wrote and then emptied.
/// </para>
/// <para>
/// <b>The two envelopes on this record are bound to two different rows, and that is the property a
/// client has to honour.</b> Associated data is rebuilt from wherever a ciphertext was found, so opening
/// <see cref="Name"/> and <see cref="Description"/> needs the bindings for <c>categories.name</c> and
/// <c>categories.description</c> under <see cref="Id"/>, while opening
/// <see cref="CategoryGroupName"/> needs <c>category_groups.name</c> under
/// <see cref="CategoryGroupId"/> — the <em>group's</em> row id, not this category's. A client that
/// reached for the wrong binding gets an authentication failure, not garbage: the tag check fails and the
/// value is unreadable, with nothing naming the cause. Each envelope travels beside the identifier it was
/// sealed against, which is why both identifiers are on the wire.
/// </para>
/// <para>
/// <b>The blind index is on no read, and its absence is a decision rather than an omission.</b> A client
/// recomputes it from the name it just decrypted, under a key only it holds, and needs it solely to
/// write. A member nobody reads would be a standing surface with no reason — and this one would be a
/// deterministic per-account fingerprint of every category name, with no key anywhere in the exchange.
/// </para>
/// </remarks>
public sealed record CategoryDto(
    Guid Id,
    string Name,
    string? Description,
    Guid CategoryGroupId,
    string CategoryGroupName,
    int Position);

public sealed record CategoryListResponse(IReadOnlyList<CategoryDto> Items);
