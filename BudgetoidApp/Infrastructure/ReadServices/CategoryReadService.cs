using Application.Categories;
using Application.Passkeys;
using Domain.Security;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

// Neither query builds the CategoryDto inside the Select any more. categories.name,
// categories.description and category_groups.name all carry a value converter, so the provider translates
// the property itself and the NarrativeField exists only once the row has materialized; encoding any of
// them in the projection would be a call the translator has to make sense of. AccountReadService,
// PayeeReadService, CategoryGroupReadService and TransactionReadService take the same shape for the same
// reason.
//
// ALL THREE NARRATIVE VALUES ON THIS RECORD ARE SEALED AND ONE RULE COVERS THEM. This header used to say
// that one of the two names was sealed and the other was not, and that a reader must not fold them into
// one treatment; there is nothing left to fold. Every one leaves the Select and is encoded in Shape.
//
// The two envelopes bind to two DIFFERENT rows, which no code here can enforce and a client must honour:
// the category's name and description open under this category's row id, the group's name under the
// GROUP's. CategoryDto's remarks carry that argument.
//
// The ordering is untouched by the sealing: categoryGroup.Position, category.Position, category.Id, all
// of them values this server can still read. Nobody should go looking for an `orderby X.Name` that had
// silently become an ordering by a nonce - this service never had one.
public sealed class CategoryReadService(BudgetoidDbContext dbContext) : ICategoryReadService
{
    public async Task<IReadOnlyList<CategoryDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        List<Row> rows = await (
                from category in dbContext.Categories.AsNoTracking()
                join categoryGroup in dbContext.CategoryGroups.AsNoTracking()
                    on category.CategoryGroupId equals categoryGroup.Id
                orderby categoryGroup.Position, category.Position, category.Id
                select new Row(
                    category.Id,
                    category.Name,
                    category.Description,
                    category.CategoryGroupId,
                    categoryGroup.Name,
                    category.Position))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(Shape)];
    }

    public async Task<CategoryDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Row? row = await (
                from category in dbContext.Categories.AsNoTracking()
                join categoryGroup in dbContext.CategoryGroups.AsNoTracking()
                    on category.CategoryGroupId equals categoryGroup.Id
                where category.Id == id
                select new Row(
                    category.Id,
                    category.Name,
                    category.Description,
                    category.CategoryGroupId,
                    categoryGroup.Name,
                    category.Position))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : Shape(row);
    }

    /// <summary>
    /// One materialized row of either query, carrying all three narrative values as their columns hold
    /// them.
    /// </summary>
    /// <remarks>
    /// A named type rather than an anonymous one, and shared by both queries rather than declared twice:
    /// it is what makes <see cref="Shape"/> the single place a row becomes a wire shape, so a member
    /// added to one projection and not the other stops compiling instead of quietly shipping on one route
    /// only. That property is what a dropped <c>Description</c> runs into: the column is nullable, so a
    /// projection that stopped selecting it would ship <see langword="null"/> on every category and no
    /// constraint anywhere would notice.
    /// </remarks>
    private sealed record Row(
        Guid Id,
        NarrativeField Name,
        NarrativeField? Description,
        Guid CategoryGroupId,
        NarrativeField CategoryGroupName,
        int Position);

    /// <summary>
    /// Turns one materialized row into the shape the wire carries, which is where all three sealed values
    /// become text.
    /// </summary>
    /// <remarks>
    /// Every envelope goes out in the one alphabet every binary member of this API crosses JSON in.
    /// <see cref="PasskeyEncoding"/> despite its name, because it is that alphabet's only implementation
    /// here and a second base64url encoder beside it is exactly the drift its neighbours argue against —
    /// not <c>System.Text.Json</c>'s own <see cref="byte"/><c>[]</c> handling, which emits padded
    /// standard base64 the client's strict decoder refuses. <b>A null description stays null and must
    /// never gain a <c>?? string.Empty</c></b>: the empty string is not a legal envelope, a client cannot
    /// tell one from the other, and the difference being carried is "no note" against "a note somebody
    /// emptied".
    /// </remarks>
    private static CategoryDto Shape(Row row) => new(
        row.Id,
        PasskeyEncoding.Encode(row.Name.Envelope.Span),
        row.Description is null ? null : PasskeyEncoding.Encode(row.Description.Envelope.Span),
        row.CategoryGroupId,
        PasskeyEncoding.Encode(row.CategoryGroupName.Envelope.Span),
        row.Position);
}
