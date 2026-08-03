using Domain.Accounts;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the immutability rules that only the application role's column grants can enforce: a
/// budgets row is never updated at all, an account's currency never changes, and a credential —
/// the row the whole sign-in resolves through — is created whole and never edited, every one of
/// its columns immutable because the role holds no <c>UPDATE</c> grant on that table of any
/// shape. The role's <c>UPDATE</c> grant names its columns explicitly, and PostgreSQL column
/// privileges are
/// additive, so an immutable column is one that is simply absent from the list; writing it fails
/// with <c>42501</c> before the row is touched. Every statement here is raw Npgsql on
/// <see cref="RepositoryTestHost.AppConnectionString" />, because grants only bind connections
/// opened as the role — the host's own connection is the container superuser and answers every
/// privilege question with yes.
/// </summary>
/// <remarks>
/// Each refusal is paired, in the same test, with a write that must succeed on the same
/// connection. Without the pair the <c>42501</c> is vacuous: a role with no <c>UPDATE</c> grant
/// at all — or a grants script that is an empty file — refuses everything with the same SQLSTATE.
/// The success half is what pins "exactly this column is immutable" rather than "the role cannot
/// write". On <c>budgets</c> and on <c>credentials</c> no column is updatable — that is the whole
/// content of both rules — so their pair is a permitted <c>INSERT</c> instead: provisioning
/// creates budgets and sign-up creates credentials, and the role must still be able to.
/// </remarks>
public sealed class AppRoleGrantsTests
{
    /// <summary>
    /// Minor unit of the USD account these tests seed. Precision is not what any of them is
    /// about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Database_RefusesEveryUpdateOnABudget_WhileStillAllowingInsert()
    {
        // Arrange — one budget, plus a second real user for the user_id statement below to aim
        // at: if the grant ever leaked user_id, the reassignment would then succeed outright
        // instead of tripping the users foreign key and passing for the wrong reason.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");
        Guid budgetId = await host.SeedAdditionalBudgetAsync(userId, "Household");

        // A bare app-role connection, not RepositoryTestHost.OpenAppConnectionAsync: every
        // statement below targets budgets, which is not one of the budget-owned tables row-level
        // security scopes — a budget is the tenant, not a tenant's row — so there is no ambient
        // budget for this session to carry and setting one would only suggest there was.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        // Act — every column of budgets by name: name, user_id, base_currency_code. The rule is
        // "a budgets row is never updated", and column-for-column is the only shape the grant
        // list can hold that in. USD is a real currencies row, for the same leak-detection reason
        // as the second user.
        PostgresException nameRefusal = await ThrowsPostgresExceptionAsync(
            app, "update budgets set name = @value where id = @id", "Renamed", budgetId);
        PostgresException userRefusal = await ThrowsPostgresExceptionAsync(
            app, "update budgets set user_id = @value where id = @id", otherUserId, budgetId);
        PostgresException currencyRefusal = await ThrowsPostgresExceptionAsync(
            app, "update budgets set base_currency_code = @value where id = @id", "USD", budgetId);

        // Assert
        await Assert.That(nameRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(userRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(currencyRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await SelectScalarAsync(
                admin, "select name from budgets where id = @id", budgetId))
            .IsEqualTo("Household");
        await Assert.That(await SelectScalarAsync(
                admin, "select user_id from budgets where id = @id", budgetId))
            .IsEqualTo(userId);
        await Assert.That(await SelectScalarAsync(
                admin, "select base_currency_code from budgets where id = @id", budgetId))
            .IsEqualTo(DBNull.Value);

        // The success half of the pair (see the class remarks) — an INSERT, because budgets is
        // the one table where no UPDATE column exists to pair with.
        await using NpgsqlCommand insert = new(
            "insert into budgets (id, user_id, name, created_at_utc) " +
            "values (@id, @user_id, @name, @created_at_utc)",
            app);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("name", "Holiday Fund");
        insert.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await Assert.That(await insert.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task Database_RefusesToChangeAnAccountsCurrency_WhileStillAllowingRename()
    {
        // Arrange — a USD account. EUR is a real currencies row seeded by the migration, so if
        // the grant ever leaked currency_code the statement would succeed outright instead of
        // tripping the currency foreign key and passing for the wrong reason.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        Guid accountId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            Account account = Account.Create(
                budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant);
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();
            accountId = account.Id;
        }

        // accounts is row-level-security scoped, so this connection carries the budget the account
        // is in. Without it the rename would match zero rows and still report no error, which would
        // leave the refusal it is paired with proving nothing.
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(budgetId);

        // Act — currency_code is absent from the accounts grant list; name is on it. Same table,
        // same row, same connection: only the column decides.
        PostgresException refusal = await ThrowsPostgresExceptionAsync(
            app, "update accounts set currency_code = @value where id = @id", "EUR", accountId);
        int renamed = await ExecuteAsync(
            app, "update accounts set name = @value where id = @id", "Everyday Checking", accountId);

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(renamed).IsEqualTo(1);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await SelectScalarAsync(
                admin, "select currency_code from accounts where id = @id", accountId))
            .IsEqualTo("USD");
        await Assert.That(await SelectScalarAsync(
                admin, "select name from accounts where id = @id", accountId))
            .IsEqualTo("Everyday Checking");
    }

    [Test]
    public async Task Database_RefusesToChangeAUsersCreatedAt_WhileStillAllowingProfileEdits()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");

        // A bare app-role connection, for the same reason as the budgets test: users sits outside
        // the budget-owned tables row-level security scopes — a user owns budgets rather than
        // belonging to one — so this session has no ambient budget to carry.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        // Act — created_at_utc is an audit fact and immutable by omission. With the identity
        // columns gone from this table it is the only omitted column left, which makes it the one
        // statement that can still tell a real GRANT UPDATE (email, display_name) list apart from
        // a table-wide grant. display_name is on that list.
        PostgresException refusal = await ThrowsPostgresExceptionAsync(
            app, "update users set created_at_utc = @value where id = @id", ForgedInstant, userId);
        int profileEdited = await ExecuteAsync(
            app, "update users set display_name = @value where id = @id", "Person Example", userId);

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(profileEdited).IsEqualTo(1);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await SelectScalarAsync(
                admin, "select created_at_utc from users where id = @id", userId))
            .IsEqualTo(SeedInstant);
        await Assert.That(await SelectScalarAsync(
                admin, "select display_name from users where id = @id", userId))
            .IsEqualTo("Person Example");
    }

    [Test]
    public async Task Database_RefusesEveryCredentialWriteExceptInsert()
    {
        // Arrange — one user with the federated credential SeedUserAsync gives it, plus a second
        // real user for the user_id statement below to aim at: if the grant ever leaked user_id,
        // the reassignment would then succeed outright instead of tripping the users foreign key
        // and passing for the wrong reason.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid credentialId = (Guid)(await SelectScalarAsync(
            admin, "select id from credentials where user_id = @id", userId))!;

        // A bare app-role connection, for the same reason as the users test: credentials sits
        // outside the budget-owned tables row-level security scopes — a credential belongs to no
        // tenant, and provisioning reads it to resolve a sign-in before an ambient budget exists —
        // so this session has no ambient budget to carry.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        // Act — every column of credentials by name. The rule is "a credential is created whole
        // and never edited", and column-for-column is the only shape the absence of an UPDATE
        // grant can be pinned in. Each statement is refused on privilege before the row is
        // reached, so none of them ever meets CK_credentials_type_shape.
        PostgresException subjectRefusal = await ThrowsPostgresExceptionAsync(
            app, "update credentials set subject = @value where id = @id", "google-2", credentialId);
        PostgresException providerRefusal = await ThrowsPostgresExceptionAsync(
            app, "update credentials set provider = @value where id = @id", "apple", credentialId);
        PostgresException typeRefusal = await ThrowsPostgresExceptionAsync(
            app, "update credentials set type = @value where id = @id", "passkey", credentialId);
        PostgresException userRefusal = await ThrowsPostgresExceptionAsync(
            app, "update credentials set user_id = @value where id = @id", otherUserId, credentialId);
        PostgresException createdAtRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update credentials set created_at_utc = @value where id = @id",
            ForgedInstant,
            credentialId);

        // No DELETE grant either, and that omission is the load-bearing half of this test.
        // Revoking a credential is a later story; until it lands, the missing privilege is what
        // stops a bug removing someone's only way back into their account.
        PostgresException deleteRefusal = await ThrowsPostgresExceptionAsync(
            app, "delete from credentials where id = @id", credentialId, credentialId);

        // Assert
        await Assert.That(subjectRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(providerRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(typeRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(userRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(createdAtRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(deleteRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        // The success half of the pair (see the class remarks) — an INSERT, because credentials is
        // the second table where no UPDATE column exists to pair with. One statement buys three
        // things at once: it is the privilege-layer proof that an account may hold more than one
        // credential, it is the passkey arm of CK_credentials_type_shape (no provider, no subject),
        // and it shows the unique index really is partial — two rows with NULL provider and NULL
        // subject coexist under it because its filter names only federated rows.
        await using NpgsqlCommand insert = new(
            "insert into credentials (id, user_id, type, provider, subject, created_at_utc) " +
            "values (@id, @user_id, 'passkey', null, null, @created_at_utc)",
            app);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await Assert.That(await insert.ExecuteNonQueryAsync()).IsEqualTo(1);

        await Assert.That(await SelectScalarAsync(
                admin, "select subject from credentials where id = @id", credentialId))
            .IsEqualTo("google-1");
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from credentials where user_id = @id", userId))
            .IsEqualTo(2L);
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The value an update of an immutable timestamp column would have written had the grant
    /// allowed it. It must differ from <see cref="SeedInstant" />: the read-back asserting the row
    /// still holds <see cref="SeedInstant" /> proves nothing if the two are equal. PostgreSQL
    /// <c>timestamptz</c> rejects a non-UTC <see cref="DateTime" />, so
    /// <see cref="DateTimeKind.Utc" /> is load-bearing here too.
    /// </summary>
    private static readonly DateTime ForgedInstant = new(2031, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>
    /// Sends one <c>update … set column = @value where id = @id</c> statement and returns the
    /// refusal. The SQL is a literal at every call site; the values are parameters, as they must
    /// be.
    /// </summary>
    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        NpgsqlConnection connection,
        string sql,
        object value,
        Guid rowId)
    {
        await using NpgsqlCommand command = BuildWrite(connection, sql, value, rowId);

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

    /// <summary>
    /// Sends the same statement shape as <see cref="ThrowsPostgresExceptionAsync" /> but expects
    /// it to go through, returning the affected-row count for the caller to assert.
    /// </summary>
    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        object value,
        Guid rowId)
    {
        await using NpgsqlCommand command = BuildWrite(connection, sql, value, rowId);
        return await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand BuildWrite(
        NpgsqlConnection connection,
        string sql,
        object value,
        Guid rowId)
    {
        NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("id", rowId);
        return command;
    }

    /// <summary>
    /// Reads one column of one row back so a refusal can be asserted as "nothing changed" and a
    /// permitted write as "this landed" — a rows-affected count alone cannot tell those apart
    /// from a statement that matched no row. A SQL NULL comes back as <see cref="DBNull.Value" />.
    /// </summary>
    private static async Task<object?> SelectScalarAsync(
        NpgsqlConnection connection,
        string sql,
        Guid rowId)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", rowId);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>
    /// Builds a context bound to an ambient budget, which the budget-isolated <c>Accounts</c> set
    /// this file seeds through requires.
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
