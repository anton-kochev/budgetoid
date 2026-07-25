using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Currencies;
using Domain.Payees;
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
    public async Task Model_CanBeBuiltWithoutAResolvedBudget()
    {
        await using BudgetoidDbContext db = CreateDbContext();

        await Assert.That(db.Model.FindEntityType(typeof(Transaction))).IsNotNull();
        await Assert.That(db.Model.FindEntityType(typeof(User))).IsNotNull();
    }

    [Test]
    [Arguments(typeof(Account))]
    [Arguments(typeof(CategoryGroup))]
    [Arguments(typeof(Category))]
    [Arguments(typeof(Payee))]
    [Arguments(typeof(Transaction))]
    public async Task Model_ScopesOwnedEntitiesToTheirBudget(Type entityClrType)
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType entity = db.Model.FindEntityType(entityClrType)!;
        IForeignKey budgetForeignKey = entity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Budget)
                                  && foreignKey.Properties.Count == 1);
        IProperty? survivingUserId = entity.FindProperty(RemovedOwnerPropertyName);

        // Assert
        await Assert.That(budgetForeignKey.Properties.Single().Name).IsEqualTo("BudgetId");
        await Assert.That(budgetForeignKey.IsRequired).IsTrue();

        // The owner link is dropped, not duplicated: a surviving UserId would be a second source of
        // truth for tenancy that no query reads, and the one state where a cross-tenant bug can hide.
        await Assert.That(survivingUserId).IsNull();
        await Assert.That(entity.GetForeignKeys()
                .Any(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(User)))
            .IsFalse();
    }

    [Test]
    [Arguments(typeof(Account), DeleteBehavior.Cascade)]
    [Arguments(typeof(CategoryGroup), DeleteBehavior.Cascade)]
    [Arguments(typeof(Category), DeleteBehavior.Cascade)]
    [Arguments(typeof(Payee), DeleteBehavior.Cascade)]
    // Transaction is the one asymmetric row, and it is deliberate: a budget that holds recorded
    // money movement must refuse deletion outright, while a budget with only structure and no
    // movement was created by mistake and takes its structure with it. Do not "normalize" this row
    // to Cascade — doing so silently turns a refused delete into an erased ledger.
    [Arguments(typeof(Transaction), DeleteBehavior.Restrict)]
    public async Task Model_RemovesOwnedRowsWithTheirBudgetExceptTransactions(
        Type entityClrType,
        DeleteBehavior expectedDeleteBehavior)
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType entity = db.Model.FindEntityType(entityClrType)!;
        IForeignKey budgetForeignKey = entity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Budget)
                                  && foreignKey.Properties.Count == 1);

        // Assert — the table above IS the policy; each row states what deleting a budget does to
        // that kind of owned row.
        await Assert.That(budgetForeignKey.DeleteBehavior).IsEqualTo(expectedDeleteBehavior);
    }

    [Test]
    public async Task Model_RequiresCategoryToReferenceACategoryGroupInTheSameBudget()
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
            .IsEquivalentTo(new[] { nameof(Category.CategoryGroupId), "BudgetId" });
        await Assert.That(categoryGroupForeignKey.PrincipalKey.Properties
                .Select(property => property.Name).ToArray())
            .IsEquivalentTo(new[] { nameof(CategoryGroup.Id), "BudgetId" });
        await Assert.That(categoryGroupForeignKey.IsRequired).IsTrue();
        await Assert.That(categoryGroupForeignKey.DeleteBehavior).IsEqualTo(DeleteBehavior.Restrict);
    }

    [Test]
    [Arguments(typeof(Account), "AccountId")]
    [Arguments(typeof(Category), "CategoryId")]
    [Arguments(typeof(Payee), "PayeeId")]
    public async Task Model_RequiresTransactionReferencesToStayInTheSameBudget(
        Type principalClrType,
        string referencePropertyName)
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType transactionEntity = db.Model.FindEntityType(typeof(Transaction))!;
        IForeignKey referenceForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == principalClrType);

        // Assert — a single-column reference lets the database hold a transaction that points at a
        // row in another budget; only the composite key ties the reference to the ambient tenant.
        await Assert.That(referenceForeignKey.Properties.Select(property => property.Name).ToArray())
            .IsEquivalentTo(new[] { referencePropertyName, "BudgetId" });
        await Assert.That(referenceForeignKey.PrincipalKey.Properties
                .Select(property => property.Name).ToArray())
            .IsEquivalentTo(new[] { "Id", "BudgetId" });
        // SET NULL is unreachable for a composite key whose BudgetId column is NOT NULL, so every
        // one of these references deletes under Restrict.
        await Assert.That(referenceForeignKey.DeleteBehavior).IsEqualTo(DeleteBehavior.Restrict);
    }

    [Test]
    public async Task Model_KeepsOptionalTransactionReferencesOptional()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType transactionEntity = db.Model.FindEntityType(typeof(Transaction))!;
        IProperty budgetIdProperty = transactionEntity.FindProperty("BudgetId")!;
        IProperty accountIdProperty = transactionEntity.FindProperty(nameof(Transaction.AccountId))!;
        IProperty payeeIdProperty = transactionEntity.FindProperty(nameof(Transaction.PayeeId))!;
        IProperty categoryIdProperty = transactionEntity.FindProperty(nameof(Transaction.CategoryId))!;
        IForeignKey budgetForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Budget));
        IForeignKey accountForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Account));
        IForeignKey payeeForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Payee));
        IForeignKey categoryForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Category));

        // Assert — this pins an absence of change, and the trap it guards is silent: calling
        // .IsRequired(false) on a composite foreign key makes *all* of its properties nullable,
        // including the BudgetId tenancy column, with no compiler error and no migration warning.
        // Optionality belongs to payee_id and category_id alone.
        await Assert.That(payeeIdProperty.IsNullable).IsTrue();
        await Assert.That(categoryIdProperty.IsNullable).IsTrue();
        await Assert.That(budgetIdProperty.IsNullable).IsFalse();
        await Assert.That(accountIdProperty.IsNullable).IsFalse();
        await Assert.That(payeeForeignKey.IsRequired).IsFalse();
        await Assert.That(categoryForeignKey.IsRequired).IsFalse();
        await Assert.That(budgetForeignKey.IsRequired).IsTrue();
        await Assert.That(accountForeignKey.IsRequired).IsTrue();
    }

    [Test]
    public async Task Model_IndexesTransactionReferencesWithTheBudget()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();
        string[] referencePropertyNames =
        [
            nameof(Transaction.AccountId),
            nameof(Transaction.CategoryId),
            nameof(Transaction.PayeeId),
        ];

        // Act
        IEntityType transactionEntity = db.Model.FindEntityType(typeof(Transaction))!;
        string[][] indexes = transactionEntity
            .GetIndexes()
            .Select(index => index.Properties.Select(property => property.Name).ToArray())
            .ToArray();

        // Assert — EF's foreign-key index convention covers the composite pairs, so the three
        // single-column indexes are dead weight the composite ones already serve as a prefix.
        foreach (string referencePropertyName in referencePropertyNames)
        {
            await Assert.That(indexes.Any(index =>
                    index.SequenceEqual([referencePropertyName, "BudgetId"])))
                .IsTrue();
            await Assert.That(indexes.Any(index => index.SequenceEqual([referencePropertyName])))
                .IsFalse();
        }

        // The list query's covering index is untouched by the re-keying.
        await Assert.That(indexes.Any(index => index.SequenceEqual(
                ["BudgetId", nameof(Transaction.Date), nameof(Transaction.CreatedAtUtc)])))
            .IsTrue();
    }

    [Test]
    [Arguments(typeof(Account))]
    [Arguments(typeof(CategoryGroup))]
    [Arguments(typeof(Category))]
    [Arguments(typeof(Payee))]
    public async Task Model_ScopesNameUniquenessToTheBudget(Type entityClrType)
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType entity = db.Model.FindEntityType(entityClrType)!;
        IIndex budgetNameIndex = entity
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "BudgetId", "Name" }));
        // Collation is not carried by the runtime read-optimized model, only by the design-time one.
        IProperty designTimeNameProperty = db
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(entityClrType)!
            .FindProperty("Name")!;

        // Assert
        await Assert.That(budgetNameIndex.IsUnique).IsTrue();
        await Assert.That(designTimeNameProperty.GetCollation()).IsEqualTo("case_insensitive");
    }

    [Test]
    public async Task Model_OrdersCategoryGroupsPerBudgetAndCategoriesPerGroup()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType categoryGroupEntity = db.Model.FindEntityType(typeof(CategoryGroup))!;
        IEntityType categoryEntity = db.Model.FindEntityType(typeof(Category))!;

        bool groupsOrderPerBudget = categoryGroupEntity.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "BudgetId", nameof(CategoryGroup.Position) }));
        // Categories stay group-scoped: groups are budget-scoped, so per-budget ordering holds
        // transitively and this index deliberately does not mention the budget.
        bool categoriesOrderPerGroup = categoryEntity.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Category.CategoryGroupId), nameof(Category.Position) }));

        // Assert
        await Assert.That(groupsOrderPerBudget).IsTrue();
        await Assert.That(categoriesOrderPerGroup).IsTrue();
    }

    [Test]
    public async Task Migrations_ContainASingleFreshBaseline()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IReadOnlyList<string> migrations = db.Database.GetMigrations().ToList();

        // Assert — CON-002: dev data is dropped, so the schema ships as one regenerated
        // InitialCreate. A second migration here means the baseline was diffed, not regenerated.
        await Assert.That(migrations.Count).IsEqualTo(1);
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

    /// <summary>
    /// The property name the re-scope removes. Spelled as a string on purpose: <c>nameof</c> would
    /// stop compiling once the property is gone, and the point of the assertion is that it is gone.
    /// </summary>
    private const string RemovedOwnerPropertyName = "UserId";

    private static BudgetoidDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres")
            .Options;

        // The IBudgetContext parameter stays optional so the model can be built without a resolved
        // tenant — design-time tooling, seeding, and these tests all rely on that.
        return new BudgetoidDbContext(options);
    }
}
