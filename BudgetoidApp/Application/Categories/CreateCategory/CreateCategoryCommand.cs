using System.Text.Json.Serialization;

namespace Application.Categories.CreateCategory;

/// <summary>
/// The body of <c>POST /api/categories</c>: an identifier the client minted, the name it sealed against
/// that identifier, the blind index it took over the same text, the note it sealed against the same
/// identifier — or no note at all — and the group the category joins.
/// </summary>
/// <param name="Id">
/// The row's identifier, in the canonical spelling <see cref="Application.Security.CanonicalIdentifier"/>
/// accepts.
/// </param>
/// <param name="Name">The sealed name as unpadded base64url.</param>
/// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
/// <param name="Description">
/// The sealed note as unpadded base64url, or <see langword="null"/> where this category has none.
/// </param>
/// <param name="CategoryGroupId">The group this category is filed under.</param>
/// <remarks>
/// <para>
/// <b><see cref="Id"/> is a <see cref="string"/> and must never become a <see cref="Guid"/>.</b> The
/// argument is <see cref="CategoryGroups.CreateCategoryGroup.CreateCategoryGroupCommand.Id"/>'s and is
/// not restated: bound as a <see cref="Guid"/>, <c>System.Text.Json</c> folds the spellings before any
/// handler sees text, which does not make the check fail — it makes the check unwritable. This row seals
/// <em>two</em> narrative members against that identifier, so a spelling this API cannot reproduce costs
/// a name and a note together.
/// </para>
/// <para>
/// <b><see cref="CategoryGroupId"/> stays a <see cref="Guid"/>, and the difference from
/// <see cref="Id"/> is not an inconsistency.</b> It is associated data for nothing: it is a foreign key
/// the caller read back from this API, so the spellings folding together costs nothing. Only a value
/// something was sealed <em>against</em> needs its exact characters preserved.
/// </para>
/// <para>
/// <b><see cref="Description"/> is the only optional member, and <see langword="null"/> is not the empty
/// string.</b> The decoder underneath refuses <see langword="null"/> and <c>""</c> identically, so the
/// distinction cannot live down there: the handler tests <c>is null</c> and nothing else. An absent
/// member is a category with no note; <c>""</c> is a malformed one and answers 400. Reading them as the
/// same thing writes NULL where a refusal was owed, and that failure is silent all the way down — the row
/// is legal, the response is a 201, and the note the person typed is gone.
/// </para>
/// <para>
/// <b>There is no <c>Position</c> member, and its absence is a decision.</b> The server computes the next
/// position within the group and appends. Sealing took no capability away from an <see cref="int"/> —
/// this side can still read and order by one — so moving the choice to the client would be an unforced
/// product change riding in a security slice.
/// </para>
/// <para>
/// <b>The <c>Disallow</c> below is on this shape for the reason the category-group pair carries it, and
/// NOT for the reason the two transaction shapes do.</b> There the attribute answers wire <em>drift</em>:
/// those shapes retired <c>payeeName</c>, a member a released client still sends. Nothing here is retired
/// — every member is new. What earns it here is <see cref="Description"/>, which binds a <b>nullable</b>
/// narrative column, so under the global <c>Skip</c> an unmapped member is indistinguishable from an
/// absent one: a body sending <c>descriptionn</c> and a body sending no description at all both leave
/// <see cref="Description"/> <see langword="null"/>. On this route that is a <b>201 with a note that never
/// lands</b>; on <c>PUT /api/categories/{id}</c>, whose shape carries the attribute for the same reason,
/// it is a <b>204 and the note the category held is gone</b>. Nothing downstream can tell either from an
/// operation — a category with no note is a legal row and NULL is how it says so.
/// </para>
/// <para>
/// <b>The placement <c>PATCH</c>'s shape deliberately does not get it</b>, and that omission is a
/// negative control rather than an oversight: it binds no nullable narrative member, so it has not made
/// this contract decision. <c>Api/Endpoints/CategoryEndpoints.cs</c> carries the rest.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCategoryCommand(
    string Id,
    string Name,
    string NameKey,
    string? Description,
    Guid CategoryGroupId);
