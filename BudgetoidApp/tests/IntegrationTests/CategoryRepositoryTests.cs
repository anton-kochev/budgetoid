using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using TestSupport;

namespace IntegrationTests;

public sealed class CategoryRepositoryTests
{
    /// <summary>
    /// Minor unit of the USD accounts these tests seed. Precision is not what any of them is
    /// about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task DeleteCategoryGroup_WithCategory_TranslatesForeignKeyBackstop()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid categoryGroupId;
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId)))
        {
            CategoryGroup categoryGroup = CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Essentials"),
                null,
                0,
                UtcNow());
            db.CategoryGroups.Add(categoryGroup);
            db.Categories.Add(Category.Create(
                Guid.CreateVersion7(),
                budgetId,
                categoryGroup.Id,
                SealedNarrative.Indexed("Groceries"),
                null,
                0,
                UtcNow()));
            await db.SaveChangesAsync();
            categoryGroupId = categoryGroup.Id;
        }

        // Act
        await using BudgetoidDbContext deleteDb = new(options, new TestBudgetContext(budgetId));
        var repository = new CategoryGroupRepository(deleteDb);
        CategoryGroup categoryGroupToDelete = (await repository.GetByIdAsync(categoryGroupId))!;
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            repository.DeleteAsync(categoryGroupToDelete));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    [Test]
    public async Task DeleteCategory_WithTransaction_TranslatesForeignKeyBackstop()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid categoryId;
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId)))
        {
            Account account = Account.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Checking"),
                AccountType.Checking,
                0m,
                "USD",
                UsdMinorUnit,
                UtcNow());
            CategoryGroup categoryGroup = CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Essentials"),
                null,
                0,
                UtcNow());
            Category category = Category.Create(
                Guid.CreateVersion7(),
                budgetId,
                categoryGroup.Id,
                SealedNarrative.Indexed("Groceries"),
                null,
                0,
                UtcNow());
            db.AddRange(account, categoryGroup, category);
            await db.SaveChangesAsync();
            Transaction transaction = Transaction.Create(
                Guid.CreateVersion7(),
                budgetId,
                account.Id,
                -10m,
                UsdMinorUnit,
                new DateOnly(2026, 7, 14),
                SealedNarrative.Description("Food"),
                UtcNow());
            transaction.AssignCategory(category.Id);
            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
            categoryId = category.Id;
        }

        // Act
        await using BudgetoidDbContext deleteDb = new(options, new TestBudgetContext(budgetId));
        var repository = new CategoryRepository(deleteDb);
        Category categoryToDelete = (await repository.GetByIdAsync(categoryId))!;
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            repository.DeleteAsync(categoryToDelete));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    /// <summary>
    /// The account's own backstop: a delete the handler's transactions check let through — a
    /// transaction committed between the check and the delete — still answers as a validation error.
    /// </summary>
    /// <remarks>
    /// It lives beside the two category backstops because all three translate the same refusal: a
    /// <c>RESTRICT</c> foreign key, which PostgreSQL 18 reports as <c>23001</c> where 17 said
    /// <c>23503</c>. The handler's pre-check hides the account's case from every endpoint test, so
    /// only the repository can reach it.
    /// </remarks>
    [Test]
    public async Task DeleteAccount_WithTransaction_TranslatesForeignKeyBackstop()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid accountId;
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId)))
        {
            Account account = Account.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Checking"),
                AccountType.Checking,
                0m,
                "USD",
                UsdMinorUnit,
                UtcNow());
            db.Add(account);
            await db.SaveChangesAsync();
            db.Transactions.Add(Transaction.Create(
                Guid.CreateVersion7(),
                budgetId,
                account.Id,
                -10m,
                UsdMinorUnit,
                new DateOnly(2026, 7, 14),
                SealedNarrative.Description("Food"),
                UtcNow()));
            await db.SaveChangesAsync();
            accountId = account.Id;
        }

        // Act
        await using BudgetoidDbContext deleteDb = new(options, new TestBudgetContext(budgetId));
        var repository = new AccountRepository(deleteDb);
        Account accountToDelete = (await repository.GetByIdAsync(accountId))!;
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            repository.DeleteAsync(accountToDelete));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    private static async Task<ValidationException> ThrowsValidationExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }

    private static DbContextOptions<BudgetoidDbContext> CreateOptions(RepositoryTestHost host) =>
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options;

    private static DateTime UtcNow() =>
        new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
