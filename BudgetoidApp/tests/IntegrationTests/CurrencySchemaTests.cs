using Domain.Currencies;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the currencies table's value rules from the schema's own side, separately from
/// <see cref="CurrencyIntegrationTests" />, which drives the same entity through the API. Every
/// write here is raw Npgsql on purpose: <see cref="Currency" /> refuses the same values
/// client-side, so an EF-based insert never reaches PostgreSQL and would prove nothing about the
/// constraint.
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
