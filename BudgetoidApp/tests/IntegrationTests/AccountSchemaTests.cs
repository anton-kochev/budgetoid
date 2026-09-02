using System.Globalization;
using Domain.Accounts;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Covers the accounts table's value rules from the schema's own side. Every write here is raw
/// Npgsql on purpose: <see cref="Account" /> refuses the same values client-side, so an EF-based
/// insert never reaches PostgreSQL and would prove nothing about the constraint. Rules that live
/// only in the domain are rules a raw INSERT walks past.
/// </summary>
public sealed class AccountSchemaTests
{
    [Test]
    public async Task Database_RejectsAnAccountTypeOutsideTheDefinedSet()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — "Investment" fits varchar(20), so the column type accepts it and only the check
        // refuses it.
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, budgetId, "Brokerage", "Investment", 0m);

        // Assert — the constraint name is asserted next to the SQLSTATE because every other check on
        // this table raises 23514 as well, and the test would otherwise pass on the wrong rejection.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_accounts_type");
    }

    [Test]
    public async Task Database_AcceptsEveryAccountTypeTheDomainDefines()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        AccountType[] definedTypes = Enum.GetValues<AccountType>();

        // Act — driven off the enum rather than a hand-written list, so adding a member without
        // widening the constraint fails here instead of in production.
        foreach (AccountType accountType in definedTypes)
        {
            await InsertAccountAsync(
                connection, budgetId, accountType.ToString(), accountType.ToString(), 0m);
        }

        // Assert
        await Assert.That(await CountAccountsAsync(connection, budgetId))
            .IsEqualTo((long)definedTypes.Length);
    }

    /// <summary>
    /// A blind index of any width but exactly 32 bytes is refused by
    /// <c>CK_accounts_name_key_length</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>31 AND 33, because one of them alone measures a ceiling rather than a width.</b> The
    /// constraint is written <c>= 32</c> and both <c>SchemaConstraintSnapshotTests</c> and
    /// <c>BudgetoidDbContextConstructionTests</c> pin that as text — but a pin is not a firing. Until
    /// this case existed, <c>&lt;= 32</c> would have shipped green in every sense that matters: it
    /// refuses 33 exactly as the equality does, and admits a 31-byte digest that stores, reads back,
    /// keys perfectly through <c>IX_accounts_budget_id_name_key</c>, never collides, and matches no
    /// account the client will ever look for. Nothing on this side can recompute it to notice, because
    /// the index key lives in a browser.
    /// </para>
    /// <para>
    /// The envelope, the type and the balance beside it are all legal, because PostgreSQL reports a
    /// multiply-violating row under whichever constraint sorts first alphabetically. The three
    /// <c>CK_accounts_name*</c> constraints sort <c>key_length</c>, <c>length</c>, <c>version</c>, so
    /// the width would still be reported here — but <c>opening_balance</c> and <c>type</c> sort after
    /// it and a malformed name would not, and a case that leans on the alphabet to pick the right
    /// answer is one edit away from asserting the wrong refusal while looking like it passed.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(31)]
    [Arguments(33)]
    public async Task Database_RefusesABlindIndexThatIsNotExactlyThirtyTwoBytes(int width)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Non-vacuity, and it has to come first: the same insert with the one legal width goes
        // through, so the refusal below is about the width and not about the statement.
        await InsertAccountAsync(connection, budgetId, "Everyday", "Checking", 0m);

        // Act
        PostgresException refusal = await ThrowsPostgresExceptionAsync(
            connection, budgetId, "Wrong width", "Checking", 0m, new byte[width]);

        // Assert — the constraint is named beside the SQLSTATE because every other check on this table
        // raises 23514 as well, and the case would otherwise pass on the wrong rejection.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo("CK_accounts_name_key_length");
        await Assert.That(refusal.TableName).IsEqualTo("accounts");
    }

    [Test]
    [Arguments("1000000000.01")]
    [Arguments("-1000000000.01")]
    public async Task Database_RejectsAnOpeningBalanceBeyondTheMagnitudeLimit(string openingBalance)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — both signs, because the rule is on the magnitude and a constraint written without
        // abs() would refuse only one of them.
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, budgetId, "Checking", "Checking", Money(openingBalance));

        // Assert
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_accounts_opening_balance");
    }

    [Test]
    [Arguments("1000000000")]
    [Arguments("-1000000000")]
    public async Task Database_AcceptsAnOpeningBalanceAtTheMagnitudeLimit(string openingBalance)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — the domain refuses only above this value, so the limit itself is legitimate data.
        // Without this case a constraint written with < instead of <= would look correct.
        await InsertAccountAsync(
            connection, budgetId, "Checking", "Checking", Money(openingBalance));

        // Assert
        await Assert.That(await CountAccountsAsync(connection, budgetId)).IsEqualTo(1L);
    }

    [Test]
    public async Task OpeningBalanceColumn_UsesNumeric14Scale4()
    {
        // Arrange — the twin of TransactionRepositoryTests.AmountColumn_UsesNumeric14Scale4. Scale 4
        // because the minor unit belongs to the currency, not the column: BHD and KWD have three
        // decimal places and numeric scale rounds silently rather than refusing, so a scale-2 column
        // would corrupt a balance instead of rejecting it. The two money columns must not drift
        // apart, which is why this assertion exists separately rather than being assumed.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        await using NpgsqlCommand command = new(
            """
            select numeric_precision, numeric_scale
            from information_schema.columns
            where table_name = 'accounts' and column_name = 'opening_balance'
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        // Assert
        await Assert.That(reader.GetInt32(0)).IsEqualTo(14);
        await Assert.That(reader.GetInt32(1)).IsEqualTo(4);
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Parses a money literal the culture-invariant way. The values arrive as strings because
    /// <c>decimal</c> is not a legal attribute argument type.
    /// </summary>
    private static decimal Money(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static async Task InsertAccountAsync(
        NpgsqlConnection connection,
        Guid budgetId,
        string name,
        string type,
        decimal openingBalance,
        byte[]? nameKey = null)
    {
        await using NpgsqlCommand command =
            BuildInsert(connection, budgetId, name, type, openingBalance, nameKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        NpgsqlConnection connection,
        Guid budgetId,
        string name,
        string type,
        decimal openingBalance,
        byte[]? nameKey = null)
    {
        await using NpgsqlCommand command =
            BuildInsert(connection, budgetId, name, type, openingBalance, nameKey);

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

    /// <remarks>
    /// <paramref name="nameKey" /> is <see langword="null" /> for "derive it from the label", which is
    /// what every case but the width one wants. It is a parameter at all because the blind index's
    /// width cannot be reached any other way: the fixture only ever emits the legal 32 bytes, so a
    /// case about a wrong width has to hand its own bytes in.
    /// </remarks>
    private static NpgsqlCommand BuildInsert(
        NpgsqlConnection connection,
        Guid budgetId,
        string name,
        string type,
        decimal openingBalance,
        byte[]? nameKey = null)
    {
        NpgsqlCommand command = new(
            """
            insert into accounts (id, budget_id, name, name_key, type, opening_balance, currency_code, created_at_utc)
            values (@id, @budget_id, @name, @name_key, @type, @opening_balance, @currency_code, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("budget_id", budgetId);
        // BOTH HALVES, through the shared fixture. `name` is the label these cases tell their rows
        // apart by, not a name: the column is bytea, so text in it is 42804 from the type checker, and
        // name_key is NOT NULL. Every case in this file is about the type, the balance or the column's
        // numeric shape, so either refusal would arrive as a seeding failure wearing the SQLSTATE the
        // case was hunting.
        command.Parameters.AddWithValue("name", SealedNarrative.Name(name).Envelope.ToArray());
        command.Parameters.AddWithValue(
            "name_key", nameKey ?? SealedNarrative.BlindIndex(name).ToArray());
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("opening_balance", openingBalance);
        command.Parameters.AddWithValue("currency_code", "USD");
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return command;
    }

    private static async Task<long> CountAccountsAsync(NpgsqlConnection connection, Guid budgetId)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from accounts where budget_id = @id",
            connection);
        command.Parameters.AddWithValue("id", budgetId);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from 'accounts', got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
