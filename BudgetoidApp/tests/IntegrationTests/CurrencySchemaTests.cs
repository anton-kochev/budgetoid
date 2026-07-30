using Domain.Accounts;
using Domain.Currencies;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the currencies table's own rules — the value checks on a row, and what a row's
/// dependants do to its deletability — separately from <see cref="CurrencyIntegrationTests" />,
/// which drives the same entity through the API. Every statement under test is raw Npgsql on
/// purpose: <see cref="Currency" /> refuses the same values client-side and EF refuses a Restrict
/// delete client-side, so neither would reach PostgreSQL and neither would prove anything about
/// the constraint.
/// </summary>
public sealed class CurrencySchemaTests
{
    [Test]
    [Arguments(-1)]
    [Arguments(5)]
    public async Task Database_RejectsAMinorUnitOutsideTheZeroToFourRange(int minorUnit)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, "ZZZ", "Test Currency", "Z", minorUnit);

        // Assert — the constraint name is asserted next to the SQLSTATE because every other check on
        // this table raises 23514 as well, and the test would otherwise pass on the wrong rejection.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_currencies_minor_unit");
    }

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    public async Task Database_AcceptsAMinorUnitAtEachEndOfTheRange(int minorUnit)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — both ends are legitimate data: JPY ships seeded with 0, and a bound written
        // exclusive on either side would refuse a currency the domain accepts.
        await InsertCurrencyAsync(connection, "ZZZ", "Test Currency", "Z", minorUnit);

        // Assert
        await Assert.That(await CountCurrencyAsync(connection, "ZZZ")).IsEqualTo(1L);
    }

    [Test]
    [Arguments("usd")]
    [Arguments("Us")]
    [Arguments("U5D")]
    public async Task Database_RejectsACurrencyCodeThatIsNotThreeUppercaseLetters(string code)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — all three fit varchar(3), so the column type accepts them and only the check refuses
        // them. A fourth character would be truncation (22001), a different rule with a different
        // owner.
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, code, "Test Currency", "Z", 2);

        // Assert
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_currencies_code");
    }

    [Test]
    public async Task Database_AcceptsAThreeLetterUppercaseCode()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — without this case a pattern that matches nothing would look correct, and the seeded
        // ISO list would be the only evidence otherwise.
        await InsertCurrencyAsync(connection, "ZZZ", "Test Currency", "Z", 2);

        // Assert
        await Assert.That(await CountCurrencyAsync(connection, "ZZZ")).IsEqualTo(1L);
    }

    [Test]
    public async Task Database_RefusesToDeleteACurrencyAnAccountIsDenominatedIn()
    {
        // Arrange — one budget, one account denominated in USD. That single reference is the whole
        // fixture; nothing else in the schema points at a currency until an account does.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using (BudgetoidDbContext db = CreateDb(host, budgetId))
        {
            db.Accounts.Add(Account.Create(
                budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant));
            await db.SaveChangesAsync();
        }

        // Act — on its face this is a rule nothing could hit: currencies arrive through HasData and
        // no application path deletes one. The scenario it guards is deploy-time. Pruning an entry
        // from the HasData seed makes `dotnet ef migrations add` emit a DELETE in the regenerated
        // baseline, and that DELETE runs against production, where an account may already be
        // denominated in the code being pruned. This test is what makes that failure appear in CI
        // rather than half-way through a migration run.
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        PostgresException exception = await ThrowsOnDeletingCurrencyAsync(connection, "USD");

        // Assert — the constraint name is pinned alongside the SQLSTATE because budgets.
        // base_currency_code references the same table under the same Restrict rule. It is null on
        // every budget today, so only the account can be refusing here; naming the constraint is
        // what keeps that true if a future provisioning change starts stamping a base currency.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(exception.ConstraintName)
            .IsEqualTo("FK_accounts_currencies_currency_code");

        // A refused statement rolls back whole. A refusal that had already removed the currency on
        // its way to failing would leave the account dangling, and a SQLSTATE-only assertion cannot
        // see that.
        await Assert.That(await CountCurrencyAsync(connection, "USD")).IsEqualTo(1L);
    }

    /// <summary>
    /// Minor unit of the USD account the delete test seeds. Precision is not what that test is
    /// about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// Fixed UTC instant for the seeded account. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Builds a context bound to an ambient budget, which the budget-isolated <c>Accounts</c> set
    /// requires.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    /// <summary>
    /// Separate from <see cref="ThrowsPostgresExceptionAsync" />, which is insert-shaped: the delete
    /// carries no currency values to parameterise and refuses for a different class of reason.
    /// </summary>
    private static async Task<PostgresException> ThrowsOnDeletingCurrencyAsync(
        NpgsqlConnection connection,
        string code)
    {
        await using NpgsqlCommand command = new(
            "delete from currencies where code = @code",
            connection);
        command.Parameters.AddWithValue("code", code);

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

    private static async Task InsertCurrencyAsync(
        NpgsqlConnection connection,
        string code,
        string name,
        string symbol,
        int minorUnit)
    {
        await using NpgsqlCommand command = BuildInsert(connection, code, name, symbol, minorUnit);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        NpgsqlConnection connection,
        string code,
        string name,
        string symbol,
        int minorUnit)
    {
        await using NpgsqlCommand command = BuildInsert(connection, code, name, symbol, minorUnit);

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

    private static NpgsqlCommand BuildInsert(
        NpgsqlConnection connection,
        string code,
        string name,
        string symbol,
        int minorUnit)
    {
        NpgsqlCommand command = new(
            """
            insert into currencies (code, name, symbol, minor_unit)
            values (@code, @name, @symbol, @minor_unit)
            """,
            connection);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("symbol", symbol);
        command.Parameters.AddWithValue("minor_unit", minorUnit);
        return command;
    }

    private static async Task<long> CountCurrencyAsync(NpgsqlConnection connection, string code)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from currencies where code = @code",
            connection);
        command.Parameters.AddWithValue("code", code);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from 'currencies', got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
