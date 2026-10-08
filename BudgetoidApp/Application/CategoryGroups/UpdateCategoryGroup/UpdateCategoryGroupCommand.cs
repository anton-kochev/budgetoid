namespace Application.CategoryGroups.UpdateCategoryGroup;

/// <summary>
/// <c>PUT /api/category-groups/{id}</c>: the row named by the route, the name the client re-sealed
/// against that row's existing identifier, the blind index over the same text, and the note it sealed
/// against the same identifier — or no note at all.
/// </summary>
/// <param name="Id">The row to update, taken from the route.</param>
/// <param name="Name">The new sealed name as unpadded base64url.</param>
/// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
/// <param name="Description">
/// The new sealed note as unpadded base64url, or <see langword="null"/> to leave the group with none.
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="Id"/> is a <see cref="Guid"/> here and a <see cref="string"/> on
/// <see cref="CreateCategoryGroup.CreateCategoryGroupCommand"/>, and the asymmetry is the point rather
/// than an oversight.</b> The argument is
/// <see cref="Payees.RenamePayee.RenamePayeeCommand"/>'s and is not restated: on a create the client is
/// <em>choosing</em> an identifier and sealing against the spelling it chose; on an update it re-seals
/// against the row's <em>existing</em> id, read back from this API in the one form <see cref="Guid"/>
/// renders. The text in the URL is never the text anything was sealed under.
/// </para>
/// <para>
/// <b>The route is a <c>PUT</c>, so an absent <see cref="Description"/> CLEARS the note the group
/// held.</b> A full replacement has no "leave it alone" state, and <c>Optional&lt;T&gt;</c> must not
/// appear here to invent one — the transaction routes carry it because they are <c>PATCH</c> and
/// genuinely have a third state. What separates "no note" from a malformed one is
/// <c>Description is null</c> in the handler and nothing else: the decoder underneath refuses
/// <see langword="null"/> and <c>""</c> identically, so a forgiving absence test would answer 204 and
/// clear a description the caller never asked to remove.
/// </para>
/// <para>
/// <b><see cref="Name"/> and <see cref="NameKey"/> are both required, and an update carrying only the
/// first would be the mistake the pair exists to make unspellable.</b> The row would hold new ciphertext
/// under the previous name's index; see <see cref="Domain.CategoryGroups.CategoryGroup.Update"/> for what
/// that costs and for why nothing on this side could notice.
/// </para>
/// </remarks>
public sealed record UpdateCategoryGroupCommand(
    Guid Id,
    string Name,
    string NameKey,
    string? Description);
