using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the identity rules the users and credentials tables hold between them, from both sides:
/// the schema that rejects a duplicate, an over-long or a mis-shaped row, and
/// <see cref="UserRepository"/>'s translation of those rejections into the one outcome it can
/// report honestly — <see langword="false"/>, the pair was refused. Which refusal it was is the
/// caller's question, not this layer's.
/// <para>
/// It also covers the one thing <c>DeleteAsync</c> decides, which is not an identity rule at all:
/// which lost race counts as the post-condition already holding, and which is a failure to report.
/// </para>
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

        // Assert — one account per provider identity, the rule IX_credentials_provider_subject owns.
        // Its neighbour below owns the converse one and the two are easy to read as duplicates: this
        // test uses two different users and one identity, that one uses one user and two identities.
        // Without this the same Google user could end up with two accounts, and
        // FindUserIdByFederatedCredentialAsync's SingleOrDefault would start throwing on a sign-in
        // that used to work.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
    }

    [Test]
    public async Task Database_RejectsASecondFederatedCredentialForTheSameUser()
    {
        // Arrange — one account that already holds its Google credential.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");
        await InsertCredentialAsync(
            connection, userId, CredentialTypes.Federated, Credential.GoogleProvider, "google-1");

        // Act — a different subject, so the provider-identity index above cannot be what refuses it.
        PostgresException exception = await ThrowsCredentialPostgresExceptionAsync(
            connection, userId, CredentialTypes.Federated, Credential.GoogleProvider, "google-2");

        // Assert — one federated credential per account, the rule
        // IX_credentials_user_id_federated owns. Nothing was stopping an account growing a second
        // one; not a feature anyone is adding, but exactly what a bug on a credential-insert path
        // would do, leaving the account with two Google identities that both resolve to it. The name
        // is asserted rather than just the SQLSTATE because this row breaches exactly one index, so
        // it is deterministic here rather than an artifact of creation order — unlike
        // TryAddAsync_WithADuplicateCredentialAndEmail_ReturnsFalse below, which breaches two.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(exception.ConstraintName)
            .IsEqualTo(CredentialConfiguration.FederatedPerUserIndexName);
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
    public async Task Database_RejectsAFederatedCredentialWithAnEmptySubject()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");

        // Act
        PostgresException exception = await ThrowsCredentialPostgresExceptionAsync(
            connection, userId, CredentialTypes.Federated, Credential.GoogleProvider, subject: "");

        // Assert — the empty string is the gap the null test above cannot close: '' is not null, so
        // the shape check used to accept it, and the resulting row was an identity nobody could sign
        // in as while it held a slot in the provider-identity index. The length test is what refuses
        // it, and it has to sit alongside the null test rather than replace it, because length(null)
        // is null and a check evaluating to null is satisfied.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_credentials_type_shape");
    }

    [Test]
    public async Task Database_RejectsAFederatedCredentialWithAnEmptyProvider()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");

        // Act
        PostgresException exception = await ThrowsCredentialPostgresExceptionAsync(
            connection, userId, CredentialTypes.Federated, provider: "", subject: "google-1");

        // Assert — the provider vocabulary, not the shape check, is what refuses this: '' is not
        // null, so the shape check is satisfied. That is why provider needs no length test of its
        // own — the dictionary already excludes every empty and every over-long value, and a row
        // breaching two checks would make the reported name an accident.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_credentials_provider");
    }

    [Test]
    public async Task Database_RejectsAFederatedCredentialWhoseProviderDiffersOnlyByCase()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");

        // Act
        PostgresException exception = await ThrowsCredentialPostgresExceptionAsync(
            connection, userId, CredentialTypes.Federated, provider: "Google", subject: "google-1");

        // Assert — the provider column carries no case-insensitive collation, deliberately, so
        // ('Google', s) and ('google', s) are two rows under IX_credentials_provider_subject and
        // therefore two accounts for one person. This check is the only thing standing between the
        // vocabulary and that outcome; Credential.CreateFederated refuses the same spelling one layer
        // up, for error quality rather than for enforcement.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_credentials_provider");
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

    // The two tests below cover what FindUserIdByFederatedCredentialAsync returns, and deliberately
    // not the other claim its `type = 'federated'` predicate carries — that the predicate is what lets
    // the planner assume the partial unique index's own predicate and use it. Nothing here pins that,
    // and nothing reasonably can: the only evidence is an EXPLAIN plan, and over the handful of rows
    // these tests seed the planner is free to prefer a sequential scan, so the assertion would fail
    // on a correct query. It stays unpinned on purpose rather than for want of a test.
    [Test]
    public async Task FindUserIdByFederatedCredentialAsync_WithAKnownSubject_ReturnsTheUserId()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act — an id and nothing more. This lookup is the one statement that runs before the session
        // has an identity, so the users table is closed to it; credentials.user_id is NOT NULL and
        // references users.id, which means the key already carries everything the dropped join proved.
        Guid? found = await repository.FindUserIdByFederatedCredentialAsync(
            Credential.GoogleProvider, "google-1");

        // Assert
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Value).IsEqualTo(userId);
    }

    [Test]
    public async Task FindUserIdByFederatedCredentialAsync_WithAnUnknownSubject_ReturnsNull()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act
        Guid? found = await repository.FindUserIdByFederatedCredentialAsync(
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
    }

    [Test]
    public async Task TryAddAsync_WhenOnlyTheCredentialCollides_LeavesNoOrphanedUserRow()
    {
        // Arrange — a winning account holds this provider identity; the loser arrives with a fresh
        // email, so the users row on its own would be perfectly insertable.
        const string losingEmail = "loser@example.com";
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync("google-1", "winner@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new UserRepository(db);

        // Act
        bool added = await repository.TryAddAsync(
            NewUser(losingEmail, out Guid userId),
            NewGoogleCredential(userId, "google-1"));

        // Assert — the surviving-row check is the whole point, not a second opinion on the boolean,
        // and it is the only assertion in this class that carries it. The two rows go in one save so
        // that a refusal leaves neither behind: a users row persisted without its credential would
        // hold "loser@example.com" under the unique email index forever while no credential resolved
        // to it, so every later sign-in with that address would be refused with a 409 and no way to
        // heal. Splitting the save would keep this method returning false and break only this line.
        // The losing email by name rather than a row count: a count of one is also satisfied by a
        // seed that never landed or by the winner being deleted, so it would let this test fail for
        // reasons that are not the orphan it exists to catch.
        await Assert.That(added).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        Email orphanEmail = Email.Create(losingEmail);
        await Assert.That(await verify.Users.AnyAsync(user => user.Email == orphanEmail)).IsFalse();
    }

    /// <summary>
    /// That the arrangement the two tests below stand on really does raise a concurrency conflict.
    /// </summary>
    /// <remarks>
    /// Written with a bare EF delete rather than through the repository, because it is the premise
    /// rather than the behaviour: without it,
    /// <see cref="DeleteAsync_WhenAnotherRequestErasedTheRowFirst_Completes" /> would pass just as
    /// happily against an arrangement in which nothing ever vanished and nothing was ever caught —
    /// the classic green that proves only that no exception was thrown. This is also the shape a
    /// second in-flight <c>DELETE /api/me</c> leaves behind, which is where the 500 came from.
    /// </remarks>
    [Test]
    public async Task Database_WhenTheRowIsDeletedBetweenTheReadAndTheSave_RaisesAConcurrencyConflict()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        ConcurrentDeleteInterceptor winner = new(
            host.ConnectionString, "delete from users where id = @id", userId);
        await using BudgetoidDbContext db = CreateDb(host, winner);
        List<User> read = await db.Users.Where(user => user.Id == userId).ToListAsync();
        db.Users.RemoveRange(read);

        // Act
        Exception? escaped = await CaptureAsync(() => db.SaveChangesAsync());

        // Assert — the read found the row, the winner then took it, and EF refused the delete of a
        // row it could no longer find.
        await Assert.That(read.Count).IsEqualTo(1);
        await Assert.That(winner.Deleted).IsEqualTo(1);
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();
    }

    /// <summary>
    /// The losing side of two concurrent erasures of one account, which is a completed erasure and
    /// not a failure.
    /// </summary>
    /// <remarks>
    /// The empty-list path in <c>DeleteAsync</c> only covers a row that was already gone when the
    /// call read. This is the other half: the row was there to read and gone by the save. Before the
    /// catch, a double-click or a client retrying a slow response answered the second request with a
    /// 500 describing an erasure that had in fact succeeded — the same lie as the 404 the method
    /// deliberately does not return, arriving by a different route.
    /// </remarks>
    [Test]
    public async Task DeleteAsync_WhenAnotherRequestErasedTheRowFirst_Completes()
    {
        // Arrange — the same vanishing row as the test above, through the repository this time.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        ConcurrentDeleteInterceptor winner = new(
            host.ConnectionString, "delete from users where id = @id", userId);
        await using BudgetoidDbContext db = CreateDb(host, winner);
        var repository = new UserRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeleteAsync(userId));

        // Assert — nothing escaped, the winner really did take the row, and the post-condition the
        // method states holds. The trailing save is what pins the detach in the catch: leave the
        // entries Deleted and a later save on this request-scoped context re-flushes a delete that
        // has already been answered, and raises the conflict a second time.
        await Assert.That(escaped).IsNull();
        await Assert.That(winner.Deleted).IsEqualTo(1);
        await Assert.That(await CountRowsAsync(connection, "users")).IsEqualTo(0L);
        await Assert.That(await CaptureAsync(() => db.SaveChangesAsync())).IsNull();
    }

    /// <summary>
    /// The narrowing on that catch, which is the half a widened <c>when</c> clause would take away.
    /// </summary>
    /// <remarks>
    /// A conflict is not an SQLSTATE, so the entries are what the method filters on: every
    /// conflicting row must be a <c>users</c> row it marked Deleted itself. Drop the <c>when</c>
    /// clause and a stranger's lost update — anything else riding along on the same
    /// <c>SaveChangesAsync</c> — is swallowed into a 204 that says the account is gone, which is the
    /// mirror image of the mis-attribution <see cref="RepositoryConstraintAttributionTests" />
    /// exists to prevent.
    /// </remarks>
    [Test]
    public async Task DeleteAsync_WhenTheConflictNamesAnotherEntity_LetsItEscape()
    {
        // Arrange — the account's credential is removed out of band and then marked Deleted here, so
        // the one delete that finds nothing is a credentials row rather than a users row. The users
        // delete this method actually makes is sound and affects its row.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using BudgetoidDbContext db = CreateDb(host);
        Credential credential = await db.Credentials.SingleAsync(row => row.UserId == userId);
        await DeleteRowAsync(connection, "delete from credentials where id = @id", credential.Id);
        db.Credentials.Remove(credential);
        var repository = new UserRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeleteAsync(userId));

        // Assert — a 500 naming a conflict this method does not model beats a 204 claiming an
        // erasure that a rolled-back transaction did not perform.
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();
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
    /// Runs one parameterised delete on the container superuser, standing in for a row another
    /// request took.
    /// </summary>
    private static async Task DeleteRowAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" />
    /// when nothing did. Deliberately untyped: the question these tests ask is <i>which</i> exception
    /// surfaces, so catching a specific one here would decide the answer in the helper.
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
    /// Builds a context with no ambient budget, which is safe here because neither <c>User</c> nor
    /// <c>Credential</c> carries a budget query filter.
    /// </summary>
    /// <param name="host">The container this context connects to.</param>
    /// <param name="interceptors">
    /// Interceptors to attach, for the tests that need something to happen inside a
    /// <c>SaveChangesAsync</c>. Empty for every other caller, which is why it is a
    /// <see langword="params" /> tail rather than a second factory.
    /// </param>
    private static BudgetoidDbContext CreateDb(
        RepositoryTestHost host,
        params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .AddInterceptors(interceptors)
            .Options);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
