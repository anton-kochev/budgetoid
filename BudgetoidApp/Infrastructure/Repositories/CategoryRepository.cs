using Domain.Categories;
using Domain.Common;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class CategoryRepository(BudgetoidDbContext dbContext) : ICategoryRepository
{
    /// <summary>
    /// Inserts a category the caller minted, sealed and indexed, answering a duplicate name with a 400
    /// keyed on the name and a duplicate identifier with a 409.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two statuses for one SQLSTATE on one table, and the split is between the two CONSTRAINTS rather
    /// than between create and update.</b> This is <c>CategoryGroupRepository.AddAsync</c>'s shape and
    /// deliberately not <c>PayeeRepository.AddAsync</c>'s, which answers <b>409</b> to a duplicate name
    /// on its own create. The constraint is one and the REMEDIES are two, and the status follows the
    /// remedy: a payee create collides because the client's decrypted list was stale and the resolution
    /// is to adopt the row that already exists, which is not a field anybody can edit; a category is not
    /// deduplicated from typed text, so the resolution to a collision is "choose another name", which
    /// <em>is</em> a correction to a member of the request and is exactly what a 400 carries.
    /// </para>
    /// <para>
    /// <b>A duplicate identifier is not a field anybody can correct either, and it is the third
    /// answer.</b> The id arrives minted by the caller, so a POST retried after a network timeout carries
    /// a byte-identical body and collides on <see cref="CategoryConfiguration.PrimaryKeyName"/> — which
    /// would otherwise answer 500 for the most ordinary thing an HTTP client does. Measured on
    /// postgres:17.10 for payees and not re-run here: a row violating both the key and the name index is
    /// reported under the key, because PostgreSQL checks a relation's indexes in OID (creation) order and
    /// the primary key is created with the table. That is why this arm is written first.
    /// </para>
    /// <para>
    /// <b>The route stays non-idempotent, deliberately.</b> Answering 200 with the row that already
    /// exists would mean deciding whether it is the same category — a comparison over AEAD envelopes this
    /// server cannot open, and it would still have to choose an answer for the case where the id matches
    /// and the name does not.
    /// </para>
    /// </remarks>
    public async Task AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        dbContext.Categories.Add(category);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // FIRST because it is the one PostgreSQL reports first, not because the runtime cares: the two
        // filters are mutually exclusive - a PostgresException carries exactly one ConstraintName - so
        // this order is documentation of the measurement, and reversing it changes no behaviour.
        //
        // Named for the reason the arm below is named, plus one that is sharper here: both arms catch the
        // SAME SQLSTATE from the same statement, so SQLSTATE alone cannot tell an id collision from a
        // name collision and whichever answer was written first would be given to both.
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, CategoryConfiguration.PrimaryKeyName))
        {
            dbContext.Entry(category).State = EntityState.Detached;
            throw DuplicateCategoryIdConflictException();
        }
        // Named, because SaveChanges flushes every tracked row and not just this category: only the
        // category name index says the blind index the client just computed is the one already taken.
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

    // The 409 this table can raise beside the 400 below. ConflictExceptionHandler renders this message as
    // ProblemDetails.Detail beside a Title fixed for every conflict in the product, and adds no extension
    // member, so this sentence is the whole of what the caller is told and has to say what they do next
    // by itself.
    //
    // It deliberately does not say "re-read your categories", which would be the duplicate-name
    // instruction and is the wrong one here: the row already wearing this id may carry a different name -
    // or sit in a budget the caller cannot read, in which case GET /api/categories/{id} answers 404 - so
    // a client sent to its list would look for a name that is not on it. What it does instead is name the
    // two readings the server cannot tell apart, a retry that already succeeded and an identifier reused
    // by mistake, and give each its own next step, because the CLIENT can tell them apart: it knows
    // whether it sent this body before.
    //
    // "Read it back" is an instruction rather than a promise, which keeps the sentence true in the
    // other-budget case where the honest conclusion is the second reading. It carries no SQLSTATE,
    // constraint name or database text.
    private static ConflictException DuplicateCategoryIdConflictException() => new(
        "A category already exists with this identifier. If this request is a retry, read that category "
        + "back by its identifier instead of posting it again; otherwise mint a fresh identifier and "
        + "post again.");

    // Keyed on Name because the remedy IS a correction to that member - see AddAsync for why this table
    // answers 400 where payees answers 409 to the collision on the equivalent index.
    //
    // The sentence still says "name" though the index is over name_key, and that is right: the caller
    // sent a name and an index computed from it by one piece of client code, and the member they can act
    // on is the name. Nothing on this side can say which two categories collided - that needs the
    // account's index key, which lives in a browser.
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
