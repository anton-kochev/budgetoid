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
    /// <summary>
    /// Inserts a category group the caller minted, sealed and indexed, answering a duplicate name with a
    /// 400 keyed on the name and a duplicate identifier with a 409.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two statuses for one SQLSTATE on one table, and the split is between the two CONSTRAINTS rather
    /// than between create and update.</b> This is <c>AccountRepository.AddAsync</c>'s shape and
    /// deliberately not <c>PayeeRepository.AddAsync</c>'s, which answers <b>409</b> to a duplicate name
    /// on its own create. A reviewer will read that as drift between three tables and try to harmonise
    /// it; the constraint is one and the REMEDIES are two, and the status follows the remedy.
    /// </para>
    /// <para>
    /// A payee create collides because the client's decrypted list was stale, and the resolution is to
    /// adopt the row that already exists — which is not a field anybody can edit, so there is nothing for
    /// a validation problem document to be keyed on. A category group is not deduplicated from typed
    /// text: a person deliberately names a group, and the resolution to a collision is "choose another
    /// name", which <em>is</em> a correction to a member of the request and is exactly what a 400
    /// carries.
    /// </para>
    /// <para>
    /// <b>A duplicate identifier is not a field anybody can correct either, and it is the third
    /// answer.</b> The id arrives minted by the caller, so a POST retried after a network timeout carries
    /// a byte-identical body and collides on <see cref="CategoryGroupConfiguration.PrimaryKeyName"/> —
    /// which would otherwise answer 500 for the most ordinary thing an HTTP client does. Measured on
    /// postgres:17.10 for payees and not re-run here: a row violating both the key and the name index is
    /// reported under the key, which is why this arm is written first.
    /// </para>
    /// <para>
    /// <b>The route stays non-idempotent, deliberately.</b> Answering 200 with the row that already
    /// exists would mean deciding whether it is the same group — a comparison over AEAD envelopes this
    /// server cannot open, and it would still have to choose an answer for the case where the id matches
    /// and the name does not.
    /// </para>
    /// </remarks>
    public async Task AddAsync(
        CategoryGroup categoryGroup,
        CancellationToken cancellationToken = default)
    {
        dbContext.CategoryGroups.Add(categoryGroup);
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
            when (IsUniqueViolationOf(exception, CategoryGroupConfiguration.PrimaryKeyName))
        {
            dbContext.Entry(categoryGroup).State = EntityState.Detached;
            throw DuplicateCategoryGroupIdConflictException();
        }
        // Named, because SaveChanges flushes every tracked row and not just this group: only the group
        // name index says the blind index the client just computed is the one already taken.
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

    // The 409 this table can raise beside the 400 above. ConflictExceptionHandler renders this message as
    // ProblemDetails.Detail beside a Title fixed for every conflict in the product, and adds no extension
    // member, so this sentence is the whole of what the caller is told and has to say what they do next
    // by itself.
    //
    // It deliberately does not say "re-read your category groups", which would be the duplicate-name
    // instruction and is the wrong one here: the row already wearing this id may carry a different name -
    // or sit in a budget the caller cannot read, in which case GET /api/category-groups/{id} answers 404
    // - so a client sent to its list would look for a name that is not on it. What it does instead is
    // name the two readings the server cannot tell apart, a retry that already succeeded and an
    // identifier reused by mistake, and give each its own next step, because the CLIENT can tell them
    // apart: it knows whether it sent this body before.
    //
    // "Read it back" is an instruction rather than a promise, which keeps the sentence true in the
    // other-budget case where the honest conclusion is the second reading. It carries no SQLSTATE,
    // constraint name or database text: a caller learns what to do and nothing about the schema that
    // refused it.
    private static ConflictException DuplicateCategoryGroupIdConflictException() => new(
        "A category group already exists with this identifier. If this request is a retry, read that "
        + "group back by its identifier instead of posting it again; otherwise mint a fresh identifier "
        + "and post again.");

    // Keyed on Name because the remedy IS a correction to that member - see AddAsync for why this table
    // answers 400 where payees answers 409 to the collision on the equivalent index.
    //
    // The sentence still says "name" though the index is over name_key, and that is right: the caller
    // sent a name and an index computed from it by one piece of client code, and the member they can act
    // on is the name. Nothing on this side can say which two groups collided - that needs the account's
    // index key, which lives in a browser.
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
