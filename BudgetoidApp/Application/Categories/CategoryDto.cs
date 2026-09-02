namespace Application.Categories;

/// <summary>
/// One category as every category-reading route hands it back, with the group it belongs to already
/// denormalized onto it.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS RECORD CARRIES A SEALED NAME AND A PLAINTEXT NAME SIDE BY SIDE, AND IT IS THE FIRST IN THE
/// PRODUCT TO DO SO.</b> <see cref="CategoryGroupName"/> is still typed <see cref="string"/> and no
/// longer holds a name — it is <c>category_groups.name</c>'s AEAD envelope as unpadded base64url, the
/// treatment <c>AccountDto.Name</c>, <c>PayeeDto.Name</c> and <c>CategoryGroupDto.Name</c> carry, because
/// this server holds no key for it. <see cref="Name"/> and <see cref="Description"/> are readable text:
/// <c>categories.name</c> and <c>categories.description</c> are not sealed yet. A reader must not fold
/// the three into one rule in either direction — neither decoding a category name as base64url nor
/// rendering a group name as a caption.
/// </para>
/// <para>
/// <b>The mixed state lasts one slice.</b> <c>categories.*</c> is the next column pair to be sealed, at
/// which point every narrative member here is an envelope and this paragraph is deleted rather than
/// amended. It is written down so the next author rewrites it instead of patching around it.
/// </para>
/// <para>
/// <b>The envelope on this record is bound to a different row than the record is about.</b> Associated
/// data is rebuilt from wherever a ciphertext was found, so opening <see cref="CategoryGroupName"/> needs
/// the binding for <c>category_groups.name</c> under the <em>group's</em> row id — which the client
/// rebuilds from <see cref="CategoryGroupId"/> — and not under this category's. A client that reached for
/// this category's own binding gets an authentication failure, not garbage: the tag check fails and the
/// value is unreadable, with nothing naming the cause. The envelope travels beside the identifier it was
/// sealed against, which is why that identifier is on the wire.
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
