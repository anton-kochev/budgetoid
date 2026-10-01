namespace Application.Categories.UpdateCategory;

/// <summary>
/// The body of <c>PUT /api/categories/{id}</c>, joined to the identifier the route carries: the name the
/// client re-sealed, the blind index it took over the same text, and the note it sealed — or no note,
/// which clears the one the category held.
/// </summary>
/// <param name="Id">The row being replaced, taken from the route and never from the body.</param>
/// <param name="Name">The sealed name as unpadded base64url.</param>
/// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
/// <param name="Description">
/// The sealed note as unpadded base64url, or <see langword="null"/> to leave the category with none.
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="Id"/> is a <see cref="Guid"/> here where the create's is a <see cref="string"/>, and
/// that asymmetry is the rule rather than drift.</b> On a create the client <em>chooses</em> an id and
/// seals against the spelling it chose, so the canonical form has to survive binding. On an update it
/// re-seals against the row's <em>existing</em> id, which it read back from this API in the one form
/// <see cref="Guid"/> renders — so there is no second spelling for the two sides to disagree about.
/// </para>
/// <para>
/// <b>An absent <see cref="Description"/> CLEARS the note, because the route is a <c>PUT</c>.</b> A full
/// replacement has no "leave it alone" state, so <c>Optional&lt;T&gt;</c> has no business on this path —
/// the transaction routes carry it because they are <c>PATCH</c> and genuinely have a third state.
/// <c>""</c> is neither: it is a malformed envelope and a 400.
/// </para>
/// </remarks>
public sealed record UpdateCategoryCommand(
    Guid Id,
    string Name,
    string NameKey,
    string? Description);
