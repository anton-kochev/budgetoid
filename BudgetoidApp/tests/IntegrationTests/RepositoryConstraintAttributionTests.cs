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
/// None of this is reachable through today's handlers, and the reason changed with the payee slice
/// rather than going away. It used to be that <c>CreateTransactionHandler</c> committed the payee
/// before the transaction insert; that handler writes no payee at all now — the client creates one
/// through <c>POST /api/payees</c> and names it by identifier — so no handler leaves two writes
/// pending on one context. That is exactly why the mechanism is built explicitly here. It is a trap
/// for the next handler that performs two writes on one context.
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

    /// <summary>
    /// The unique index a duplicate account name trips, spelled out here rather than read off
    /// <c>AccountConfiguration.NameIndexName</c>.
    /// </summary>
    /// <remarks>
    /// <b>A literal on purpose, unlike the constant the repository matches against.</b> Every case in
    /// this file is about a repository's <c>catch … when</c> attributing a violation to the wrong
    /// constraint, and the whole mechanism is a string comparison against a name PostgreSQL reports. A
    /// test that read the same constant the production filter reads would agree with the filter by
    /// construction and could never catch the two spellings drifting apart — which is the failure this
    /// file exists to make loud. The value moved once already, when the index moved from <c>name</c> to
    /// <c>name_key</c>; that move is a schema change a person edits here, having read why.
    /// </remarks>
    private const string AccountNameIndex = "IX_accounts_budget_id_name_key";

    /// <summary>
    /// The unique index a duplicate payee name trips, spelled out here for the reason
    /// <see cref="AccountNameIndex" /> is.
    /// </summary>
    /// <remarks>
    /// It moved from <c>name</c> to <c>name_key</c> when the column became an AEAD envelope: a name is
    /// ciphertext drawn under a fresh nonce, so equal names are unequal bytes and the old index could
    /// no longer see a duplicate at all. What a duplicate is now is an equal <b>blind index</b>, which
    /// is why every seeder below collides two payees by handing them the same
    /// <see cref="SealedNarrative.Indexed" /> label rather than the same word.
    /// </remarks>
    private const string PayeeNameIndex = "IX_payees_budget_id_name_key";
    private const string UserEmailIndex = "IX_users_email";
    private const string PayeeBudgetForeignKey = "FK_payees_budgets_budget_id";

    [Test]
    public async Task AddAccount_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape()
    {
        // Arrange — the intruder is a duplicate payee blind index, which breaks
        // IX_payees_budget_id_name_key with the same 23505 the account name index would raise. The
        // account itself is flawless: "Checking" is the only account in this budget.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.Payees.Add(Payee.Create(
                Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed("Corner Shop"), UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        db.Payees.Add(Payee.Create(
            Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed("Corner Shop"), UtcNow()));
        var repository = new AccountRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow())));

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
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        var repository = new AccountRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow())));

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
        // Arrange — the intruder is a duplicate payee blind index again, breaking
        // IX_payees_budget_id_name_key with the 23505 that IX_category_groups_budget_id_name would
        // also raise. The group being added is the first one in this budget.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.Payees.Add(Payee.Create(
                Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed("Corner Shop"), UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        db.Payees.Add(Payee.Create(
            Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed("Corner Shop"), UtcNow()));
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
        // Named rather than inlined: the payee takes two identifiers now and only the second is the
        // broken rule — the row's own id is fine and its budget names nothing.
        Guid budgetThatWasNeverCreated = Guid.CreateVersion7();
        db.Payees.Add(Payee.Create(
            Guid.CreateVersion7(),
            budgetThatWasNeverCreated,
            SealedNarrative.Indexed("Corner Shop"),
            UtcNow()));
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

    /// <summary>
    /// <c>PayeeRepository.AddAsync</c> replaced <c>GetOrCreateAsync</c>, and this pair replaced the two
    /// cases that covered it — the same two halves, against the member that exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old pair could only be rewritten, not repointed: <c>GetOrCreateAsync</c> took a name, and a
    /// lookup by name is no longer a question this side can answer. Its recovery path — swallow the
    /// 23505, re-read the name, hand back whoever won the race — went with it, so the half that used to
    /// end in an <c>InvalidOperationException</c> now ends in a <c>ConflictException</c> the client is
    /// asked to resolve by re-reading its own list.
    /// </para>
    /// <para>
    /// <b>Both halves have to survive together or the census in <c>UnitTests</c> becomes a lie.</b>
    /// <c>RepositoryAttributionCensusTests</c> lists <c>PayeeRepository</c> in
    /// <c>CoveredByAttributionTests</c>, whose definition is "both halves, here" — drop either one and
    /// that entry goes on passing while claiming coverage this file no longer holds.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddPayee_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape()
    {
        // Arrange — the intruder is a duplicate account name, breaking
        // IX_accounts_budget_id_name_key with the same 23505 the payee name index would raise. The
        // payee being added is the only one in this budget, so its own insert is sound.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.Accounts.Add(Account.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        db.Accounts.Add(Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow()));
        var repository = new PayeeRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(Payee.Create(
            Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed("Corner Shop"), UtcNow())));

        // Assert — an account collision must not come back as the payee conflict, which tells a client
        // its payee list is stale and asks it to re-read: a confident instruction about a list that has
        // nothing to do with the row PostgreSQL refused.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(AccountNameIndex);
    }

    [Test]
    public async Task AddPayee_WithADuplicateBlindIndex_TranslatesItsOwnUniqueIndex()
    {
        // Arrange — the collision this repository does model: two payees, one budget, one blind index.
        // Equal labels are what make the two indexes equal; the envelopes beside them differ anyway,
        // because every seal draws a fresh nonce.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.Payees.Add(Payee.Create(
                Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed("Corner Shop"), UtcNow()));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId));
        var repository = new PayeeRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(Payee.Create(
            Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed("Corner Shop"), UtcNow())));

        // Assert — narrowing the catch must not silence it. A create's remedy is to adopt the row that
        // already exists, which is not a correction to any field, so this half is a ConflictException
        // and not the ValidationException UpdateAsync raises on the very same index.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ConflictException>();
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
