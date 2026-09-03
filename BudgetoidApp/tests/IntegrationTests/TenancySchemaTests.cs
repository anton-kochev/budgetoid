using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Covers the rule that a budget-owned row never moves between budgets. This is tenancy, not
/// tidiness: a row that changes <c>budget_id</c> carries its history into someone else's ledger.
/// The domain makes it true by construction — no method on <see cref="Account" />,
/// <see cref="CategoryGroup" />, <see cref="Category" />, <see cref="Payee" /> or
/// <see cref="Transaction" /> reaches <c>BudgetId</c> — so every statement here is raw Npgsql, the
/// only way to send the UPDATE the domain will not produce.
/// </summary>
/// <remarks>
/// <para>
/// The database enforces the rule in two layers, and these five tests are split along that seam.
/// Composite foreign keys default to <c>ON UPDATE NO ACTION</c>, so moving a parent out from under
/// a child is refused with <c>23503</c> on any connection, superuser included. That covers a
/// transaction (its <c>(account_id, budget_id)</c> reference always exists) and a category (its
/// <c>(category_group_id, budget_id)</c> reference always exists) completely, and those two tests
/// run on the admin connection. It covers an account, a category group and a payee only while
/// something references them — an <b>empty</b> account, an <b>empty</b> category group and an
/// <b>unreferenced</b> payee have no child to object — so for those three the refusal comes from
/// the application role's column grants instead: <c>UPDATE</c> is granted per explicit column list
/// and <c>budget_id</c> is absent from every list, so the same statement fails with <c>42501</c>.
/// Grants only bind connections opened as the role, which is why those three tests run on
/// <see cref="RepositoryTestHost.AppConnectionString" /> — on the admin connection they would pass
/// no matter what the grants say.
/// </para>
/// <para>
/// Each <c>42501</c> is paired, in the same test, with an UPDATE of a granted column on the same
/// table that must succeed on the same role. Without the pair the refusal is vacuous: a role with
/// no UPDATE grant at all — or a grants script that is an empty file — refuses everything with the
/// same SQLSTATE. The success half is what pins "exactly this column is immutable" rather than
/// "the role cannot write".
/// </para>
/// </remarks>
public sealed class TenancySchemaTests
{
    /// <summary>
    /// Minor unit of the USD account these tests seed. Precision is not what any of them is about;
    /// the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Database_RefusesToMoveATransactionToAnotherBudget()
    {
        // Arrange — one account and the transaction on it. The transaction's account reference is
        // composite and mandatory, so there is no shape of transaction this refusal misses.
        await using RepositoryTestHost host = await StartHostAsync();
        (_, Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid transactionId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            Account account = Account.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant);
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();

            Transaction transaction = Transaction.Create(
                Guid.CreateVersion7(),
                budgetId,
                account.Id,
                -10m,
                UsdMinorUnit,
                new DateOnly(2026, 6, 12),
                SealedNarrative.Description("Groceries"),
                SeedInstant);
            seed.Transactions.Add(transaction);
            await seed.SaveChangesAsync();
            transactionId = transaction.Id;
        }

