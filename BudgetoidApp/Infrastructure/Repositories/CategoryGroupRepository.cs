using Domain.CategoryGroups;
using Domain.Common;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class CategoryGroupRepository(BudgetoidDbContext dbContext)
    : ICategoryGroupRepository
{
    public async Task AddAsync(
        CategoryGroup categoryGroup,
        CancellationToken cancellationToken = default)
    {
        dbContext.CategoryGroups.Add(categoryGroup);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(categoryGroup).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    public Task<CategoryGroup?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return dbContext.CategoryGroups.FirstOrDefaultAsync(
            categoryGroup => categoryGroup.Id == id,
            cancellationToken);
    }

    public async Task<int> GetNextPositionAsync(CancellationToken cancellationToken = default)
    {
        int? maximum = await dbContext.CategoryGroups
            .Select(categoryGroup => (int?)categoryGroup.Position)
            .MaxAsync(cancellationToken);
        return maximum is null ? 0 : maximum.Value + 1;
    }

    public async Task UpdateAsync(
        CategoryGroup categoryGroup,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(categoryGroup).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    public async Task MoveToPositionAsync(
        CategoryGroup categoryGroup,
        int position,
        CancellationToken cancellationToken = default)
    {
        List<CategoryGroup> ordered = await dbContext.CategoryGroups
            .OrderBy(group => group.Position)
            .ThenBy(group => group.Id)
            .ToListAsync(cancellationToken);
        CategoryGroupOrdering.MoveToPosition(categoryGroup, position, ordered);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        CategoryGroup categoryGroup,
        CancellationToken cancellationToken = default)
    {
        List<CategoryGroup> remaining = await dbContext.CategoryGroups
            .Where(group => group.Id != categoryGroup.Id)
            .OrderBy(group => group.Position)
            .ThenBy(group => group.Id)
            .ToListAsync(cancellationToken);
        CategoryGroupOrdering.CloseGap(remaining);

        dbContext.CategoryGroups.Remove(categoryGroup);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            dbContext.Entry(categoryGroup).State = EntityState.Detached;
            throw ReferencedCategoryGroupValidationException();
        }
    }

    public Task<bool> HasCategoriesAsync(
        Guid categoryGroupId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Categories.AnyAsync(
            category => category.CategoryGroupId == categoryGroupId,
            cancellationToken);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        };

    private static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
        };

    private static ValidationException DuplicateNameValidationException() => new(
        new Dictionary<string, string[]>
        {
            [nameof(CategoryGroup.Name)] = ["Category group name must be unique."],
        });

    private static ValidationException ReferencedCategoryGroupValidationException() => new(
        new Dictionary<string, string[]>
        {
            [nameof(CategoryGroup.Id)] =
                ["Category group cannot be deleted because it has categories."],
        });
}
