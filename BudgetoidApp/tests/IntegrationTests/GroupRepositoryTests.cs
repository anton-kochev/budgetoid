using Domain.Accounts;
using Domain.Common;
using Domain.Groups;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests;

public sealed class GroupRepositoryTests
{
    [Test]
    public async Task DeleteAsync_WithLinkedTransaction_ThrowsValidationExceptionViaForeignKeyBackstop()
    {
        // Arrange — a group referenced by a transaction. This deletes the group directly through the
        // repository, bypassing DeleteGroupHandler's HasTransactionsAsync guard, so it exercises the
        // database FK Restrict backstop (the TOCTOU race path) rather than the app-level check.
        await using RepositoryTestHost host = await StartHostAsync();
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid groupId;

        await using (BudgetoidDbContext db = new(options, new TestUserContext(userId)))
        {
            Account account = Account.Create(userId, "Checking", AccountType.Checking, 0m, "USD", UtcNow());
            db.Accounts.Add(account);
            Group group = Group.Create(userId, "Groceries", null, UtcNow());
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;

            Transaction transaction = Transaction.Create(userId, account.Id, -10m, new DateOnly(2026, 7, 3), "Coffee", UtcNow());
            transaction.AssignGroup(group.Id);
            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
        }

        // Act
        await using BudgetoidDbContext deleteDb = new(options, new TestUserContext(userId));
        var repository = new GroupRepository(deleteDb);
        Group toDelete = (await repository.GetByIdAsync(groupId))!;

        try
        {
            await repository.DeleteAsync(toDelete);
        }
        catch (ValidationException exception)
        {
            // Assert
            await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
            return;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }

    private static DateTime UtcNow() => new(2026, 7, 3, 13, 14, 15, DateTimeKind.Utc);

    private static DbContextOptions<BudgetoidDbContext> CreateOptions(RepositoryTestHost host)
    {
        return new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options;
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