        // Act — the admin connection, which the class remarks say is enough for a composite-FK
        // refusal. It carries no ambient budget and needs none: row-level security never applies to
        // the container superuser, and this connection is also what reads the counts back below,
        // where seeing every budget's rows is the point.
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, "transactions", transactionId, otherBudgetId);

        // Assert — the constraint name is asserted next to the SQLSTATE so the refusal has to come
        // from the composite account reference, which is the tenancy rule, rather than from
        // FK_transactions_budgets_budget_id, which would only mean the destination did not exist.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(exception.ConstraintName)
            .IsEqualTo("FK_transactions_accounts_account_id_budget_id");

        // The destination row is asserted rather than assumed, and on this table it is not
        // redundant with the constraint name above: point the move at a nonexistent budget and
        // PostgreSQL still names the composite account key first, so the name assertion alone stays
        // green while the test has stopped being about tenancy. Only this line notices. See
        // SeedTwoBudgetsAsync.
        await Assert.That(await CountRowsAsync(connection, "budgets", "id", otherBudgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "transactions", "budget_id", budgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "transactions", "budget_id", otherBudgetId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesToMoveACategoryToAnotherBudget()
    {
        // Arrange — a category always sits in a group, and the reference to that group is composite,
        // so this refusal has no gap either.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid userId, Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid categoryId;
        Guid categoryGroupId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            CategoryGroup group = CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Everyday"),
                null,
                0,
                SeedInstant);
            seed.CategoryGroups.Add(group);
            Category category = Category.Create(
                Guid.CreateVersion7(),
                budgetId,
                group.Id,
                SealedNarrative.Indexed("Groceries"),
                null,
                0,
                SeedInstant);
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            categoryId = category.Id;
            categoryGroupId = group.Id;
        }

        // Act — the admin connection, on the same terms as the transaction test above: it is enough
        // for a composite-FK refusal, and it is also what reads the counts back.
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, "categories", categoryId, otherBudgetId);

        // Assert — the group reference is the tenancy rule, and here, unlike on transactions, naming
        // it does rule out a refusal that came from the destination budget not existing: with a
        // nonexistent destination PostgreSQL names FK_categories_budgets_budget_id instead. Which
        // constraint gets named first is an ordering artifact though, not a guarantee, so the
        // destination row is still asserted below rather than inferred from the name.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(exception.ConstraintName)
            .IsEqualTo("FK_categories_category_groups_category_group_id_budget_id");

        await Assert.That(await CountRowsAsync(connection, "budgets", "id", otherBudgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "categories", "budget_id", budgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "categories", "budget_id", otherBudgetId))
            .IsEqualTo(0L);

        // THE SUCCESS HALF, AND THIS TABLE HAD NONE AT ALL UNTIL THIS SLICE. Every count above is a
        // count of rows that did not move, and a refusal proves nothing on its own: a role that could
        // update NO column of this table satisfies every assertion so far. The pair is what makes the
        // refusal mean "budget_id is withheld" rather than "categories is read-only to this role".
        //
        // THE CONNECTION DIFFERS FROM THE REFUSAL'S, unlike on category_groups, and the reason is the
        // reason this table never had a control: the refusal above is a composite FOREIGN KEY doing the
        // work, and a foreign key needs no grant, so it fires on the admin connection and the case was
        // complete without ever opening an app-role one. That is exactly how a grant hole survives here
        // — the tenancy question is answered by a constraint, and the grant question is never asked.
        // The success half therefore opens an app connection of its own, carrying the same user and the
        // same ambient budget.
        //
        // FIVE COLUMNS IN ONE STATEMENT, AND FOUR WOULD NOT DO. This is the longest grant list of the
        // four sealed tables, and the argument is category_groups' with one more column on it: EF names
        // only what changed, so a rename leaving the note alone emits `name, name_key` and passes under
        // a grant missing `description`, while a description-only edit emits `description, name` and
        // passes under one missing `name_key`. Measured this slice under the real hole,
        // GRANT UPDATE (name, description, position, category_group_id): a genuine rename answers
        // `42501: permission denied for table categories`, while `set category_group_id = ...` and
        // `set position = ...` both answer UPDATE 1 on the same connection in the same request. Naming
        // all five is the only shape that reddens on any single missing column.
        //
        // PostgreSQL names the RELATION and nothing else — `permission denied for table categories`,
        // from aclcheck_error — so a 42501 here tells a reader which table and never which column. That
        // is why the statement is spelled out inline: the SQL is the only place the five column names
        // appear together, and a helper would hide the one list a person debugging this needs to read.
        //
        // The values go through SealedNarrative for the reason the account and group cases give, and the
        // description is deliberately NON-NULL: writing null would still exercise the grant but would
        // leave the case unable to tell "the column was written" from "the column was cleared".
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(userId, budgetId);
        IndexedName renamedTo = SealedNarrative.Indexed("Food Shopping");
        NarrativeField note = SealedNarrative.Description("Weekly food shop");
        await using NpgsqlCommand rewrite = new(
            "update categories set name = @name, name_key = @name_key, "
            + "description = @description, position = @position, "
            + "category_group_id = @category_group_id where id = @id",
            app);
        rewrite.Parameters.AddWithValue("name", renamedTo.Name.Envelope.ToArray());
        rewrite.Parameters.AddWithValue("name_key", renamedTo.BlindIndex.ToArray());
        rewrite.Parameters.AddWithValue("description", note.Envelope.ToArray());
        rewrite.Parameters.AddWithValue("position", 1);
        rewrite.Parameters.AddWithValue("category_group_id", categoryGroupId);
        rewrite.Parameters.AddWithValue("id", categoryId);
        await Assert.That(await rewrite.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task Database_RefusesToMoveAnEmptyAccountToAnotherBudget()
    {
        // Arrange — an account with no transactions on it, which is exactly the shape the
        // transactions foreign key cannot refuse: nothing references the account, so the only
        // thing standing between it and another budget's ledger is the app role's grant list, in
        // which accounts.budget_id does not appear.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid userId, Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid accountId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            Account account = Account.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant);
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();
            accountId = account.Id;
        }

        // Act — on the app role's connection; the class remarks say why the admin connection
        // cannot observe this rule. It carries budgetId, the budget the account is in, because
        // accounts is row-level-security scoped: without it the rename below would match zero rows
        // and the refusal it is paired with would go vacuous. The destination budget is real (see
        // SeedTwoBudgetsAsync), so if the grant ever leaked budget_id the move would succeed
        // outright instead of tripping a foreign key and passing for the wrong reason.
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(userId, budgetId);
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            app, "accounts", accountId, otherBudgetId);

        // Assert
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await Assert.That(await CountRowsAsync(connection, "accounts", "budget_id", budgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "accounts", "budget_id", otherBudgetId))
            .IsEqualTo(0L);

        // The success half of the pair (see the class remarks): name and name_key are both on the
        // accounts grant list, so the same role renaming the same row must go through. Same connection
        // as the refusal, so the session's ambient budget is identical too and only the column differs.
        //
        // SPELLED OUT INLINE, AS ALL THREE FLIPPED TESTS NOW ARE, and the divergence is the
        // point rather than a duplication to fold back. There used to be a helper writing one text
        // `name`; category_groups was its last caller and it left when that column was sealed. It was
        // wrong for a bytea column in two separate ways.
        // A text literal into a bytea column is refused by the TYPE CHECKER with 42804 — before any
        // grant or policy is consulted, so it never reaches the question this pair is asking, and its
        // SQLSTATE is easy to mistake for a refusal somebody measured. And one column is not the
        // operation: Account.Update takes an IndexedName and writes the envelope and the index in one
        // statement, so a rename this role can actually perform names both columns, and a grant that
        // covered only one would refuse the whole statement while leaving a one-column probe green.
        // That is not hypothetical — AppRoleGrantsTests carried exactly that probe.
        //
        // Two further traps under the values themselves, both of which answer 23514 and both of which
        // would be read as the row-level-security verdict this file is about: a short or wrongly
        // versioned envelope trips CK_accounts_name_length or CK_accounts_name_version, and an index
        // of any width but 32 trips CK_accounts_name_key_length. SealedNarrative.Indexed is what makes
        // both halves well-formed by construction.
        IndexedName renamedTo = SealedNarrative.Indexed("Everyday Checking");
        await using NpgsqlCommand rename = new(
            "update accounts set name = @name, name_key = @name_key where id = @id",
            app);
        rename.Parameters.AddWithValue("name", renamedTo.Name.Envelope.ToArray());
        rename.Parameters.AddWithValue("name_key", renamedTo.BlindIndex.ToArray());
        rename.Parameters.AddWithValue("id", accountId);
        await Assert.That(await rename.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task Database_RefusesToMoveAnEmptyCategoryGroupToAnotherBudget()
    {
        // Arrange — a group with no categories in it, for the same reason as the empty account:
        // the categories foreign key has no row to object with, so the grant list on
        // category_groups — which does not carry budget_id — is the rule's only enforcement.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid userId, Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid groupId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            CategoryGroup group = CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Everyday"),
                null,
                0,
                SeedInstant);
            seed.CategoryGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        // Act — app role connection carrying budgetId, real destination budget, on the same terms
        // as the account test above.
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(userId, budgetId);
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            app, "category_groups", groupId, otherBudgetId);

        // Assert
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await Assert.That(await CountRowsAsync(connection, "category_groups", "budget_id", budgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "category_groups", "budget_id", otherBudgetId))
            .IsEqualTo(0L);

        // The success half of the pair (see the class remarks): all four mutable columns are on the
        // category_groups grant list, so the same role rewriting the same row must go through. Same
        // connection as the refusal, so the session's ambient budget is identical too.
        //
        // UpdateNameAsync IS GONE AND THIS WAS ITS LAST CALLER. That helper wrote one text `name`,
        // which is wrong here in the two ways it was already wrong on accounts and payees - a text
        // literal into a bytea column is refused by the TYPE CHECKER with 42804, before any grant or
        // policy is consulted, so it never reaches the question this pair asks, and its SQLSTATE reads
        // like a refusal somebody measured - plus one that is this table's own.
        //
        // FOUR COLUMNS IN ONE STATEMENT, AND THREE WOULD NOT DO. On accounts and payees a rename always
        // names both halves of the name, so ANY rename catches a half grant. Here EF names only the
        // columns that changed, so a rename leaving the description alone emits two columns and
        // SUCCEEDS under a grant missing `description` - measured on postgres:17.10 under
        // GRANT UPDATE (name, name_key, position): the two-column statement answers UPDATE 1, the
        // three-column one answers 42501, and `set description = null` answers 42501. `position` shares
        // the same grant list, so a three-column control cannot tell a four-column grant from a
        // three-column one either. Only naming all four in one statement reddens on any single missing
        // column, which is why this is spelled out inline rather than behind a helper that would invite
        // the next table to reuse a shape that does not fit it.
        //
        // The values are built by SealedNarrative for the reason the two cases above give, and this
        // table adds two more traps to the list: a short or wrongly versioned description trips
        // CK_category_groups_description_length or CK_category_groups_description_version, both 23514
        // and both easy to read as the row-level-security verdict this file is about. The description
        // is deliberately NON-NULL here - writing null would still exercise the grant, but it would
        // leave the case unable to tell "the column was written" from "the column was cleared".
        IndexedName renamedTo = SealedNarrative.Indexed("Essentials");
        NarrativeField note = SealedNarrative.Description("Required spending");
        await using NpgsqlCommand rewrite = new(
            "update category_groups set name = @name, name_key = @name_key, "
            + "description = @description, position = @position where id = @id",
            app);
        rewrite.Parameters.AddWithValue("name", renamedTo.Name.Envelope.ToArray());
        rewrite.Parameters.AddWithValue("name_key", renamedTo.BlindIndex.ToArray());
        rewrite.Parameters.AddWithValue("description", note.Envelope.ToArray());
        rewrite.Parameters.AddWithValue("position", 1);
        rewrite.Parameters.AddWithValue("id", groupId);
        await Assert.That(await rewrite.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task Database_RefusesToMoveAnUnreferencedPayeeToAnotherBudget()
    {
        // Arrange — a payee no transaction names. A payee is referenced optionally, so this is not
        // an exotic state: every payee is unreferenced between being created and being used, and
        // for that whole window the grant list on payees — no budget_id in it — is the only thing
        // holding the tenancy line.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid userId, Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid payeeId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            Payee payee = Payee.Create(Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed("Corner Shop"), SeedInstant);
            seed.Payees.Add(payee);
            await seed.SaveChangesAsync();
            payeeId = payee.Id;
        }

        // Act — app role connection carrying budgetId, real destination budget, on the same terms
        // as the two tests above.
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(userId, budgetId);
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            app, "payees", payeeId, otherBudgetId);

        // Assert
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await Assert.That(await CountRowsAsync(connection, "payees", "budget_id", budgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "payees", "budget_id", otherBudgetId))
            .IsEqualTo(0L);

        // The success half of the pair (see the class remarks): name and name_key are both on the
        // payees grant list, so the same role renaming the same row must go through. Same connection
        // as the refusal, so the session's ambient budget is identical too and only the column differs.
        //
        // SPELLED OUT INLINE, LIKE ITS TWO NEIGHBOURS, and the divergence is the point rather
        // than a duplication to fold back. The deleted helper wrote one text `name`, which was wrong
        // here in two separate ways once payees.name became bytea. A text literal into a bytea column is
        // refused by the TYPE CHECKER with 42804 — before any grant or policy is consulted, so it never
        // reaches the question this pair is asking, and its SQLSTATE is easy to mistake for a refusal
        // somebody measured. And one column is not the operation: Payee.Rename takes an IndexedName and
        // writes the envelope and the index in one statement, so a rename this role can actually
        // perform names both columns, and a grant that covered only one would refuse the whole
        // statement while leaving a one-column probe green. That is not hypothetical — it is exactly
        // the shape that let a (name)-only grant ship on accounts with nothing red.
        //
        // Two further traps under the values themselves, both of which answer 23514 and both of which
        // would be read as the row-level-security verdict this file is about: a short or wrongly
        // versioned envelope trips CK_payees_name_length or CK_payees_name_version, and an index of any
        // width but 32 trips CK_payees_name_key_length. SealedNarrative.Indexed is what makes both
        // halves well-formed by construction.
        IndexedName renamedTo = SealedNarrative.Indexed("Corner Shop Deli");
        await using NpgsqlCommand rename = new(
            "update payees set name = @name, name_key = @name_key where id = @id",
            app);
        rename.Parameters.AddWithValue("name", renamedTo.Name.Envelope.ToArray());
        rename.Parameters.AddWithValue("name_key", renamedTo.BlindIndex.ToArray());
        rename.Parameters.AddWithValue("id", payeeId);
        await Assert.That(await rename.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Seeds one owner with two budgets and returns the owner together with both budget ids: the
    /// one every row starts in, and the one every UPDATE moves it to. The owner is returned because
    /// an app-role session names a user as well as a budget — see
    /// <see cref="RepositoryTestHost.OpenAppConnectionAsync" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The destination is a <b>real, persisted</b> budget, and that is the point of this helper. A
    /// freshly generated <see cref="Guid" /> would be the obvious shortcut and would ruin the file:
    /// every table carries a single-column foreign key to <c>budgets</c>, so a move to a
    /// nonexistent budget raises <c>23503</c> for a reason that has nothing to do with tenancy —
    /// the two refusal tests would go green on the wrong constraint, and the three
    /// characterization tests would report a gap as closed when it is wide open.
    /// </para>
    /// <para>
    /// The destination is also left empty. All four named tables keep one name per budget, by two
    /// mechanisms rather than one. <c>categories</c> alone still indexes the name COLUMN, under the
    /// <c>case_insensitive</c> collation, as <c>IX_categories_budget_id_name</c>. On <c>accounts</c>,
    /// <c>payees</c> and now <c>category_groups</c> the name is a <c>bytea</c> envelope this server
    /// holds no key for, so <c>IX_accounts_budget_id_name_key</c>, <c>IX_payees_budget_id_name_key</c>
    /// and <c>IX_category_groups_budget_id_name_key</c> are unique over <c>(budget_id, name_key)</c> —
    /// the blind index the client computes over a name it case-folded first — and the
    /// <c>case_insensitive</c> collation left all three columns BY FORCE, because <c>bytea</c> is not a
    /// collatable type. The consequence for this helper is the same
    /// either way: a row waiting in the destination under the same name — the same index value on the
    /// sealed pair — would make the UPDATE fail with <c>23505</c> instead of doing what the test is
    /// about. The two budgets differ for the same reason, against <c>IX_budgets_user_id_name</c>: that
    /// column is an envelope too and carries no blind index, so what a duplicate would collide on is
    /// raw byte equality, which two seeds of one label produce because
    /// <see cref="SealedNarrative.Name" /> is deterministic in its label.
    /// </para>
    /// </remarks>
    private static async Task<(Guid UserId, Guid BudgetId, Guid OtherBudgetId)> SeedTwoBudgetsAsync(
        RepositoryTestHost host)
    {
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid budgetId = await host.SeedAdditionalBudgetAsync(userId, "Household");
        Guid otherBudgetId = await host.SeedAdditionalBudgetAsync(userId, "Holiday Fund");
        return (userId, budgetId, otherBudgetId);
    }

    /// <summary>
    /// Sends the move over <paramref name="connection" /> and returns the refusal. The caller opens
    /// the connection rather than handing over a string, because the two refusals under test live
    /// on different ones and the app-role one is not interchangeable with its connection string:
    /// composite-FK refusals fire anywhere, so the admin connection exercises them, while grant
    /// refusals only exist for the app role — and an app-role connection has to carry the signed-in
    /// user and its ambient budget, which only
    /// <see cref="RepositoryTestHost.OpenAppConnectionAsync" /> arranges.
    /// </summary>
    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        NpgsqlConnection connection,
        string table,
        Guid rowId,
        Guid destinationBudgetId)
    {
        await using NpgsqlCommand command = BuildMove(connection, table, rowId, destinationBudgetId);

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected PostgresException.");
    }

    private static NpgsqlCommand BuildMove(
        NpgsqlConnection connection,
        string table,
        Guid rowId,
        Guid destinationBudgetId)
    {
        // The table name is interpolated because every caller passes a literal; the ids are
        // parameters, as they must be.
        NpgsqlCommand command = new(
            $"update {table} set budget_id = @budget_id where id = @id",
            connection);
        command.Parameters.AddWithValue("budget_id", destinationBudgetId);
        command.Parameters.AddWithValue("id", rowId);
        return command;
    }

    /// <summary>
    /// Counts rows keyed on whichever column the caller is reasoning about. The column is explicit
    /// rather than derived from the table because these tests straddle two keys: the budget's own
    /// id on <c>budgets</c>, the owning budget id on everything else.
    /// </summary>
    private static async Task<long> CountRowsAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        Guid id)
    {
        await using NpgsqlCommand command = new(
            $"select count(*) from {table} where {column} = @id",
            connection);
        command.Parameters.AddWithValue("id", id);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{table}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Builds a context bound to an ambient budget, which the budget-isolated sets these tests seed
    /// — <c>Accounts</c>, <c>CategoryGroups</c>, <c>Categories</c>, <c>Payees</c>,
    /// <c>Transactions</c> — all require.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
