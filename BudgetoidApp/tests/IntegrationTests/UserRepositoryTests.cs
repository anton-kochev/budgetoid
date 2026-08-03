using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the identity rules the users and credentials tables hold between them, from both sides:
/// the schema that rejects a duplicate, an over-long or a mis-shaped row, and
/// <see cref="UserRepository"/>'s translation of those rejections into the one outcome it can
/// report honestly — <see langword="false"/>, the pair was refused. Which refusal it was is the
/// caller's question, not this layer's.
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
        await InsertUserAsync(connection, "google-1", "sam@example.com");

        // Act — raw Npgsql on purpose: the subject is the unique index itself, and an EF-based
        // insert would only prove what UserRepository does with the violation, not that the database
        // raises one.
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, "google-2", "sam@example.com");

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
        await InsertUserAsync(connection, "google-1", "Sam@example.com");

        // Act
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, "google-2", "sam@example.com");

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
        await InsertUserAsync(connection, "google-1", "josé@example.com");
        await InsertUserAsync(connection, "google-2", "jose@example.com");

        // Assert — case_insensitive is ICU und-u-ks-level2, which folds case but not accents. This
        // pins the real behaviour so nobody later reports it as a bug and "fixes" it to level1,
        // which would silently start refusing a legitimate second account.
        await Assert.That(await CountRowsAsync(connection, "users")).IsEqualTo(2L);
    }

    [Test]
    public async Task Database_RejectsASecondFederatedCredentialForTheSameProviderSubject()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await InsertUserAsync(connection, "google-1", "first@example.com");

        // Act — a second account, so nothing but the credential index can refuse this.
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, "google-1", "second@example.com");

        // Assert — one account per provider identity. Without this the same Google user could end
        // up with two accounts, and FindByFederatedCredentialAsync's SingleOrDefault would start
        // throwing on a sign-in that used to work.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
    }

    [Test]
    public async Task Database_RejectsAUserValueLongerThanItsColumn()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        string email = EmailOfLength(Email.MaxLength + 1);

        // Act
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, "google-1", email);

        // Assert — 22001 is a rejection, which is what "the database enforces it" has to mean. The
        // contrast worth remembering is numeric scale, which silently rounds instead of refusing and
        // therefore cannot own its rule; see docs/decisions/0002 for the worked example.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.StringDataRightTruncation);
    }

    [Test]
    [Arguments("subject", Credential.MaxSubjectLength)]
    [Arguments("provider", Credential.MaxProviderLength)]
    public async Task Database_RejectsACredentialValueLongerThanItsColumn(string column, int maxLength)
    {
        // Arrange — the users row lands first, so the only thing left to refuse is the credential.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");
        string provider = column == "provider"
            ? new string('p', maxLength + 1)
            : Credential.GoogleProvider;
        string subject = column == "subject"
            ? new string('s', maxLength + 1)
            : "google-1";

        // Act
        PostgresException exception = await ThrowsCredentialPostgresExceptionAsync(
            connection, userId, CredentialTypes.Federated, provider, subject);

        // Assert — the subject bound is Google's documented maximum for the `sub` claim, so a
        // provider that grows its identifiers has to be noticed here rather than silently truncated.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.StringDataRightTruncation);
    }

    [Test]
    public async Task Database_RejectsAFederatedCredentialWithNoSubject()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");

        // Act
        PostgresException exception = await ThrowsCredentialPostgresExceptionAsync(
            connection, userId, CredentialTypes.Federated, Credential.GoogleProvider, subject: null);

        // Assert — a federated credential with nothing to match on would be invisible to every
        // sign-in while still occupying the account, so the shape check refuses it outright.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
    }

    [Test]
    public async Task Database_RejectsAPasskeyCredentialCarryingASubject()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");

        // Act
        PostgresException exception = await ThrowsCredentialPostgresExceptionAsync(
            connection, userId, CredentialTypes.Passkey, provider: null, subject: "google-1");

        // Assert — the other arm of the same check. A passkey is held by the authenticator, not
        // granted by an issuer, so an issuer's identifier on one is a row nobody can interpret.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
    }

    [Test]
    public async Task Database_RejectsAnUnknownCredentialType()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");

        // Act
        PostgresException exception = await ThrowsCredentialPostgresExceptionAsync(
            connection, userId, "password", provider: null, subject: null);

        // Assert — the type column is varchar rather than a PostgreSQL enum, so this CHECK is the
        // only thing standing between the vocabulary and any string a writer felt like storing.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
    }

    [Test]
    public async Task Database_AcceptsTwoPasskeyCredentialsForTheSameUser()
    {
        // Arrange — the account already has its Google credential, so this adds a third and fourth
        // row to the same user.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");
        await InsertCredentialAsync(
            connection, userId, CredentialTypes.Federated, Credential.GoogleProvider, "google-1");

        // Act
        await InsertCredentialAsync(connection, userId, CredentialTypes.Passkey, null, null);
        await InsertCredentialAsync(connection, userId, CredentialTypes.Passkey, null, null);

        // Assert — FR-043: an account may hold more than one credential. Two passkey rows also
        // prove the (provider, subject) index really is partial: both carry (NULL, NULL), which an
        // unfiltered NULLS NOT DISTINCT index would have collapsed into a duplicate.
        await Assert.That(await CountRowsAsync(connection, "credentials")).IsEqualTo(3L);
    }

    [Test]
    public async Task FindByFederatedCredentialAsync_WithAKnownSubject_ReturnsTheUser()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act
        User? found = await repository.FindByFederatedCredentialAsync(
            Credential.GoogleProvider, "google-1");

        // Assert
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(userId);
    }

    [Test]
    public async Task FindByFederatedCredentialAsync_WithAnUnknownSubject_ReturnsNull()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act
        User? found = await repository.FindByFederatedCredentialAsync(
            Credential.GoogleProvider, "google-2");

        // Assert — null rather than a throw, and it has to stay that way: the handler reads exactly
        // this to decide between "first sign-in" and "someone else holds the email".
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task TryAddAsync_WithADuplicateCredential_ReturnsFalse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-1", "first@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act — a lost race on the provider identity, which the provisioning handler resolves by
        // re-reading that credential, so it must surface as false rather than as a throw.
        bool added = await repository.TryAddAsync(
            NewUser("second@example.com", out Guid userId),
            NewGoogleCredential(userId, "google-1"));

        // Assert
        await Assert.That(added).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await verify.Credentials.CountAsync()).IsEqualTo(1);
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
            NewUser("shared@example.com", out Guid userId),
            NewGoogleCredential(userId, "google-2"));

        // Assert — the repository deliberately declines to decide what this refusal meant. From
        // here, a stranger holding the email and a request that raced itself look the same; the only
        // thing on offer is a constraint name PostgreSQL picks by index order, which this layer does
        // not control and which carries no information about what happened. The caller's re-read by
        // credential is what separates the two, so the answer this method owes is just "refused".
        await Assert.That(added).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await verify.Credentials.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task TryAddAsync_WithADuplicateCredentialAndEmail_ReturnsFalse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act — the single-threaded reduction of a concurrent sign-in: the same person's insert
        // arriving after their own pair has landed, so it carries the same subject and the same
        // email and violates both unique rules at once.
        bool added = await repository.TryAddAsync(
            NewUser("person@example.com", out Guid userId),
            NewGoogleCredential(userId, "google-1"));

        // Assert — PostgreSQL names only one constraint for this pair, and which one is decided by
        // the order the baseline migration happens to create the two indexes in, not by what
        // happened. A test asserting a particular name here would be pinning migration ordering
        // rather than a rule, and code branching on that name would be reading an accident as a
        // fact. So the outcome is false, exactly as for any other refused insert.
        await Assert.That(added).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await verify.Credentials.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task TryAddAsync_WhenOnlyTheCredentialCollides_LeavesNoOrphanedUserRow()
    {
        // Arrange — a winning account holds this provider identity; the loser arrives with a fresh
        // email, so the users row on its own would be perfectly insertable.
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-1", "winner@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act
        bool added = await repository.TryAddAsync(
            NewUser("loser@example.com", out Guid userId),
            NewGoogleCredential(userId, "google-1"));

        // Assert — the count is the whole point, not a second opinion on the boolean. The two rows
        // go in one save so that a refusal leaves neither behind: a users row persisted without its
        // credential would hold "loser@example.com" under the unique email index forever while no
        // credential resolved to it, so every later sign-in with that address would be refused with
        // a 409 and no way to heal. Splitting the save would keep this method returning false and
        // break only this line.
        await Assert.That(added).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await verify.Credentials.CountAsync()).IsEqualTo(1);
    }

    /// <summary>
    /// The <c>type</c> values the schema recognises, spelled as the column stores them. Held here
    /// rather than read off <c>CredentialType</c> because the raw-SQL tests below have to be able to
    /// write a value the enum cannot express.
    /// </summary>
    private static class CredentialTypes
    {
        public const string Federated = "federated";
        public const string Passkey = "passkey";
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime"/>, so <see cref="DateTimeKind.Utc"/> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Builds a user and hands back its generated id, which the credential needs before either row
    /// is saved.
    /// </summary>
    private static User NewUser(string email, out Guid userId)
    {
        User user = User.Create(email, SeedInstant);
        userId = user.Id;
        return user;
    }

    private static Credential NewGoogleCredential(Guid userId, string subject) =>
        Credential.CreateFederated(userId, Credential.GoogleProvider, subject, SeedInstant);

    /// <summary>
    /// Builds a syntactically plausible address of exactly <paramref name="length"/> characters by
    /// padding the local part.
    /// </summary>
    private static string EmailOfLength(int length)
    {
        const string domain = "@example.com";
        return new string('a', length - domain.Length) + domain;
    }

    /// <summary>
    /// Writes the pair — the users row and the federated credential that resolves to it — the way
    /// production writes it, so that a test naming one identity keeps meaning one account.
    /// </summary>
    private static async Task InsertUserAsync(
        NpgsqlConnection connection,
        string googleSubject,
        string email)
    {
        Guid userId = await InsertUserRowAsync(connection, email);
        await InsertCredentialAsync(
            connection, userId, CredentialTypes.Federated, Credential.GoogleProvider, googleSubject);
    }

    private static async Task<Guid> InsertUserRowAsync(NpgsqlConnection connection, string email)
    {
        Guid userId = Guid.CreateVersion7();
        await using NpgsqlCommand command = BuildUserInsert(connection, userId, email);
        await command.ExecuteNonQueryAsync();
        return userId;
    }

    private static async Task InsertCredentialAsync(
        NpgsqlConnection connection,
        Guid userId,
        string type,
        string? provider,
        string? subject)
    {
        await using NpgsqlCommand command = BuildCredentialInsert(
            connection, userId, type, provider, subject);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Attempts the pair and returns whichever of the two inserts PostgreSQL refused.
    /// </summary>
    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        NpgsqlConnection connection,
        string googleSubject,
        string email)
    {
        Guid userId = Guid.CreateVersion7();

        try
        {
            await using NpgsqlCommand user = BuildUserInsert(connection, userId, email);
            await user.ExecuteNonQueryAsync();
            await using NpgsqlCommand credential = BuildCredentialInsert(
                connection, userId, CredentialTypes.Federated, Credential.GoogleProvider, googleSubject);
            await credential.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected PostgresException.");
    }

    private static async Task<PostgresException> ThrowsCredentialPostgresExceptionAsync(
        NpgsqlConnection connection,
        Guid userId,
        string type,
        string? provider,
        string? subject)
    {
        await using NpgsqlCommand command = BuildCredentialInsert(
            connection, userId, type, provider, subject);

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

    private static NpgsqlCommand BuildUserInsert(
        NpgsqlConnection connection,
        Guid userId,
        string email)
    {
        NpgsqlCommand command = new(
            """
            insert into users (id, email, created_at_utc)
            values (@id, @email, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return command;
    }

    private static NpgsqlCommand BuildCredentialInsert(
        NpgsqlConnection connection,
        Guid userId,
        string type,
        string? provider,
        string? subject)
    {
        NpgsqlCommand command = new(
            """
            insert into credentials (id, user_id, type, provider, subject, created_at_utc)
            values (@id, @user_id, @type, @provider, @subject, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("provider", (object?)provider ?? DBNull.Value);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return command;
    }

    private static async Task<long> CountRowsAsync(NpgsqlConnection connection, string table)
    {
        await using NpgsqlCommand command = new($"select count(*) from {table}", connection);

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
    /// Builds a context with no ambient budget, which is safe here because neither <c>User</c> nor
    /// <c>Credential</c> carries a budget query filter.
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
