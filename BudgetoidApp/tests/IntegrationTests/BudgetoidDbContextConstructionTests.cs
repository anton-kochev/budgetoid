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
    public async Task Model_BoundsEveryValueRangeTheDatabaseCanCheck()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act — the design-time model for the same reason the collation assertions use it: the
        // runtime read-optimized model drops everything only migrations consume, and a check
        // constraint is exactly that.
        string[] checkConstraints = db
            .GetService<IDesignTimeModel>()
            .Model
            .GetEntityTypes()
            .SelectMany(entity => entity.GetCheckConstraints())
            .Select(constraint =>
                $"{constraint.Name}: {constraint.EntityType.GetTableName()} {constraint.Sql}")
            .ToArray();

        // Assert — one set equality rather than seven existence checks, because the set also fails
        // on a constraint nobody meant to add. Each row is a range the domain already refuses and
        // that a raw INSERT walks straight past today. The magnitude bound repeats the domain
        // literal (Account.cs, Transaction.cs: Math.Abs(x) > 1_000_000_000m), so 1000000000 itself
        // stays legal on both sides and only 1000000000.01 is out.
        string[] expected =
        [
            "CK_accounts_opening_balance: accounts abs(opening_balance) <= 1000000000",
            "CK_accounts_type: accounts type in ('Checking', 'Savings', 'Cash', 'CreditCard')",
            "CK_categories_position: categories position >= 0",
            "CK_category_groups_position: category_groups position >= 0",
            // The issuer vocabulary, bounded the way the type vocabulary below it is: neither the
            // column nor UserRepository's lookup folds case, so without this 'Google' and 'google'
            // are two accounts for one person.
            "CK_credentials_provider: credentials provider is null or provider in ('google')",
            "CK_credentials_type: credentials type in ('passkey', 'federated')",
            // The shape check is what makes "a credential is exactly one type" a database rule: a
            // federated row with no issuer, or a passkey row carrying one, is rejected rather than
            // stored. The length test sits alongside the null test rather than replacing it —
            // length(null) is null and a check evaluating to null is satisfied.
            "CK_credentials_type_shape: credentials (type = 'federated' and provider is not null and "
            + "subject is not null and length(subject) > 0) or (type = 'passkey' and provider is null "
            + "and subject is null)",
            "CK_currencies_code: currencies code ~ '^[A-Z]{3}$'",
            "CK_currencies_minor_unit: currencies minor_unit between 0 and 4",
            "CK_transactions_amount: transactions abs(amount) <= 1000000000",
        ];
        await Assert.That(checkConstraints).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Migrations_KeepTheBaselineFrozen()
    {
        // Arrange — the id is spelled out here on purpose, and reading it off the migrations
        // directory instead would defeat the test: an id derived from the files agrees with whatever
        // file is present, including the replacement, so the assertion could never fail. Production's
        // __EFMigrationsHistory names this exact string, and the deploy pipeline applies migrations
        // unattended on every push to main, so a regenerated baseline arrives under a new id, the
        // next push finds nothing applied, and it re-creates every table against a populated
        // database. Having to edit this line is the checkpoint the retired manual deploy step was.
        const string frozenBaselineId = "20260804230129_InitialCreate";
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IReadOnlyList<string> migrations = db.Database.GetMigrations().ToList();
        bool inApplyOrder = migrations.SequenceEqual(
            migrations.OrderBy(migration => migration, StringComparer.Ordinal));

        // Assert — the count is deliberately not pinned. Schema changes are additive migrations from
        // here on (docs/engineering/migrations.md; ADR 0006), so a second and a tenth id are
        // both legal and only the first is frozen. That inversion is what makes this a stronger guard
        // than the count it replaces: counting one migration failed on a diffed baseline, which is
        // now the wanted thing, and stayed green through a regenerated one, which is the dangerous
        // thing — regeneration keeps the count at one and changes nothing but the id.
        // The ordering assertion is what turns "first" into "earliest", so no separate check that
        // nothing sorts ahead of the baseline is needed: GetMigrations returns ids in the order they
        // apply, which for timestamp-prefixed ids is ordinal sort order, so a back-dated migration
        // lands at index 0 and fails the id assertion rather than hiding behind it. The claim is
        // asserted rather than assumed because everything below rests on it.
        await Assert.That(migrations).IsNotEmpty();
        await Assert.That(inApplyOrder)
            .IsTrue()
            .Because("GetMigrations stopped returning ids in apply order, so the first element is "
                     + "no longer necessarily the baseline");
        await Assert.That(migrations[0])
            .IsEqualTo(frozenBaselineId)
            .Because("the baseline is frozen; add a migration instead of regenerating InitialCreate");
    }

    [Test]
    public async Task Migrations_MatchTheModel()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act — synchronous by necessity: EF Core 10 ships no async overload of this. No live
        // server is contacted either, because the check diffs the migrations assembly snapshot
        // against the design-time model and never opens the connection it builds.
        bool pending = db.Database.HasPendingModelChanges();

        // Assert — every other test in this class reads db.Model, which is the configuration code,
        // and those stay green when the migration disagrees with it. Drift is not silent elsewhere,
        // though: MigrateAsync refuses to migrate a drifted model, so every container-backed test in
        // the suite fails on PendingModelChangesWarning. This test earns its place by being the
        // cheap, legible version of that — no Docker, no container wait, and a message naming
        // regeneration as the fix rather than dozens of identical warnings on unrelated tests.
        await Assert.That(pending)
            .IsFalse()
            .Because("the model drifted from the migration; regenerate InitialCreate");
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
        // The rule, stated once: the runtime read-optimized model drops everything only migrations
        // consume, so an annotation-backed getter read off db.Model answers null instead of the
        // configured value — and null quietly satisfies neither IsTrue nor IsFalse. Collation and
        // NULLS NOT DISTINCT are both in that group, so both come from the design-time model.
        IEntityType designTimeBudgetEntity = db
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(Budget))!;
        IProperty designTimeNameProperty = designTimeBudgetEntity.FindProperty(nameof(Budget.Name))!;
        IIndex designTimeOwnerNameIndex = designTimeBudgetEntity
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Budget.UserId), nameof(Budget.Name) }));

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

        // Assert — the name is nullable because the budget created at provisioning has no name, and
        // the index must be NULLS NOT DISTINCT because of it. PostgreSQL's default treats every NULL
        // as distinct, so without the opt-out two concurrent provisioning requests would each insert
        // a (user_id, NULL) row and the user would end up owning two budgets. Making two unnamed
        // budgets collide is what replaces the shared literal name the racers used to collide on —
        // and it states the real invariant: at most one unnamed budget per user, any number of named
        // ones.
        // GetAreNullsDistinct is worded the way PostgreSQL words the option, not the way the rule
        // reads: it answers false exactly when the index is NULLS NOT DISTINCT. IsFalse below is
        // therefore the assertion that pins the opt-out, and flipping it to IsTrue would assert the
        // default this test exists to refuse.
        await Assert.That(ownerNameIndex.IsUnique).IsTrue();
        await Assert.That(designTimeOwnerNameIndex.GetAreNullsDistinct()).IsFalse();
        await Assert.That(nameProperty.IsNullable).IsTrue();
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
