using Domain.Transactions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IntegrationTests;

public sealed class BudgetoidDbContextConstructionTests
{
    [Test]
    public async Task Model_CanBeBuiltWithoutAResolvedCurrentUser()
    {
        await using BudgetoidDbContext db = CreateDbContext();

        await Assert.That(db.Model.FindEntityType(typeof(Transaction))).IsNotNull();
        await Assert.That(db.Model.FindEntityType(typeof(User))).IsNotNull();
    }

    [Test]
    public async Task Model_ConfiguresTransactionUserForeignKey()
    {
        await using BudgetoidDbContext db = CreateDbContext();

        IEntityType transactionEntity = db.Model.FindEntityType(typeof(Transaction))!;
        IForeignKey? userForeignKey = transactionEntity
            .GetForeignKeys()
            .SingleOrDefault(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(User));

        await Assert.That(userForeignKey).IsNotNull();
        await Assert.That(userForeignKey!.Properties.Single().Name).IsEqualTo(nameof(Transaction.UserId));
    }

    private static BudgetoidDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres")
            .Options;

        return new BudgetoidDbContext(options);
    }
}
