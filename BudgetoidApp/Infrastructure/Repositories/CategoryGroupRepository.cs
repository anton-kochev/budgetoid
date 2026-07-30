using Domain.CategoryGroups;
using Domain.Common;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
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
        // Named, because SaveChanges flushes every tracked row and not just this group: only the group
        // name index says the name the caller just typed is the one already taken.
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, CategoryGroupConfiguration.NameIndexName))
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
        // Same index as AddAsync: a rename collides with exactly the rule an insert would.
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, CategoryGroupConfiguration.NameIndexName))
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
        // The categories reference is the only foreign key pointing at category_groups, so this name is
        // the whole of "it has categories" — a guarantee that stays true by being stated rather than by
        // nothing else happening to reference the table.
        catch (DbUpdateException exception)
            when (IsForeignKeyViolationOf(exception, CategoryConfiguration.CategoryGroupForeignKeyName))
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

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        } postgresException && postgresException.ConstraintName == indexName;

    private static bool IsForeignKeyViolationOf(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
        } postgresException && postgresException.ConstraintName == constraintName;

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
