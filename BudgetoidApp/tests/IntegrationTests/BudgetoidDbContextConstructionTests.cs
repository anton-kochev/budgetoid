using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Currencies;
using Domain.Transactions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    [Test]
    public async Task Model_RequiresCategoryToReferenceCategoryGroupOwnedBySameUser()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType categoryEntity = db.Model.FindEntityType(typeof(Category))!;
        IForeignKey categoryGroupForeignKey = categoryEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(CategoryGroup));

        // Assert
        await Assert.That(categoryGroupForeignKey.Properties.Select(property => property.Name).ToArray())
            .IsEquivalentTo(new[] { nameof(Category.CategoryGroupId), nameof(Category.UserId) });
        await Assert.That(categoryGroupForeignKey.IsRequired).IsTrue();
        await Assert.That(categoryGroupForeignKey.DeleteBehavior).IsEqualTo(DeleteBehavior.Restrict);
    }

    [Test]
    public async Task Model_ScopesBudgetToItsOwningUser()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType budgetEntity = db.Model.FindEntityType(typeof(Budget))!;
        IForeignKey userForeignKey = budgetEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(User));

        // Assert
        await Assert.That(userForeignKey.Properties.Single().Name).IsEqualTo(nameof(Budget.UserId));
        await Assert.That(userForeignKey.IsRequired).IsTrue();
        await Assert.That(userForeignKey.DeleteBehavior).IsEqualTo(DeleteBehavior.Cascade);
    }

    [Test]
    public async Task Model_ScopesBudgetNameUniquenessToTheOwner()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType budgetEntity = db.Model.FindEntityType(typeof(Budget))!;
        IIndex ownerNameIndex = budgetEntity
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Budget.UserId), nameof(Budget.Name) }));
        IProperty nameProperty = budgetEntity.FindProperty(nameof(Budget.Name))!;
        // Collation is not carried by the runtime read-optimized model, only by the design-time one.
        IProperty designTimeNameProperty = db
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(Budget))!
            .FindProperty(nameof(Budget.Name))!;

        // The schema is multi-budget-ready from day one: "exactly one budget per user" is a
        // release-scope property (no code path creates a second one), never a schema invariant, so a
        // unique constraint over the owner alone would have to be dropped by the multi-budget release.
        bool constrainsOwnerAlone =
            budgetEntity.GetIndexes().Any(index =>
                index.IsUnique
                && index.Properties.Count == 1
                && index.Properties[0].Name == nameof(Budget.UserId))
            || budgetEntity.GetKeys().Any(key =>
                key.Properties.Count == 1
                && key.Properties[0].Name == nameof(Budget.UserId));

        // Assert
        await Assert.That(ownerNameIndex.IsUnique).IsTrue();
        await Assert.That(nameProperty.IsNullable).IsFalse();
        await Assert.That(nameProperty.GetMaxLength()).IsEqualTo(200);
        await Assert.That(designTimeNameProperty.GetCollation()).IsEqualTo("case_insensitive");
        await Assert.That(constrainsOwnerAlone).IsFalse();
    }

    [Test]
    public async Task Model_AllowsABudgetWithoutABaseCurrency()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType budgetEntity = db.Model.FindEntityType(typeof(Budget))!;
        IProperty baseCurrencyProperty = budgetEntity.FindProperty(nameof(Budget.BaseCurrencyCode))!;
        IForeignKey currencyForeignKey = budgetEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Currency));

        // Assert
        await Assert.That(baseCurrencyProperty.IsNullable).IsTrue();
        await Assert.That(baseCurrencyProperty.GetMaxLength()).IsEqualTo(3);
        await Assert.That(currencyForeignKey.Properties.Single().Name).IsEqualTo(nameof(Budget.BaseCurrencyCode));
        await Assert.That(currencyForeignKey.IsRequired).IsFalse();
        await Assert.That(currencyForeignKey.DeleteBehavior).IsEqualTo(DeleteBehavior.Restrict);
    }

    [Test]
    public async Task Model_ContainsIsoCurrencySeedDataForFreshBaseline()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IReadOnlyList<IDictionary<string, object?>> seeds = db
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(Currency))!
            .GetSeedData()
            .ToList();

        // Assert
        await Assert.That(seeds.Count).IsEqualTo(13);
        await Assert.That(seeds.Any(seed => (string)seed[nameof(Currency.Code)]! == "USD")).IsTrue();
    }

    private static BudgetoidDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres")
            .Options;

        return new BudgetoidDbContext(options);
    }
}
