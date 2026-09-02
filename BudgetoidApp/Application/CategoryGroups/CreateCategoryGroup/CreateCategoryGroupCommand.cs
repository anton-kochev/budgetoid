namespace Application.CategoryGroups.CreateCategoryGroup;

/// <summary>
/// The body of <c>POST /api/category-groups</c>: an identifier the client minted, the name it sealed
/// against that identifier, the blind index it took over the same text, and the note it sealed against
/// the same identifier — or no note at all.
/// </summary>
/// <param name="Id">
/// The row's identifier, in the canonical spelling <see cref="Application.Security.CanonicalIdentifier"/>
/// accepts.
/// </param>
/// <param name="Name">The sealed name as unpadded base64url.</param>
/// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
/// <param name="Description">
/// The sealed note as unpadded base64url, or <see langword="null"/> where this group has none.
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="Id"/> is a <see cref="string"/> and must never become a <see cref="Guid"/>.</b> The
/// argument is <see cref="Payees.CreatePayee.CreatePayeeCommand.Id"/>'s and is not restated: bound as a
/// <see cref="Guid"/>, <c>System.Text.Json</c> folds the spellings before any handler sees text, which
/// does not make the check fail — it makes the check unwritable. What is worse here than there is the
/// blast radius of getting it wrong: this row seals <em>two</em> narrative members against that
/// identifier, so a spelling this API cannot reproduce costs a name and a note together.
/// </para>
/// <para>
/// <b><see cref="Description"/> is the only optional member, and <see langword="null"/> is not the empty
/// string.</b> The decoder underneath refuses <see langword="null"/> and <c>""</c> identically, so the
/// distinction cannot live down there: the handler tests <c>is null</c> and nothing else. An absent
/// member is a group with no note; <c>""</c> is a malformed one and answers 400. Reading them as the
/// same thing writes NULL where a refusal was owed, and that failure is silent all the way down — the row
/// is legal, the response is a 201, and the note the person typed is gone.
/// </para>
/// <para>
/// <b>There is no <c>Position</c> member, and its absence is a decision.</b> The server computes the next
/// position and appends. Sealing took no capability away from an <see cref="int"/> — this side can still
/// read and order by one — so moving the choice to the client would be an unforced product change riding
/// in a security slice, and it would delete the append rule with nothing replacing it.
/// </para>
/// </remarks>
public sealed record CreateCategoryGroupCommand(
    string Id,
    string Name,
    string NameKey,
    string? Description);
