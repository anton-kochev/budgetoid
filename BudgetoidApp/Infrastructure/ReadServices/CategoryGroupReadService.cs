using Application.CategoryGroups;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class CategoryGroupReadService(BudgetoidDbContext dbContext)
    : ICategoryGroupReadService
{
    public async Task<IReadOnlyList<CategoryGroupDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await dbContext.CategoryGroups
            .AsNoTracking()
            .OrderBy(categoryGroup => categoryGroup.Position)
            .ThenBy(categoryGroup => categoryGroup.Id)
            .Select(categoryGroup => new CategoryGroupDto(
                categoryGroup.Id,
                categoryGroup.Name,
                categoryGroup.Description,
                categoryGroup.Position))
            .ToListAsync(cancellationToken);
    }

    public Task<CategoryGroupDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return dbContext.CategoryGroups
            .AsNoTracking()
            .Where(categoryGroup => categoryGroup.Id == id)
            .Select(categoryGroup => new CategoryGroupDto(
                categoryGroup.Id,
                categoryGroup.Name,
                categoryGroup.Description,
                categoryGroup.Position))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
