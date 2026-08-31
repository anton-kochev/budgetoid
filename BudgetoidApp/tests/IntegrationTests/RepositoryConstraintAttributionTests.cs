using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Payees;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Covers who a repository is allowed to speak for when PostgreSQL refuses a write.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mechanism every red test here uses.</b> <c>SaveChangesAsync()</c> flushes everything the
/// scoped <see cref="BudgetoidDbContext" /> is tracking, not only the entity the repository was
/// handed. So each of these tests tracks one extra, unrelated row that breaks a <i>different</i>
/// constraint carrying the <i>same</i> SQLSTATE, then calls the repository with a perfectly valid
/// entity of its own. The repository's <c>SaveChangesAsync</c> flushes both, the other constraint
/// fires, and a <c>catch</c> that matches on SQLSTATE alone cannot tell whose rule broke — so it
/// dresses a stranger's violation up in this repository's message. The harm is not the extra write;
/// it is the mis-attribution: a confident, specific, false 400.
/// </para>
/// <para>
/// The expected behaviour is that an unmatched violation <b>propagates</b> as a
/// <see cref="DbUpdateException" />. A 500 naming the constraint beats a 400 that lies, and that is
/// no longer one repository's local reasoning but the rule this folder holds every translating
/// repository to: <b>nine of the ten</b> filter on <see cref="PostgresException.ConstraintName" />.
/// <c>SessionRepository</c> is the tenth and translates nothing — its only <c>catch</c> is a bounded
/// concurrency retry. Note that the filter is not always visible at the <c>catch</c>:
/// <c>CategoryRepository</c>, <c>CategoryGroupRepository</c> and <c>UserRepository</c> spell it
/// inside an <c>IsUniqueViolationOf</c> / <c>IsForeignKeyViolationOf</c> helper while the rest write
/// a property pattern in the <c>when</c> clause. "Does this repository narrow" is a question about
/// the predicate, never about the syntax it is spelled in.
/// </para>
/// <para>
/// Each repository this file covers is covered from the other side too: it must still translate
/// <i>its own</i> constraint into its own message. Without that half, narrowing a <c>catch</c> could
/// be "fixed" by deleting it.
/// </para>
/// <para>
/// <b>What this file covers is the five repositories reachable through a budget</b> — accounts,
/// budgets, categories, category groups and payees. The identity-side repositories pin their own
/// narrowing beside the method, in their own files: <c>UserRepositoryTests</c>,
/// <c>PasskeyRepositoryTests</c> with <c>PasskeyCeremonyTests</c>,
/// <c>RecoveryCodeRepositoryTests</c>, <c>SessionRepositoryTests</c> and
/// <c>TransactionRepositoryTests</c>. That split is a placement decision, not a coverage claim, and
/// the sentence that used to be here — "every repository is also covered from the other side" — was
/// a completeness claim nothing executed, which is why it went stale twice unnoticed.
/// <c>RepositoryAttributionCensusTests</c> in <c>UnitTests</c> now executes it: it reflects over the
/// live <c>Infrastructure.Repositories</c> namespace and fails unless every repository in it appears
/// in exactly one of those two sets. Read its <c>PinnedElsewhere</c> entries rather than this
/// paragraph for which halves each of those files actually holds. Three of them used to pin the
/// translation and carry no mis-attribution control; each now carries one, written to the mechanism
/// this file's first paragraph describes and placed beside its method rather than moved in here.
/// </para>
/// <para>
/// None of this is reachable through today's handlers — <c>CreateTransactionHandler</c> commits the
/// payee before the transaction insert, and no other handler leaves two writes pending on one
/// context — which is exactly why the mechanism is built explicitly here. It is a trap for the next
/// handler that performs two writes on one context.
/// </para>
/// <para>
/// <c>TransactionRepository</c> is absent on purpose, and the reason is placement rather than
/// innocence. It does catch constraint violations — <c>UpdateAsync</c> has two, on the account and
/// category foreign keys, each filtered by name for exactly the reason this file argues — so it is
/// not the case that it has nothing to attribute. What earns the absence is that its narrowing is
/// pinned beside the method in <c>TransactionRepositoryTests</c>, as
/// <c>UserRepository.DeleteAsync</c>'s is in <c>UserRepositoryTests</c>. Its second narrowing is of a
/// different kind and could not be written here anyway:
/// <c>DeleteAllForAmbientBudgetAsync</c> answers a concurrency conflict, which carries no constraint
/// name and no SQLSTATE for a filter to mis-read, so the <i>entries</i> are what narrow it.
/// <b>The gap this paragraph used to record is closed</b>:
/// <c>UpdateAsync_WhenATrackedRowBreaksAnotherForeignKey_LetsTheViolationEscape</c> is its
/// mis-attribution control, staging the same payee-against-a-missing-budget <c>23503</c> that
/// <see cref="AddCategory_WhenATrackedRowBreaksAnotherForeignKey_LetsTheViolationEscape" /> stages
/// here — the same mechanism, in the file that owns the method.
/// </para>
/// </remarks>
public sealed class RepositoryConstraintAttributionTests
{
    /// <summary>
    /// Minor unit of the USD accounts these tests seed. Precision is not what any of them is about;
    /// the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    private const string AccountNameIndex = "IX_accounts_budget_id_name";
    private const string PayeeNameIndex = "IX_payees_budget_id_name";
    private const string UserEmailIndex = "IX_users_email";
    private const string PayeeBudgetForeignKey = "FK_payees_budgets_budget_id";

