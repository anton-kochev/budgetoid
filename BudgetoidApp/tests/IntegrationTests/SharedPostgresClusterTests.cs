using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins what the shared cluster itself is, as opposed to what any one test does inside it.
/// </summary>
public sealed class SharedPostgresClusterTests
{
    /// <summary>
    /// The suite runs against the same PostgreSQL major as production.
    /// </summary>
    /// <remarks>
    /// Production runs PostgreSQL 18. Catalog shapes the provisioning checks read — constraints
    /// listed in <c>pg_constraint</c>, ACL rows, default privileges — move between majors, so a
    /// suite green on another major is evidence about a database nobody ships on.
    /// </remarks>
    [Test]
    public async Task Cluster_RunsTheProductionMajor()
    {
        // Arrange
        string connectionString = await SharedPostgresCluster.CreateDatabaseAsync();

        try
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("select current_setting('server_version_num')::int / 10000", connection);

            // Act
            int major = (int)(await command.ExecuteScalarAsync())!;

            // Assert
            await Assert.That(major).IsEqualTo(18);
        }
        finally
        {
            await SharedPostgresCluster.DropDatabaseAsync(connectionString);
        }
    }
}
