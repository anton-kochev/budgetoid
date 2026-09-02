using Application.Categories;
using Application.Passkeys;
using Domain.Security;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

// Neither query builds the CategoryDto inside the Select any more. category_groups.name carries a value
// converter, so the provider translates the property itself and the NarrativeField exists only once the
// row has materialized; encoding it in the projection would be a call the translator has to make sense
// of. AccountReadService, PayeeReadService and TransactionReadService take the same shape for the same
// reason.
//
// ONE OF THE TWO NAMES ON THIS RECORD IS SEALED AND THE OTHER IS NOT. The group's leaves the Select and
// is encoded in Shape; the category's own name and description stay inside it, because categories.name
// and categories.description are still readable text. That is a statement about today - those columns are
// the next to move - and not a rule. A reader must not fold the two into one treatment in either
// direction.
//
// The ordering is untouched by the sealing: categoryGroup.Position, category.Position, category.Id, all
// of them values this server can still read.
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
    /// One materialized row of either query, carrying the group's name as its column holds it and the
    /// category's own as text.
    /// </summary>
    /// <remarks>
    /// A named type rather than an anonymous one, and shared by both queries rather than declared twice:
    /// it is what makes <see cref="Shape"/> the single place a row becomes a wire shape, so a member
    /// added to one projection and not the other stops compiling instead of quietly shipping on one route
    /// only.
    /// </remarks>
    private sealed record Row(
        Guid Id,
        string Name,
        string? Description,
        Guid CategoryGroupId,
        NarrativeField CategoryGroupName,
        int Position);

    /// <summary>
    /// Turns one materialized row into the shape the wire carries, which is where the group's sealed name
    /// becomes text.
    /// </summary>
    /// <remarks>
    /// The envelope goes out in the one alphabet every binary member of this API crosses JSON in.
    /// <see cref="PasskeyEncoding"/> despite its name, because it is that alphabet's only implementation
    /// here — not <c>System.Text.Json</c>'s own <see cref="byte"/><c>[]</c> handling, which emits padded
    /// standard base64 the client's strict decoder refuses. The category's own name and description pass
    /// through untouched, because they are still the text they say they are.
    /// </remarks>
    private static CategoryDto Shape(Row row) => new(
        row.Id,
        row.Name,
        row.Description,
        row.CategoryGroupId,
        PasskeyEncoding.Encode(row.CategoryGroupName.Envelope.Span),
        row.Position);
}
