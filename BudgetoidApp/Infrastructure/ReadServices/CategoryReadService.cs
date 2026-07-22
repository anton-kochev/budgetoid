using Application.Categories;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class CategoryReadService(BudgetoidDbContext dbContext) : ICategoryReadService
{
    public async Task<IReadOnlyList<CategoryDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await (
                from category in dbContext.Categories.AsNoTracking()
                join categoryGroup in dbContext.CategoryGroups.AsNoTracking()
                    on category.CategoryGroupId equals categoryGroup.Id
                orderby categoryGroup.Position, category.Position, category.Id
                select new CategoryDto(
                    category.Id,
                    category.Name,
                    category.Description,
                    category.CategoryGroupId,
                    categoryGroup.Name,
                    category.Position))
            .ToListAsync(cancellationToken);
    }

    public async Task<CategoryDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await (
                from category in dbContext.Categories.AsNoTracking()
                join categoryGroup in dbContext.CategoryGroups.AsNoTracking()
                    on category.CategoryGroupId equals categoryGroup.Id
                where category.Id == id
                select new CategoryDto(
                    category.Id,
                    category.Name,
                    category.Description,
                    category.CategoryGroupId,
                    categoryGroup.Name,
                    category.Position))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
