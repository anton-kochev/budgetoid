using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

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
                budgetId,
                "Essentials",
                null,
                0,
                UtcNow());
            db.CategoryGroups.Add(categoryGroup);
            db.Categories.Add(Category.Create(
                budgetId,
                categoryGroup.Id,
                "Groceries",
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
                budgetId,
                "Checking",
                AccountType.Checking,
                0m,
                "USD",
                UsdMinorUnit,
                UtcNow());
            CategoryGroup categoryGroup = CategoryGroup.Create(
                budgetId,
                "Essentials",
                null,
                0,
                UtcNow());
            Category category = Category.Create(
                budgetId,
                categoryGroup.Id,
                "Groceries",
                null,
                0,
                UtcNow());
            db.AddRange(account, categoryGroup, category);
            await db.SaveChangesAsync();
            Transaction transaction = Transaction.Create(
                budgetId,
                account.Id,
                -10m,
                UsdMinorUnit,
                new DateOnly(2026, 7, 14),
                "Food",
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
