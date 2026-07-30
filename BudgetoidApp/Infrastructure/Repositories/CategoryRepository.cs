using Domain.Categories;
using Domain.Common;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
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
        // Named, because SaveChanges flushes every tracked row and not just this category: only the
        // category name index says the name the caller just typed is the one already taken.
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, CategoryConfiguration.NameIndexName))
        {
            dbContext.Entry(category).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
        // The sharpest case in this file: a category points at both a group and a budget, and both
        // refusals arrive as 23503. Only the group's name makes "Category group was not found." true
        // rather than a confident, specific lie about a group the caller can still read.
        catch (DbUpdateException exception)
            when (IsForeignKeyViolationOf(exception, CategoryConfiguration.CategoryGroupForeignKeyName))
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
        // Same index as AddAsync: a rename collides with exactly the rule an insert would.
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, CategoryConfiguration.NameIndexName))
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
        // The destination group is what this call can get wrong, and it is the group's own foreign key
        // that says so — the budget reference is untouched by a move and has no business answering for
        // it.
        catch (DbUpdateException exception)
            when (IsForeignKeyViolationOf(exception, CategoryConfiguration.CategoryGroupForeignKeyName))
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
        // The transactions reference is the only foreign key pointing at categories, so this name is the
        // entirety of "this category still has transactions". Any other 23503 reaching here is a
        // different failure and must propagate rather than come back as a message about transactions
        // this category does not have.
        catch (DbUpdateException exception)
            when (IsForeignKeyViolationOf(exception, TransactionConfiguration.CategoryForeignKeyName))
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
