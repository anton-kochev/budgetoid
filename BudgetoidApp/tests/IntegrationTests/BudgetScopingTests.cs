using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Real-PostgreSQL behaviour for the rules the budget re-scope moves off the user — name uniqueness
/// and ordering are per budget — and, since the four name columns were sealed, for <em>who</em>
/// refuses a duplicate.
/// </summary>
/// <remarks>
/// <para>
/// <b>This remark used to argue that three of the four sealed-name tables needed no end-to-end case,
/// and that argument is deleted rather than softened.</b> It rested on two claims. The first was that
/// <c>Model_ScopesNameUniquenessToTheBudgetOverTheBlindIndex</c> covers them — it does cover what it
/// covers, but a model test reads a <c>HasIndex</c> declaration, and a declaration says nothing about
/// whether anything ever reaches the index it declares. The second was that "category groups and
/// categories still index the name COLUMN under <c>case_insensitive</c>", which stopped being true
/// the day those two columns became <c>bytea</c>: both index <c>name_key</c> now, neither carries a
/// collation, and <c>bytea</c> cannot carry one. <b>A remark defending an absence with an argument
/// about a schema that no longer exists is how the gap below stayed invisible</b>, and the old
/// sentence about a fourth Testcontainer buying nothing is what it bought instead.
/// </para>
/// <para>
/// <b>The gap was never "nothing executes a duplicate insert".</b> Two do —
/// <c>RepositoryConstraintAttributionTests.AddCategoryGroup_WithADuplicateGroupName_TranslatesItsOwnUniqueIndex</c>
/// and its <c>AddCategory_</c> twin — and the four unique indexes are pinned as schema text by
/// <c>SchemaConstraintSnapshotTests</c>, so neither existence nor translation was ever unheld. What
/// nothing on any of the four tables said is that <b>the refusal came from the database rather than
/// from an application pre-check</b>. Those cases read an exception type and a <c>Name</c> key, and a
/// <c>SELECT … WHERE name_key = @key</c> in front of the insert reproduces both exactly: same type,
/// same key, same status, same sentence. Under one, every uniqueness rule in this product would be
/// enforced by a read-then-write with no transaction around it — which two concurrent requests walk
/// straight through — and the index could have been dropped from the database with nothing going red.
/// </para>
/// <para>
/// <b>Two halves close it and neither is the other's duplicate.</b> The three
/// <c>…_WithTheSameBlindIndexInOneBudget_AreRejected</c> cases go through a raw
/// <see cref="BudgetoidDbContext" /> with no repository between them and PostgreSQL, so what answers
/// is a <c>23505</c> under a constraint name — the database refusing, with nothing in the application
/// standing anywhere it could have refused first.
/// <see cref="AddingADuplicateName_ReadsNoneOfItsOwnTableBeforeTheInsert" /> comes at it from the
/// other side and reads the statements the four repositories actually send, so a pre-check added
/// later is red on the statement rather than on a status it cannot change.
/// </para>
/// <para>
/// <b>Payees has no case of the first kind here, and that is a known gap rather than a judgement.</b>
/// It is the one of the four whose duplicate-insert coverage
/// (<c>RepositoryConstraintAttributionTests.AddPayee_WithADuplicateBlindIndex_TranslatesItsOwnUniqueIndex</c>)
/// asserts a type and no constraint name at all, so it is the table where the pre-check substitution
/// would be least visible. It is covered by the census below on the read side and by the schema-text
/// pin on the existence side, and by nothing on the "PostgreSQL is what said no" side. Written down
/// as an absence rather than argued away, because the paragraph this one replaced is what argued the
/// last four absences away.
/// </para>
/// </remarks>
public sealed class BudgetScopingTests
{
    /// <summary>
    /// Minor unit of the USD accounts these tests seed. Precision is not what any of them is
    /// about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// That the account-name index is scoped to a budget: one label, two budgets, two rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This case still passes and its old reasoning is dead, which is why the reasoning is rewritten
    /// rather than left standing.</b> It used to read as "one label produces one digest, so a rule that
    /// was global would refuse the second row" — a sentence about what a client emits. That premise is
    /// being retired: the blind-index message gains the budget id, so a conforming client folding one
    /// label in two budgets will produce <em>two</em> digests and the second row would persist under a
    /// global index too. Leaving the old words would have left a green case whose stated subject it had
    /// stopped measuring.
    /// </para>
    /// <para>
    /// <b>What it actually proves, and what it must be read as proving, is the shape of the index:
    /// composite, with <c>budget_id</c> leading.</b> Drop that column from
    /// <c>IX_accounts_budget_id_name_key</c> and the second insert below is refused; that is the whole
    /// claim, and it is a claim about the schema rather than about a browser.
    /// </para>
    /// <para>
    /// <b>It works because <see cref="SealedNarrative.BlindIndex" /> is deterministic in its label and
    /// nothing else</b> — no budget id, no key, no message grammar. That is now a fixture deliberately
    /// modelling a state production no longer produces: two budgets holding one digest. Keeping it that
    /// way is the point rather than staleness. A fixture that folded the budget id in as the client
    /// does would make both rows differ by construction, and this case would then persist two rows
    /// whatever the index was over — green against a schema with no <c>budget_id</c> in the index at
    /// all. A test whose fixture already guarantees the outcome measures nothing, so the collision has
    /// to be handed to the database for the database to be the thing that declines to raise it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Accounts_WithTheSameNameInDifferentBudgets_BothPersist()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");