    [Test]
    public async Task AddAccount_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape()
    {
        // Arrange — the intruder is a duplicate payee name, which breaks IX_payees_budget_id_name
        // with the same 23505 the account name index would raise. The account itself is flawless:
        // "Checking" is the only account in this budget.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.Payees.Add(Payee.Create(budgetId, "Corner Shop", UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        db.Payees.Add(Payee.Create(budgetId, "Corner Shop", UtcNow()));
        var repository = new AccountRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(Account.Create(
            budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow())));

        // Assert — a payee collision must not come back as "Account name must be unique.". Nothing
        // about the account is wrong, so this repository has no message to offer and the violation
        // belongs to the caller, named.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(PayeeNameIndex);
    }

    [Test]
    public async Task AddAccount_WithADuplicateAccountName_TranslatesItsOwnUniqueIndex()
    {
        // Arrange — the collision this repository does model: two accounts, one budget, one name.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.Accounts.Add(Account.Create(
                budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        var repository = new AccountRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(Account.Create(
            budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow())));

        // Assert — narrowing the catch must not silence it. This is the half that keeps a fix from
        // passing by removing the translation altogether.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ValidationException>();
        await Assert.That(((ValidationException)escaped!).Errors.ContainsKey(nameof(Account.Name)))
            .IsTrue();
    }

    [Test]
    public async Task AddBudget_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape()
    {
        // Arrange — the intruder is a second user reusing a taken email, which breaks IX_users_email
        // with the same 23505 the budget name index would raise. It is not even a budget-owned row,
        // which is the point: the tracked graph is wider than the repository's subject.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");

        // No IBudgetContext: neither Budget nor User carries a BudgetIsolation query filter, so this
        // context never needs an ambient budget.
        await using BudgetoidDbContext db = new(CreateOptions(host));
        // No credential for this one: a user row without one is legal at the schema level, and the
        // subject here is the email index, not identity resolution.
        db.Users.Add(User.CreateWithId(Guid.CreateVersion7(), "person@example.com", UtcNow()));
        var repository = new BudgetRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.TryAddAsync(
            Budget.Create(
                Guid.CreateVersion7(), userId, SealedNarrative.Name("Household"), UtcNow())));

        // Assert — this one does not even lie out loud: TryAddAsync returns false, and provisioning
        // reads that as "someone else won the race, re-read the budget". There is no budget to
        // re-read, so a stranger's email collision turns into a failed sign-in with nothing logged.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(UserEmailIndex);
    }

    [Test]
    public async Task AddBudget_WithASecondNamelessBudget_ReportsItsOwnUniqueIndexAsALostRace()
    {
        // Arrange — the collision this repository does model: same owner, no name, which is the half
        // of IX_budgets_user_id_name that survived the column becoming ciphertext. It used to be
        // arranged as the same name twice, and that arrangement no longer collides with anything:
        // every seal draws a fresh nonce, so two rows a client sealed from one word hold different
        // bytes, and the case_insensitive collation that made "Household" meet "household" went with
        // the text type. NULLS NOT DISTINCT is untouched, and it is the same index and the same 23505
        // this test was always about.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = new(CreateOptions(host));
        var repository = new BudgetRepository(db);
        bool firstAdded = await repository.TryAddAsync(
            Budget.CreateDefault(Guid.CreateVersion7(), userId, UtcNow()));

        // Act
        bool secondAdded = await repository.TryAddAsync(
            Budget.CreateDefault(Guid.CreateVersion7(), userId, UtcNow()));

        // Assert
        await Assert.That(firstAdded).IsTrue();
        await Assert.That(secondAdded).IsFalse();
    }

    [Test]
    public async Task AddCategoryGroup_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape()
    {
        // Arrange — the intruder is a duplicate payee name again, breaking
        // IX_payees_budget_id_name with the 23505 that IX_category_groups_budget_id_name would also
        // raise. The group being added is the first one in this budget.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.Payees.Add(Payee.Create(budgetId, "Corner Shop", UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        db.Payees.Add(Payee.Create(budgetId, "Corner Shop", UtcNow()));
        var repository = new CategoryGroupRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(
            CategoryGroup.Create(budgetId, "Essentials", null, 0, UtcNow())));

        // Assert — "Category group name must be unique." would be a lie about a name nobody else
        // holds, and the client would render it against the name field the user just typed.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(PayeeNameIndex);
    }

    [Test]
    public async Task AddCategoryGroup_WithADuplicateGroupName_TranslatesItsOwnUniqueIndex()
    {
        // Arrange — the collision this repository does model.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.CategoryGroups.Add(CategoryGroup.Create(budgetId, "Essentials", null, 0, UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        var repository = new CategoryGroupRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(
            CategoryGroup.Create(budgetId, "essentials", null, 1, UtcNow())));

        // Assert
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ValidationException>();
        await Assert.That(((ValidationException)escaped!).Errors.ContainsKey(nameof(CategoryGroup.Name)))
            .IsTrue();
    }

    [Test]
    public async Task AddCategory_WhenATrackedRowBreaksAnotherForeignKey_LetsTheViolationEscape()
    {
        // Arrange — the sharpest case in the file. categories carries two foreign keys that both
        // raise 23503: the composite one to category_groups and budget_id to budgets. The intruder
        // here is a payee pointed at a budget that does not exist, so the 23503 comes from
        // FK_payees_budgets_budget_id while the category being added references a real group in its
        // own budget and is beyond reproach.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid categoryGroupId;
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            CategoryGroup categoryGroup = CategoryGroup.Create(budgetId, "Essentials", null, 0, UtcNow());
            seed.CategoryGroups.Add(categoryGroup);
            await seed.SaveChangesAsync();
            categoryGroupId = categoryGroup.Id;
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        db.Payees.Add(Payee.Create(Guid.CreateVersion7(), "Corner Shop", UtcNow()));
        var repository = new CategoryRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(
            Category.Create(budgetId, categoryGroupId, "Groceries", null, 0, UtcNow())));

        // Assert — reporting "Category group was not found." here would be a confident, specific,
        // false 400 about a group the test just created and can still read.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(PayeeBudgetForeignKey);
    }

    [Test]
    public async Task AddCategory_WithAnUnknownCategoryGroup_TranslatesItsOwnForeignKey()
    {
        // Arrange — the violation this repository does model: a group id that no row answers to, so
        // the composite FK_categories_category_groups_category_group_id_budget_id refuses the write.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = new(CreateOptions(host), new TestBudgetContext(budgetId));
        var repository = new CategoryRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(Category.Create(
            budgetId, Guid.CreateVersion7(), "Groceries", null, 0, UtcNow())));

        // Assert
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ValidationException>();
        await Assert.That(((ValidationException)escaped!).Errors.ContainsKey(nameof(Category.CategoryGroupId)))
            .IsTrue();
    }

    [Test]
    public async Task GetOrCreatePayee_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape()
    {
        // Arrange — the intruder is a duplicate account name, breaking IX_accounts_budget_id_name
        // with the same 23505 the payee name index would raise. No payee named "Corner Shop" exists,
        // so the repository's own insert is sound.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.Accounts.Add(Account.Create(
                budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        db.Accounts.Add(Account.Create(
            budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow()));
        var repository = new PayeeRepository(db, new TestBudgetContext(budgetId), TimeProvider.System);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.GetOrCreateAsync("Corner Shop"));

        // Assert — this catch does not throw a message, it swallows and re-reads, which is worse:
        // the re-read finds nothing (the transaction rolled back) and the account collision comes
        // out as "Payee unique violation occurred but no matching payee was found." — an
        // InvalidOperationException blaming payees for a row nobody asked this repository about.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(AccountNameIndex);
    }

    [Test]
    public async Task GetOrCreatePayee_WhenItsOwnInsertCollides_StillTakesItsOwnRecoveryPath()
    {
        // Arrange — the collision this repository does model, staged single-threaded. A duplicate
        // payee is tracked but not yet written, so the repository's lookup (which reads the
        // database, not the change tracker) misses it exactly as the losing side of the real race
        // does, and its own insert then collides on IX_payees_budget_id_name.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = new(CreateOptions(host), new TestBudgetContext(budgetId));
        db.Payees.Add(Payee.Create(budgetId, "Corner Shop", UtcNow()));
        var repository = new PayeeRepository(db, new TestBudgetContext(budgetId), TimeProvider.System);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.GetOrCreateAsync("Corner Shop"));

        // Assert — the recovery path runs: the 23505 is swallowed and the name re-read. Nothing was
        // committed, so it ends in the repository's own InvalidOperationException rather than a
        // returned payee. What this pins is that the catch still fires for the payee index, so a
        // narrower filter cannot be satisfied by deleting the catch.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<InvalidOperationException>();
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" />
    /// when nothing did. Deliberately untyped: the whole question these tests ask is <i>which</i>
    /// exception surfaces, so catching a specific one here would decide the answer in the helper.
    /// </summary>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Names the constraint PostgreSQL actually refused on, or <see langword="null" /> when the
    /// escaping exception never reached the database at all.
    /// </summary>
    private static string? ConstraintNameOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.ConstraintName
            : null;

    private static DbContextOptions<BudgetoidDbContext> CreateOptions(RepositoryTestHost host) =>
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options;

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static DateTime UtcNow() => new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
