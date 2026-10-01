using Application.CategoryGroups;
using Application.Passkeys;
using Domain.Security;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

// Neither query below builds the CategoryGroupDto inside the Select any more. category_groups.name and
// category_groups.description both carry a value converter, so the provider translates the properties
// themselves and the NarrativeField exists only once the row has materialized; encoding either in the
// projection would be a call the translator has to make sense of. The shape is AccountReadService's and
// PayeeReadService's, deliberately, because it is the same problem.
//
// THE ORDERING IS UNCHANGED AND THAT IS WORTH SAYING. The accounts and payees slices each had to move an
// `orderby X.Name` that had silently become an ordering by the nonce. This service never ordered by name:
// it orders by Position and then by Id, and Position is an int the server can still read. Nobody should
// go looking for the move that is missing here.
public sealed class CategoryGroupReadService(BudgetoidDbContext dbContext)
    : ICategoryGroupReadService
{
    public async Task<IReadOnlyList<CategoryGroupDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        List<Row> rows = await dbContext.CategoryGroups
            .AsNoTracking()
            .OrderBy(categoryGroup => categoryGroup.Position)
            .ThenBy(categoryGroup => categoryGroup.Id)
            .Select(categoryGroup => new Row(
                categoryGroup.Id,
                categoryGroup.Name,
                categoryGroup.Description,
                categoryGroup.Position))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(Shape)];
    }

    public async Task<CategoryGroupDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Row? row = await dbContext.CategoryGroups
            .AsNoTracking()
            .Where(categoryGroup => categoryGroup.Id == id)
            .Select(categoryGroup => new Row(
                categoryGroup.Id,
                categoryGroup.Name,
                categoryGroup.Description,
                categoryGroup.Position))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : Shape(row);
    }

    /// <summary>
    /// One materialized row of either query, carrying both narrative values as their columns hold them.
    /// </summary>
    /// <remarks>
    /// A named type rather than an anonymous one, and shared by both queries rather than declared twice:
    /// it is what makes <see cref="Shape"/> the single place a row becomes a wire shape, so a member
    /// added to one projection and not the other stops compiling instead of quietly shipping on one route
    /// only.
    /// </remarks>
    private sealed record Row(
        Guid Id,
        NarrativeField Name,
        NarrativeField? Description,
        int Position);

    /// <summary>
    /// Turns one materialized row into the shape the wire carries, which is where both sealed values
    /// become text.
    /// </summary>
    /// <remarks>
    /// Both envelopes go out in the one alphabet every binary member of this API crosses JSON in.
    /// <see cref="PasskeyEncoding"/> despite its name, because it is that alphabet's only implementation
    /// here and a second base64url encoder beside it is exactly the drift its neighbours argue against —
    /// and not <c>System.Text.Json</c>'s own <see cref="byte"/><c>[]</c> handling, which emits padded
    /// standard base64 the client's strict decoder refuses. <b>A null description stays null and must
    /// never gain a <c>?? string.Empty</c></b>: the empty string is not a legal envelope, a client cannot
    /// tell one from the other, and the difference being carried is "no note" against "a note somebody
    /// emptied".
    /// </remarks>
    private static CategoryGroupDto Shape(Row row) => new(
        row.Id,
        PasskeyEncoding.Encode(row.Name.Envelope.Span),
        row.Description is null ? null : PasskeyEncoding.Encode(row.Description.Envelope.Span),
        row.Position);
}