        // Act
        await using (BudgetoidDbContext dbA = CreateDb(host, budgetA))
        {
            dbA.Accounts.Add(CreateAccount(budgetA, "Checking"));
            await dbA.SaveChangesAsync();
        }

        await using BudgetoidDbContext dbB = CreateDb(host, budgetB);
        dbB.Accounts.Add(CreateAccount(budgetB, "Checking"));
        await dbB.SaveChangesAsync();

        // Assert
        await Assert.That(await dbB.Accounts.CountAsync()).IsEqualTo(1);
        await Assert.That(await CountAccountsAsync(host)).IsEqualTo(2L);
    }

    /// <summary>
    /// Two accounts whose names index alike cannot both live in one budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This case used to be spelled "Checking" against "checking", and that spelling has stopped
    /// being writable rather than having been simplified away.</b> The uniqueness rule is the same rule
    /// and it did not move layers — it moved COLUMNS, from <c>name</c> to <c>name_key</c>, because
    /// <c>accounts.name</c> is a sealed narrative field now and every seal draws a fresh nonce, so two
    /// rows holding one name hold different bytes. An index left on the envelope would exist, be
    /// unique, be rendered by <c>pg_get_indexdef</c>, and refuse nothing.
    /// </para>
    /// <para>
    /// <b>What DID leave this server is the case folding, and saying so is the point of this remark.</b>
    /// The <c>case_insensitive</c> collation went with the column type — <c>bytea</c> is not collatable
    /// — and the folding is now part of the normalisation the client applies before it computes the
    /// HMAC. This side cannot check that it happened: an index is a digest under a key that lives in a
    /// browser, so "Checking" and "checking" are the same account or two accounts entirely according to
    /// a step no constraint here can be written to. A reader who notices the old case-only case is gone
    /// must not restore it against the schema — it would seed two labels, get two digests, and assert a
    /// refusal that is now the client's to produce.
    /// </para>
    /// <para>
    /// So the honest subject left on this side is the one asserted below: EQUAL INDEX VALUES IN ONE
    /// BUDGET ARE REFUSED. The seeding uses one label twice rather than two, because
    /// <see cref="SealedNarrative.Indexed" /> is deterministic in its label and that determinism is the
    /// one property of a real blind index a fixture can reproduce. The two rows' envelopes are equal
    /// too, which is a fixture artefact and not the subject — the index is what the constraint is over,
    /// and <see cref="Accounts_WithTheSameNameInDifferentBudgets_BothPersist" /> next door is what says
    /// the refusal is scoped to the budget rather than global.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Accounts_WithTheSameBlindIndexInOneBudget_AreRejected()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        db.Accounts.Add(CreateAccount(budgetId, "Checking"));
        await db.SaveChangesAsync();

        // Act
        db.Accounts.Add(CreateAccount(budgetId, "Checking"));
        DbUpdateException? caught = null;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That((caught!.InnerException as PostgresException)?.SqlState)
            .IsEqualTo(PostgresErrorCodes.UniqueViolation);

        // THE CONSTRAINT NAME, not merely the SQLSTATE, and it is what keeps this case from passing on
        // the wrong refusal. 23505 is the answer to every unique violation on this table, including the
        // primary key and AK_accounts_id_budget_id — either of which a seeder that reused an id would
        // trip, with the same code, while the blind index was enforcing nothing at all.
        //
        // A LITERAL rather than AccountConfiguration.NameIndexName, for the reason
        // RepositoryConstraintAttributionTests states about the same string: that constant is what
        // AccountRepository matches PostgresException.ConstraintName against to decide whether a 23505
        // is the collision it models, so a test reading it agrees with the production filter by
        // construction and cannot see the two spellings drift apart.
        await Assert.That((caught.InnerException as PostgresException)?.ConstraintName)
            .IsEqualTo("IX_accounts_budget_id_name_key");
    }

    /// <summary>
    /// Two category groups whose names index alike cannot both live in one budget, and it is PostgreSQL
    /// that says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The account case's shape, on the table the old class remark said needed nothing.</b> Its
    /// argument about why the collision is spelled with one label twice rather than with two spellings
    /// of one word holds here unchanged and is not restated: the column is an AEAD envelope, every seal
    /// draws a fresh nonce, the <c>case_insensitive</c> collation left with the plaintext, and the only
    /// duplicate this table can still see is an equal blind index.
    /// </para>
    /// <para>
    /// <b>What is this case's own is that no repository stands between it and the database.</b>
    /// <c>RepositoryConstraintAttributionTests.AddCategoryGroup_WithADuplicateGroupName_TranslatesItsOwnUniqueIndex</c>
    /// already runs a duplicate through <c>CategoryGroupRepository</c> and reads back a
    /// <c>ValidationException</c> keyed on <c>Name</c>. An <c>AnyAsync</c> over <c>name_key</c> placed
    /// in front of that repository's <c>SaveChangesAsync</c> produces the identical exception, the
    /// identical key and the identical 400 — so that case cannot tell an index from a pre-check, and
    /// this one can only be satisfied by a database that refused.
    /// </para>
    /// <para>
    /// <b>The positions differ, which is not tidiness.</b> Both rows are otherwise free of any second
    /// rule to break: distinct identifiers keep <c>PK_category_groups</c> out of it, and the name index
    /// is the only constraint left for the <c>23505</c> to come from.
    /// </para>
    /// <para>
    /// <b>The constraint name is a LITERAL rather than <c>CategoryGroupConfiguration.NameIndexName</c>,
    /// for the reason written out on the account case above</b> — that constant is what
    /// <c>CategoryGroupRepository</c> matches <see cref="PostgresException.ConstraintName" /> against,
    /// so a test reading it would agree with the production filter by construction and could never see
    /// the two spellings drift apart.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CategoryGroups_WithTheSameBlindIndexInOneBudget_AreRejected()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        db.CategoryGroups.Add(CreateCategoryGroup(budgetId, "Essentials", 0));
        await db.SaveChangesAsync();

        // Act
        db.CategoryGroups.Add(CreateCategoryGroup(budgetId, "Essentials", 1));
        DbUpdateException? caught = null;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That((caught!.InnerException as PostgresException)?.SqlState)
            .IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That((caught.InnerException as PostgresException)?.ConstraintName)
            .IsEqualTo("IX_category_groups_budget_id_name_key");
    }

    /// <summary>
    /// Two categories whose names index alike cannot both live in one budget, and it is PostgreSQL that
    /// says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sibling above's argument in full, plus a seeded group.</b> A category cannot exist without
    /// one — the reference is composite and <c>RESTRICT</c> — so the group is written in the same save
    /// as the first category. Its label is deliberately unlike either category's, so
    /// <c>IX_category_groups_budget_id_name_key</c> has nothing to fire on and the constraint name
    /// asserted below can only have come from the categories index.
    /// </para>
    /// <para>
    /// <b>This is the table where the pre-check substitution would have cost the most.</b>
    /// <c>categories</c> is the one whose <c>GRANT UPDATE</c> list shipped without <c>name_key</c>
    /// twice, and the story of how that stayed quiet is written on
    /// <c>CategoryChangeTrackingTests</c>: the suite issued zero genuine renames, so the loud
    /// <c>42501</c> was never raised. A uniqueness rule held by a pre-check has the same
    /// shape — the application answers, the database is never consulted, and the run is green either
    /// way.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Categories_WithTheSameBlindIndexInOneBudget_AreRejected()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        CategoryGroup group = CreateCategoryGroup(budgetId, "Essentials", 0);
        db.CategoryGroups.Add(group);
        db.Categories.Add(CreateCategory(budgetId, group.Id, "Groceries", 0));
        await db.SaveChangesAsync();

        // Act
        db.Categories.Add(CreateCategory(budgetId, group.Id, "Groceries", 1));
        DbUpdateException? caught = null;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That((caught!.InnerException as PostgresException)?.SqlState)
            .IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That((caught.InnerException as PostgresException)?.ConstraintName)
            .IsEqualTo("IX_categories_budget_id_name_key");
    }

    /// <summary>
    /// No repository reads its own table before inserting into it, so the duplicate-name refusal on all
    /// four sealed-name tables is the database's and not an application pre-check's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The half the exception-shaped cases structurally cannot hold.</b>
    /// <c>RepositoryConstraintAttributionTests</c> asserts, for each of these four, that a duplicate
    /// comes back as the right exception keyed on the right member. Every one of those assertions is
    /// satisfied by an <c>AnyAsync</c> over <c>name_key</c> in front of <c>SaveChangesAsync</c> that
    /// throws the same exception itself — identical type, identical key, identical wire answer. So the
    /// evidence that the unique index is what enforces uniqueness was, until this case, entirely
    /// circumstantial on all four tables at once.
    /// </para>
    /// <para>
    /// <b>Why a pre-check is a defect and not merely a different implementation.</b> It is a read
    /// followed by a write with no transaction around it and no lock taken, so two requests naming one
    /// name both read "free" and both insert. Under a real index the second is refused; under a
    /// pre-check that has replaced the index, the second row is written and the budget holds two
    /// counterparties, two accounts or two categories for one name — with the blind index, which is the
    /// only thing that could have seen it, no longer being asked. It also does not work at all on these
    /// columns in the general case: this server cannot recompute a digest, so a pre-check can only
    /// compare bytes a client sent, and the day two clients disagree about normalisation it silently
    /// stops matching.
    /// </para>
    /// <para>
    /// <b>Data-driven over all four rather than over the two that lack a case, and the symmetry is the
    /// argument.</b> "Two tables have it and two do not" is exactly the asymmetry that produced the gap
    /// this file just closed — the old class remark reasoned from an account case to three tables it had
    /// stopped describing. Two extra <c>[Arguments]</c> rows are what that costs to make structurally
    /// impossible, and a fifth sealed-name table added later is one more row rather than a decision.
    /// </para>
    /// <para>
    /// <b>Reads of OTHER tables are deliberately not refused.</b> The claim is narrow — nothing looks
    /// this row's own name up before writing it — and a repository is free to read whatever else it
    /// needs. <c>CategoryRepository.PlaceAsync</c> and <c>DeleteAsync</c> both read <c>categories</c>
    /// legitimately, which is why the window is bounded at the <c>INSERT</c> rather than covering the
    /// whole call: a read AFTER the insert cannot be a pre-check.
    /// </para>
    /// <para>
    /// <b>Three non-vacuity guards, because two of the three ways this could pass on nothing are
    /// silent.</b> Something must escape (or the duplicate never collided and the arrangement is
    /// broken); an <c>INSERT</c> naming the table must have been sent (or a pre-check that
    /// short-circuited would leave no statements to search and an empty offender list); and the offenders
    /// are rendered as the SQL itself rather than counted, so a red hands the reader the exact
    /// <c>SELECT</c> to delete.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(AccountsTable)]
    [Arguments(PayeesTable)]
    [Arguments(CategoryGroupsTable)]
    [Arguments(CategoriesTable)]
    public async Task AddingADuplicateName_ReadsNoneOfItsOwnTableBeforeTheInsert(string table)
    {
        // Arrange — the taken name is seeded on a context of its own, so every statement the recorder
        // holds afterwards belongs to the Act. The recorder is attached through DbContextOptions, the
        // way the three *ChangeTrackingTests files attach it.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid categoryGroupId = await SeedTheTakenNameAsync(CreateOptions(host), budgetId, table);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));

        // Act — the repository's own add, on a row carrying a fresh identifier and the taken label, so
        // the name index is the only rule it breaks.
        Exception? escaped = await CaptureAsync(
            () => AddTheDuplicateAsync(db, table, budgetId, categoryGroupId));

        // Assert — that the write reached the database and was refused there. Without it every claim
        // below is satisfied by an arrangement whose two rows never collided at all.
        await Assert.That(escaped).IsNotNull();

        IReadOnlyList<string> statements = recorder.Statements;
        int insert = IndexOfFirst(statements, statement => IsWriteInto(statement, table));

        // The INSERT itself, before the window is measured: a repository that answered from a pre-check
        // without ever reaching the database sends none, and an offender list taken over an empty window
        // would then be empty for the worst possible reason.
        await Assert.That(insert)
            .IsGreaterThanOrEqualTo(0)
            .Because($"nothing INSERTed into {table}, so the refusal never came from PostgreSQL");

        string[] readsBeforeTheInsert =
        [
            .. statements.Take(insert).Where(statement => IsReadOf(statement, table)),
        ];

        // Offenders as the SQL rather than as a count, for the reason the logger census in UnitTests
        // renders sentences: a red that says "1 != 0" leaves the reader to find the statement, and a
        // statement is what somebody has to delete.
        await Assert.That(readsBeforeTheInsert).IsEmpty();
    }

    /// <summary>
    /// That the census above reports a read of the table when there is one to report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pin passes over an empty offender list, and an empty list is what a broken predicate
    /// produces too.</b> The shape is
    /// <c>GetSignedInUserHandlerTests.TheLoggerQuery_ReportsAConstructorThatTakesOne</c>'s: a defect
    /// staged on purpose, so the day <see cref="IsReadOf" /> stops recognising a statement — a quoting
    /// rule that changes under a provider upgrade, a verb prefix that drifts, a table name that stops
    /// matching — this case goes red while the pin stays green and says nothing.
    /// </para>
    /// <para>
    /// <b>That failure mode is not hypothetical here and the correction is the reason this control
    /// exists.</b> <see cref="NamesTheTable" /> was first written to look for a QUOTED relation, which
    /// is what EF does to column names; all four arms of the pin then reported no <c>INSERT</c> at all.
    /// Had the miss landed on the read side instead of the write side, the pin would have gone green
    /// having examined nothing, and there would have been no second case to notice.
    /// </para>
    /// <para>
    /// <b>The read is staged on the repository's own <see cref="BudgetoidDbContext" />, which is what
    /// makes it the real defect and not a lookalike.</b> A pre-check inside <c>AddAsync</c> would run on
    /// exactly this context, be recorded by exactly this interceptor and arrive in exactly this
    /// position — before the insert the same call goes on to send. It is spelled as a bare
    /// <c>AnyAsync</c> rather than as a lookup by <c>name_key</c> only because a predicate over the
    /// blind index would add a parameter binding without adding anything to the claim; the statement is
    /// a <c>SELECT</c> naming the table either way, and the table name is the whole of what is matched.
    /// </para>
    /// <para>
    /// One table rather than four. The census's four arms differ in which repository is called, and this
    /// case calls no repository behaviour that the arms do not — its subject is the two string
    /// predicates, which are shared by every arm and have nothing per-table in them.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheCensusReportsAReadOfItsOwnTableBeforeTheInsert()
    {
        // Arrange — the census arrangement for one table.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid categoryGroupId = await SeedTheTakenNameAsync(
            CreateOptions(host), budgetId, CategoryGroupsTable);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));

        // Act — the defect first: a read of category_groups on the context the repository is about to
        // be handed. It tracks nothing, so the add below behaves exactly as it does in the pin and the
        // only difference between the two runs is the statement this line puts on the wire.
        await db.CategoryGroups.AnyAsync();
        Exception? escaped = await CaptureAsync(
            () => AddTheDuplicateAsync(db, CategoryGroupsTable, budgetId, categoryGroupId));

        // Assert — the same three steps the pin takes, with the third answering the other way.
        await Assert.That(escaped).IsNotNull();

        IReadOnlyList<string> statements = recorder.Statements;
        int insert = IndexOfFirst(statements, statement => IsWriteInto(statement, CategoryGroupsTable));
        await Assert.That(insert).IsGreaterThanOrEqualTo(0);

        string[] readsBeforeTheInsert =
        [
            .. statements.Take(insert)
                .Where(statement => IsReadOf(statement, CategoryGroupsTable)),
        ];

        // Exactly one, and it names the table. A predicate that reported every statement would satisfy
        // "the staged read is reported" while turning the pin red on the four INSERTs it exists to
        // allow, so the count is what tells a filter that recognises reads from one that recognises
        // everything.
        await Assert.That(readsBeforeTheInsert.Length).IsEqualTo(1);
        await Assert.That(readsBeforeTheInsert[0]).Contains(CategoryGroupsTable);
    }

    [Test]
    public async Task CategoryGroups_PositionsAreIndependentPerBudget()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");

        await using (BudgetoidDbContext dbA = CreateDb(host, budgetA))
        {
            dbA.CategoryGroups.Add(CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetA,
                SealedNarrative.Indexed("Essentials"),
                null,
                0,
                UtcNow()));
            dbA.CategoryGroups.Add(CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetA,
                SealedNarrative.Indexed("Lifestyle"),
                null,
                1,
                UtcNow()));
            await dbA.SaveChangesAsync();
        }

        // Act — budget B starts its own sequence at 0; positions in budget A must not interfere.
        await using BudgetoidDbContext dbB = CreateDb(host, budgetB);
        dbB.CategoryGroups.Add(CategoryGroup.Create(
            Guid.CreateVersion7(),
            budgetB,
            SealedNarrative.Indexed("Essentials"),
            null,
            0,
            UtcNow()));
        await dbB.SaveChangesAsync();
        List<CategoryGroup> groupsOfB = await dbB.CategoryGroups
            .OrderBy(group => group.Position)
            .ToListAsync();

        // Assert
        await Assert.That(groupsOfB.Count).IsEqualTo(1);
        await Assert.That(groupsOfB[0].Position).IsEqualTo(0);
        await Assert.That(groupsOfB[0].BudgetId).IsEqualTo(budgetB);

        await using BudgetoidDbContext dbA2 = CreateDb(host, budgetA);
        List<int> positionsOfA = await dbA2.CategoryGroups
            .OrderBy(group => group.Position)
            .Select(group => group.Position)
            .ToListAsync();
        await Assert.That(positionsOfA).IsEquivalentTo(new[] { 0, 1 });
    }

    /// <summary>
    /// The four sealed-name tables the census runs over, spelled as the database spells them.
    /// </summary>
    /// <remarks>
    /// <b>Constants because <c>[Arguments]</c> demands them</b>, and the unquoted table name because
    /// that is what <see cref="StatementRecorder.WritesTo" /> takes and what the statement text carries.
    /// They are the DATABASE's spelling and not the <c>DbSet</c>'s: a statement says
    /// <c>"category_groups"</c> where C# says <c>CategoryGroups</c>, and a search for the second finds
    /// nothing in a run that is going perfectly wrong.
    /// </remarks>
    private const string AccountsTable = "accounts";
    private const string PayeesTable = "payees";
    private const string CategoryGroupsTable = "category_groups";
    private const string CategoriesTable = "categories";

    /// <summary>
    /// The label both rows of a census arm carry, which is what makes their blind indexes equal.
    /// </summary>
    /// <remarks>
    /// Neutral across four tables on purpose: it is a name on an account, a payee, a group and a
    /// category in turn, and a label reading as one of them ("Checking", "Groceries") would suggest the
    /// arms differ in something other than which repository is called.
    /// </remarks>
    private const string TakenLabel = "Taken";

    /// <summary>
    /// The label of the scaffolding group every census arm seeds. Deliberately not
    /// <see cref="TakenLabel" />, so on the <see cref="CategoryGroupsTable" /> arm the row the duplicate
    /// collides with is the seeded group carrying the taken label and never this one.
    /// </summary>
    private const string GroupLabel = "Holder";

    private static Account CreateAccount(Guid budgetId, string name) => Account.Create(
        Guid.CreateVersion7(),
        budgetId,
        SealedNarrative.Indexed(name),
        AccountType.Checking,
        0m,
        "USD",
        UsdMinorUnit,
        UtcNow());

    private static CategoryGroup CreateCategoryGroup(Guid budgetId, string name, int position) =>
        CategoryGroup.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed(name),
            null,
            position,
            UtcNow());

    private static Category CreateCategory(
        Guid budgetId,
        Guid categoryGroupId,
        string name,
        int position) => Category.Create(
        Guid.CreateVersion7(),
        budgetId,
        categoryGroupId,
        SealedNarrative.Indexed(name),
        null,
        position,
        UtcNow());

    /// <summary>
    /// Seeds the row whose name the census arm then collides with, and hands back the group id the
    /// <see cref="CategoriesTable" /> arm needs.
    /// </summary>
    /// <remarks>
    /// <b>The group is written on every arm and not only on the two that need one.</b> A category cannot
    /// exist without a group, and seeding it unconditionally keeps the four arms one shape rather than
    /// four — which matters because the thing being compared across arms is which repository was called,
    /// and nothing else. It costs one row on the two arms that ignore it.
    /// </remarks>
    private static async Task<Guid> SeedTheTakenNameAsync(
        DbContextOptions<BudgetoidDbContext> options,
        Guid budgetId,
        string table)
    {
        await using BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId));
        CategoryGroup group = CreateCategoryGroup(budgetId, GroupLabel, 0);
        seed.CategoryGroups.Add(group);

        switch (table)
        {
            case AccountsTable:
                seed.Accounts.Add(CreateAccount(budgetId, TakenLabel));
                break;
            case PayeesTable:
                seed.Payees.Add(Payee.Create(
                    Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed(TakenLabel), UtcNow()));
                break;
            case CategoryGroupsTable:
                seed.CategoryGroups.Add(CreateCategoryGroup(budgetId, TakenLabel, 1));
                break;
            case CategoriesTable:
                seed.Categories.Add(CreateCategory(budgetId, group.Id, TakenLabel, 0));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(table), table, "No census arm is wired for this table.");
        }

        await seed.SaveChangesAsync();

        return group.Id;
    }

    /// <summary>
    /// Adds the duplicate through the repository that owns <paramref name="table" />.
    /// </summary>
    /// <remarks>
    /// <b>Through the repository and never through the <c>DbSet</c>.</b> The subject is what the
    /// production write path sends, and the write path is <c>AddAsync</c>: a case that added the entity
    /// itself would measure EF's statement generation, which nobody was ever going to put a pre-check
    /// into. Every row carries a fresh identifier, so the primary key stays out of it and the name index
    /// is the only rule broken.
    /// </remarks>
    private static Task AddTheDuplicateAsync(
        BudgetoidDbContext db,
        string table,
        Guid budgetId,
        Guid categoryGroupId) => table switch
        {
            AccountsTable => new AccountRepository(db).AddAsync(CreateAccount(budgetId, TakenLabel)),
            PayeesTable => new PayeeRepository(db).AddAsync(Payee.Create(
                Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed(TakenLabel), UtcNow())),
            CategoryGroupsTable => new CategoryGroupRepository(db).AddAsync(
                CreateCategoryGroup(budgetId, TakenLabel, 2)),
            CategoriesTable => new CategoryRepository(db).AddAsync(
                CreateCategory(budgetId, categoryGroupId, TakenLabel, 1)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(table), table, "No census arm is wired for this table."),
        };

    /// <summary>
    /// Whether <paramref name="statement" /> reads <paramref name="table" />.
    /// </summary>
    /// <remarks>
    /// The verb is a prefix test on the trimmed text, <see cref="StatementRecorder.WritesTo" />'s shape,
    /// so a write mentioning the word <c>select</c> in a column name cannot be read as a read.
    /// </remarks>
    private static bool IsReadOf(string statement, string table) =>
        statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
        && NamesTheTable(statement, table);

    /// <summary>
    /// Whether <paramref name="statement" /> inserts into <paramref name="table" />.
    /// </summary>
    private static bool IsWriteInto(string statement, string table) =>
        statement.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
        && NamesTheTable(statement, table);

    /// <summary>
    /// Whether <paramref name="statement" /> names <paramref name="table" /> as a whole identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The table name arrives UNQUOTED and that is measured rather than assumed.</b> This helper was
    /// first written to look for <c>"category_groups"</c> with the quotes EF puts around column names;
    /// all four arms then found no <c>INSERT</c> at all and reported <c>-1</c>. What the recorder
    /// actually holds is <c>INSERT INTO category_groups (id, budget_id, …)</c> — quoted columns, a bare
    /// relation — so a quoted search finds nothing in a run where everything is working.
    /// </para>
    /// <para>
    /// <b>Which is why this is a delimited match and not <c>Contains</c>.</b> Bare names collide by
    /// prefix: a search for <c>categories</c> is safe against <c>category_groups</c> today only because
    /// neither is a substring of the other, and that is an accident of two spellings rather than a
    /// property anybody maintains. Requiring a non-identifier character on both sides makes the check
    /// hold for whatever the fifth sealed-name table is called.
    /// </para>
    /// </remarks>
    private static bool NamesTheTable(string statement, string table)
    {
        for (int at = statement.IndexOf(table, StringComparison.Ordinal);
            at >= 0;
            at = statement.IndexOf(table, at + 1, StringComparison.Ordinal))
        {
            int after = at + table.Length;
            bool boundedOnTheLeft = at == 0 || !IsIdentifierCharacter(statement[at - 1]);
            bool boundedOnTheRight =
                after == statement.Length || !IsIdentifierCharacter(statement[after]);

            if (boundedOnTheLeft && boundedOnTheRight)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    /// <summary>
    /// The position of the first statement satisfying <paramref name="predicate" />, or <c>-1</c>.
    /// </summary>
    /// <remarks>
    /// Written out rather than reached for through <c>ToList().FindIndex</c> so the miss answers
    /// <c>-1</c> explicitly, which is the value the case asserts against before it takes a window.
    /// </remarks>
    private static int IndexOfFirst(IReadOnlyList<string> statements, Func<string, bool> predicate)
    {
        for (int position = 0; position < statements.Count; position++)
        {
            if (predicate(statements[position]))
            {
                return position;
            }
        }

        return -1;
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" /> when
    /// nothing did. Deliberately untyped, the shape
    /// <c>RepositoryConstraintAttributionTests.CaptureAsync</c> uses: the census has no opinion about
    /// which refusal a repository translates a duplicate into, only that a duplicate was refused.
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

    private static async Task<long> CountAccountsAsync(RepositoryTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select count(*) from accounts", connection);
        object? scalar = await command.ExecuteScalarAsync();

        return scalar is long count
            ? count
            : throw new InvalidOperationException($"Expected a count, got '{scalar ?? "null"}'.");
    }

    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId) =>
        new(CreateOptions(host), new TestBudgetContext(budgetId));

    /// <summary>
    /// Options against this host's database, optionally carrying <paramref name="recorder" />.
    /// </summary>
    /// <remarks>
    /// The connection is the container superuser, as in the <c>*ChangeTrackingTests</c> files: the claims
    /// here are about which statements EF composes and about which constraint PostgreSQL raises, both of
    /// which are decided independently of the app role's grants.
    /// </remarks>
    private static DbContextOptions<BudgetoidDbContext> CreateOptions(
        RepositoryTestHost host,
        StatementRecorder? recorder = null)
    {
        DbContextOptionsBuilder<BudgetoidDbContext> builder = new();
        builder.UseNpgsql(host.ConnectionString);
        if (recorder is not null)
        {
            builder.AddInterceptors(recorder);
        }

        return builder.Options;
    }

    private static DateTime UtcNow() =>
        new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
