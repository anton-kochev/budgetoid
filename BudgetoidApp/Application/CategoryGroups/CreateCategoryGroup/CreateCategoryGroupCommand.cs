using System.Text.Json.Serialization;

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
/// <para>
/// <b>The <c>Disallow</c> below is on this shape for a reason the two transaction shapes' is not, and
/// folding the two into one rule loses it.</b> There the attribute answers wire <em>drift</em>: those
/// shapes retired <c>payeeName</c>, a member a released client still sends, and refusing it visibly beat
/// dropping it in silence. Nothing here is retired — every member is new. What earns it here is
/// <see cref="Description"/>, which binds a <b>nullable</b> narrative column, so an unmapped member is
/// indistinguishable from an absent one. Measured against this shape under
/// <c>JsonSerializerDefaults.Web</c> with the options <c>Api/Program.cs</c> registers — whose
/// <c>UnmappedMemberHandling</c> is the default <c>Skip</c> — a body sending <c>descriptionn</c> and a
/// body sending no description at all both leave <see cref="Description"/> <see langword="null"/>, and
/// only the correctly spelled member arrives carrying anything. On this route that is a <b>201 with a
/// note that never lands</b>; on <c>PUT /api/category-groups/{id}</c>, whose shape carries the attribute
/// for the same reason, it is a <b>204 and the note the group held is gone</b>.
/// </para>
/// <para>
/// <b>Nothing downstream can tell that defect from an operation</b>, which is what makes it worth a
/// contract change. A group with no note is a legal row and NULL is how it says so, so the write
/// succeeds, the schema is satisfied and every read afterwards agrees the group has no description. The
/// paragraph above spends itself distinguishing <see langword="null"/> from <c>""</c> for exactly this
/// reason; that care buys nothing while a typo outside the shape reaches the same NULL by a path no
/// member of this record is named in. <c>Api/Endpoints/CategoryGroupEndpoints.cs</c> carries the rest —
/// the per-type discipline and what the refusal costs a caller.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCategoryGroupCommand(
    string Id,
    string Name,
    string NameKey,
    string? Description);
