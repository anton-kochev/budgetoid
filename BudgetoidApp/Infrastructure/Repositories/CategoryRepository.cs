using Domain.Categories;
using Domain.Common;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class CategoryRepository(BudgetoidDbContext dbContext) : ICategoryRepository
{
    public async Task AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        dbContext.Categories.Add(category);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(category).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            dbContext.Entry(category).State = EntityState.Detached;
            throw CategoryGroupValidationException();
        }
    }

    public Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Categories.FirstOrDefaultAsync(
            category => category.Id == id,
            cancellationToken);
    }

    public async Task<int> GetNextPositionAsync(
        Guid categoryGroupId,
        CancellationToken cancellationToken = default)
    {
        int? maximum = await dbContext.Categories
            .Where(category => category.CategoryGroupId == categoryGroupId)
            .Select(category => (int?)category.Position)
            .MaxAsync(cancellationToken);
        return maximum is null ? 0 : maximum.Value + 1;
    }

    public async Task UpdateAsync(
        Category category,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(category).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    public async Task PlaceAsync(
        Category category,
        Guid categoryGroupId,
        int position,
        CancellationToken cancellationToken = default)
    {
        List<Category> source = await LoadGroupWithoutAsync(
            category.CategoryGroupId,
            category.Id,
            cancellationToken);
        List<Category> destination = category.CategoryGroupId == categoryGroupId
            ? source
            : await LoadGroupWithoutAsync(categoryGroupId, category.Id, cancellationToken);
        CategoryOrdering.Place(category, categoryGroupId, position, source, destination);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            dbContext.Entry(category).State = EntityState.Detached;
            throw CategoryGroupValidationException();
        }
    }

    public async Task DeleteAsync(
        Category category,
        CancellationToken cancellationToken = default)
    {
        List<Category> remaining = await LoadGroupWithoutAsync(
            category.CategoryGroupId,
            category.Id,
            cancellationToken);
        CategoryOrdering.CloseGap(remaining, category.CategoryGroupId);

        dbContext.Categories.Remove(category);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            dbContext.Entry(category).State = EntityState.Detached;
            throw ReferencedCategoryValidationException();
        }
    }

    public Task<bool> HasTransactionsAsync(
        Guid categoryId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Transactions.AnyAsync(
            transaction => transaction.CategoryId == categoryId,
            cancellationToken);
    }

    private async Task<List<Category>> LoadGroupWithoutAsync(
        Guid categoryGroupId,
        Guid excludedCategoryId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Categories
            .Where(category =>
                category.CategoryGroupId == categoryGroupId
                && category.Id != excludedCategoryId)
            .OrderBy(category => category.Position)
            .ThenBy(category => category.Id)
            .ToListAsync(cancellationToken);
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
            [nameof(Category.Name)] = ["Category name must be unique."],
        });

    private static ValidationException CategoryGroupValidationException() => new(
        new Dictionary<string, string[]>
        {
            [nameof(Category.CategoryGroupId)] = ["Category group was not found."],
        });

    private static ValidationException ReferencedCategoryValidationException() => new(
        new Dictionary<string, string[]>
        {
            [nameof(Category.Id)] =
                ["Category cannot be deleted because it has transactions."],
        });
}
