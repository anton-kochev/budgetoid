using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the users table's identity rules from both sides: the schema that rejects a duplicate or
/// over-long value, and <see cref="UserRepository"/>'s translation of those rejections into the one
/// outcome it can report honestly — <see langword="false"/>, the row was refused. Which refusal it
/// was is the caller's question, not this layer's.
/// </summary>
public sealed class UserRepositoryTests
{
    [Test]
    public async Task Database_RejectsASecondUserWithTheSameEmail()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await InsertUserAsync(connection, "google-1", "sam@example.com", "Sam");

        // Act — raw Npgsql on purpose: the subject is the unique index itself, and an EF-based
        // insert would only prove what UserRepository does with the violation, not that the database
        // raises one.
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, "google-2", "sam@example.com", "Sam Again");

        // Assert
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
    }

    [Test]
    public async Task Database_RejectsASecondUserWhoseEmailDiffersOnlyByCase()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await InsertUserAsync(connection, "google-1", "Sam@example.com", "Sam");

        // Act
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, "google-2", "sam@example.com", "Sam Again");

        // Assert — this is the case_insensitive collation doing its work, and pg_get_indexdef does
        // not render it, so without this test the collation could be dropped from
        // UserConfiguration with every line of the unique-index snapshot staying byte-identical.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
    }

    [Test]
    public async Task Database_AcceptsTwoUsersWhoseEmailsDifferOnlyByAccent()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        await InsertUserAsync(connection, "google-1", "josé@example.com", "José");
        await InsertUserAsync(connection, "google-2", "jose@example.com", "Jose");

        // Assert — case_insensitive is ICU und-u-ks-level2, which folds case but not accents. This
        // pins the real behaviour so nobody later reports it as a bug and "fixes" it to level1,
        // which would silently start refusing a legitimate second account.
        await Assert.That(await CountUsersAsync(connection)).IsEqualTo(2L);
    }

    [Test]
    [Arguments("google_subject", User.MaxGoogleSubjectLength)]
    [Arguments("email", Email.MaxLength)]
    [Arguments("display_name", User.MaxDisplayNameLength)]
    public async Task Database_RejectsAValueLongerThanItsColumn(string column, int maxLength)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        string googleSubject = column == "google_subject"
            ? new string('s', maxLength + 1)
            : "google-1";
        string email = column == "email"
            ? EmailOfLength(maxLength + 1)
            : "person@example.com";
        string displayName = column == "display_name"
            ? new string('d', maxLength + 1)
            : "Person";

        // Act
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, googleSubject, email, displayName);

        // Assert — 22001 is a rejection, which is what "the database enforces it" has to mean. The
        // contrast worth remembering is numeric scale, which silently rounds instead of refusing and
        // therefore cannot own its rule; see docs/decisions/0002 for the worked example.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.StringDataRightTruncation);
    }

    [Test]
    public async Task TryAddAsync_WithADuplicateGoogleSubject_ReturnsFalse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-1", "first@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act — a lost race on the identity column, which the provisioning handler resolves by
        // re-reading that subject, so it must surface as false rather than as a throw.
        bool added = await repository.TryAddAsync(
            User.Create("google-1", "second@example.com", "Second", SeedInstant));

        // Assert
        await Assert.That(added).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task TryAddAsync_WithAnEmailAnotherAccountHolds_ReturnsFalse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-1", "shared@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act
        bool added = await repository.TryAddAsync(
            User.Create("google-2", "shared@example.com", "Second", SeedInstant));

        // Assert — the repository deliberately declines to decide what this refusal meant. From
        // here, a stranger holding the email and a request that raced itself look the same; the only
        // thing on offer is a constraint name PostgreSQL picks by index order, which this layer does
        // not control and which carries no information about what happened. The caller's re-read by
        // google subject is what separates the two, so the answer this method owes is just "refused".
        await Assert.That(added).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task TryAddAsync_WithADuplicateGoogleSubjectAndEmail_ReturnsFalse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act — the single-threaded reduction of a concurrent sign-in: the same person's insert
        // arriving after their own row has landed, so it carries the same subject and the same
        // email and violates both unique indexes at once.
        bool added = await repository.TryAddAsync(
            User.Create("google-1", "person@example.com", "Person", SeedInstant));

        // Assert — PostgreSQL names only one constraint for this row, the lower-OID one, and the
        // OIDs follow the order the regenerated baseline migration happens to create the two
        // indexes in. A test asserting a particular name here would be pinning migration ordering
        // rather than a rule, and code branching on that name would be reading an accident as a
        // fact. So the outcome is false, exactly as for any other refused insert.
        await Assert.That(added).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task UpdateProfileAsync_WithAFreeEmail_ReturnsTrueAndPersists()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "old@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);
        User user = (await repository.FindByGoogleSubjectAsync("google-1"))!;
        user.UpdateProfile("new@example.com", "New");

        // Act
        bool updated = await repository.UpdateProfileAsync(user);

        // Assert
        await Assert.That(updated).IsTrue();
        await using BudgetoidDbContext verify = CreateDb(host);
        User persisted = await verify.Users.SingleAsync(candidate => candidate.Id == userId);
        await Assert.That(persisted.Email.Value).IsEqualTo("new@example.com");
        await Assert.That(persisted.DisplayName).IsEqualTo("New");
    }

    [Test]
    public async Task UpdateProfileAsync_WithAnEmailAnotherUserHolds_RestoresTheCallersInstance()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-other", "taken@example.com");
        await host.SeedUserAsync("google-1", "mine@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);
        User user = (await repository.FindByGoogleSubjectAsync("google-1"))!;
        user.UpdateProfile("mine@example.com", "Stored");
        await repository.UpdateProfileAsync(user);
        user.UpdateProfile("taken@example.com", "Fresh");

        // Act
        bool updated = await repository.UpdateProfileAsync(user);

        // Assert — asserted on the caller's own reference, not on a re-read: ReloadAsync resetting
        // the change tracker while leaving this object holding the rejected email is a real and
        // easy-to-miss bug, and the handler reads exactly this object to decide what the session
        // signs in as. No throw either — a stale cached IdP attribute must not fail a sign-in.
        await Assert.That(updated).IsFalse();
        await Assert.That(user.Email.Value).IsEqualTo("mine@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("Stored");
    }

    [Test]
    public async Task UpdateProfileAsync_AfterARejectedRefresh_LeavesTheContextUsable()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-other", "taken@example.com");
        await host.SeedUserAsync("google-1", "mine@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);
        User user = (await repository.FindByGoogleSubjectAsync("google-1"))!;
        user.UpdateProfile("taken@example.com", "Fresh");
        await repository.UpdateProfileAsync(user);

        // Act — the sign-in continues on this same scoped context, which goes on to provision the
        // default budget. A rejected change left pending would be replayed by the next save.
        bool added = await repository.TryAddAsync(
            User.Create("google-3", "third@example.com", "Third", SeedInstant));

        // Assert
        await Assert.That(added).IsTrue();
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(3);
        User persisted = await verify.Users.SingleAsync(candidate => candidate.GoogleSubject == "google-1");
        await Assert.That(persisted.Email.Value).IsEqualTo("mine@example.com");
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime"/>, so <see cref="DateTimeKind.Utc"/> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Builds a syntactically plausible address of exactly <paramref name="length"/> characters by
    /// padding the local part.
    /// </summary>
    private static string EmailOfLength(int length)
    {
        const string domain = "@example.com";
        return new string('a', length - domain.Length) + domain;
    }

    private static async Task InsertUserAsync(
        NpgsqlConnection connection,
        string googleSubject,
        string email,
        string? displayName)
    {
        await using NpgsqlCommand command = BuildInsert(connection, googleSubject, email, displayName);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        NpgsqlConnection connection,
        string googleSubject,
        string email,
        string? displayName)
    {
        await using NpgsqlCommand command = BuildInsert(connection, googleSubject, email, displayName);

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
        string googleSubject,
        string email,
        string? displayName)
    {
        NpgsqlCommand command = new(
            """
            insert into users (id, google_subject, email, display_name, created_at_utc)
            values (@id, @google_subject, @email, @display_name, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("google_subject", googleSubject);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("display_name", (object?)displayName ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return command;
    }

    private static async Task<long> CountUsersAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new("select count(*) from users", connection);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from 'users', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Builds a context with no ambient budget, which is safe here because <c>User</c> carries no
    /// budget query filter.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
