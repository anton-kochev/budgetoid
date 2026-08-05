using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the rules the <c>sessions</c> table holds on its own: that a session and the credential
/// that opened it can never disagree about whose they are, that neither outlives the account, that a
/// session with no live interval and a kind outside the vocabulary are both unstorable, and that a
/// session written the way production writes it survives the round trip through the least-privilege
/// role. Every negative is raw Npgsql on the container superuser connection, because
/// <see cref="Session.Establish" /> already refuses most of these one layer up — an EF-based write
/// would measure the domain rather than the schema, which is exactly the layer this file exists to
/// be independent of.
/// </summary>
/// <remarks>
/// The passkey credential is seeded with raw SQL because no domain factory mints one yet.
/// <c>type = 'passkey'</c> with <c>provider</c> and <c>subject</c> both NULL is the shape
/// <c>CK_credentials_type_shape</c> already permits, and <c>AppRoleGrantsTests</c> inserts the same
/// row the same way.
/// </remarks>
public sealed class SessionSchemaTests
{
    [Test]
    public async Task Database_RefusesASessionWhoseCredentialBelongsToAnotherUser()
    {
        // Arrange — two accounts, each holding its own credential, so the only thing wrong with the
        // row below is that the two ids name different people.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userA = await InsertUserRowAsync(connection, "a@example.com");
        await InsertCredentialAsync(connection, userA, "google-a");
        Guid userB = await InsertUserRowAsync(connection, "b@example.com");
        Guid credentialB = await InsertCredentialAsync(connection, userB, "google-b");

        // Act — 'locked' against a federated credential, which is the pairing
        // CK_sessions_kind_matches_credential permits, so the only thing left to refuse the row is
        // the disagreement about whose credential it is.
        PostgresException exception = await ThrowsSessionInsertAsync(
            connection, userA, credentialB, "federated", "locked", SeedInstant, ExpiryInstant);

        // Assert — the composite foreign key is what refuses this, and it is the only thing that
        // could: user_isolation decides tenancy on user_id alone and never looks at the credential,
        // so a stored disagreement would be a row the policy happily shows to the wrong person.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(await CountRowsAsync(connection, "sessions", "user_id", userA))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RemovesASessionWithTheCredentialThatEstablishedIt()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");
        Guid credentialId = await InsertCredentialAsync(connection, userId, "google-1");
        await InsertSessionAsync(
            connection, userId, credentialId, "federated", "locked", SeedInstant, ExpiryInstant);

        // Act — raw Npgsql on purpose: under a cascade EF would delete the dependents itself, so an
        // EF-based delete proves nothing about what the schema does.
        await ExecuteAsync(connection, "delete from credentials where id = @id", credentialId);

        // Assert — CASCADE rather than RESTRICT, and that is the decision: RESTRICT would let a row
        // of access bookkeeping hold up the removal of a credential, and through it an account
        // erasure. The user survives, so this is the credential's cascade rather than the one below.
        await Assert.That(await CountRowsAsync(connection, "sessions", "user_id", userId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "users", "id", userId)).IsEqualTo(1L);
    }

    [Test]
    public async Task Database_RemovesASessionWithTheUserThatOwnsIt()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");
        Guid credentialId = await InsertCredentialAsync(connection, userId, "google-1");
        await InsertSessionAsync(
            connection, userId, credentialId, "federated", "locked", SeedInstant, ExpiryInstant);

        // Act
        await ExecuteAsync(connection, "delete from users where id = @id", userId);

        // Assert — there is deliberately no second foreign key from sessions to users; the transitive
        // cascade is the whole mechanism. users -> credentials cascades and credentials -> sessions
        // cascades, so the session goes two hops rather than one, and a direct foreign key would add
        // nothing but another constraint name for the pinned snapshots to carry.
        await Assert.That(await CountRowsAsync(connection, "sessions", "user_id", userId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "credentials", "user_id", userId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesASessionWhoseExpiryIsNotAfterItsCreation()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");
        Guid credentialId = await InsertCredentialAsync(connection, userId, "google-1");

        // Act — an expiry equal to the creation instant, which is the boundary Session.Establish
        // already refuses. Raw SQL is what reaches the check at all. The kind and the credential
        // type agree, so the lifetime is the row's only defect.
        PostgresException exception = await ThrowsSessionInsertAsync(
            connection, userId, credentialId, "federated", "locked", SeedInstant, SeedInstant);

        // Assert — the name is asserted because this row breaches exactly one constraint, so the
        // attribution is deterministic rather than an artifact of evaluation order, and the repo pins
        // attribution by constraint name.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_sessions_lifetime");
    }

    [Test]
    public async Task Database_RefusesASessionKindOutsideTheVocabulary()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");
        Guid credentialId = await InsertCredentialAsync(connection, userId, "google-1");

        // Act — 'admin' against a federated credential, and the federated half is what keeps the
        // attribution deterministic. CK_sessions_kind_matches_credential reads
        // (kind = 'full') = (credential_type = 'passkey'); with an out-of-vocabulary kind the left
        // side is false, so a passkey credential would make the two sides disagree and breach that
        // check as well, leaving which of the two names PostgreSQL reports an accident of evaluation
        // order. A federated credential makes both sides false, satisfying the matching check, so
        // the vocabulary is the row's only defect.
        PostgresException exception = await ThrowsSessionInsertAsync(
            connection, userId, credentialId, "federated", "admin", SeedInstant, ExpiryInstant);

        // Assert — the vocabulary is a dictionary the database owns, so a kind the domain never mints
        // cannot arrive through raw SQL either. kind is a varchar rather than a native enum, which
        // makes this CHECK the only thing standing between the vocabulary and any string a writer
        // felt like storing — and the column decides whether a session reaches budget content. The
        // name is asserted rather than the SQLSTATE alone: one defect must report one name, which is
        // what the arrangement above buys.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_sessions_kind");
    }

    [Test]
    public async Task Database_RefusesAFullSessionOpenedByAFederatedCredential()
    {
        // Arrange — one account holding both shapes of credential, so the two halves below differ
        // only in which credential opened the session and which kind it claims.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        Guid userId = await InsertUserRowAsync(connection, "person@example.com");
        Guid federatedId = await InsertCredentialAsync(connection, userId, "google-1");
        Guid passkeyId = await InsertPasskeyCredentialAsync(connection, userId);

        // Act — 'full' against the federated credential. Every other constraint on the row is
        // satisfied: 'full' is in the vocabulary, the credential is this account's own so the
        // composite foreign key holds, and the expiry is after the creation.
        PostgresException federatedRefusal = await ThrowsSessionInsertAsync(
            connection, userId, federatedId, "federated", "full", SeedInstant, ExpiryInstant);

        // The mirror, because the constraint is an equality rather than an implication and refuses
        // both directions. It costs one more credential row and it is what stops a future
        // "credential_type = 'passkey' or kind <> 'full'" from passing as a weakening of the rule.
        PostgresException passkeyRefusal = await ThrowsSessionInsertAsync(
            connection, userId, passkeyId, "passkey", "locked", SeedInstant, ExpiryInstant);

        // Assert — until this check existed the rule lived only in the Session.Establish factory,
        // while GRANT INSERT on sessions was unconditional over the whole column list: a row pairing
        // a federated credential with kind = 'full' was fully storable by the application role, and
        // a session that reaches budget content is exactly what the pairing must never produce.
        // Nothing landed either way, because a refusal that had already written the row would leave
        // the rule true only about the SQLSTATE.
        await Assert.That(federatedRefusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(federatedRefusal.ConstraintName)
            .IsEqualTo("CK_sessions_kind_matches_credential");
        await Assert.That(passkeyRefusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(passkeyRefusal.ConstraintName)
            .IsEqualTo("CK_sessions_kind_matches_credential");
        await Assert.That(await CountRowsAsync(connection, "sessions", "user_id", userId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Session_RoundTripsThroughTheApplicationRole()
    {
        // Arrange — the seeded account and the federated credential that resolves to it, then an
        // app-role session naming that user. sessions is policed on app.current_user_id, so without
        // the setting the write would not be refused on privilege at all — it would fail its WITH
        // CHECK, which is a different measurement.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId;
        Guid sessionId;
        await using (NpgsqlConnection write = await host.OpenAppConnectionForUserAsync(userId))
        {
            await using BudgetoidDbContext db = CreateAppDb(write);
            Credential credential = await db.Credentials.SingleAsync(
                stored => stored.UserId == userId);
            credentialId = credential.Id;
            Session session = Session.Establish(credential, SeedInstant, ExpiryInstant);
            sessionId = session.Id;

            // Act
            await new SessionRepository(db).AddAsync(session);
        }

        // Assert — read back on a second app-role connection, the way a later request would. Two
        // rules ride on this one round trip: a missing INSERT grant would have surfaced as 42501
        // above, and a converter storing the PascalCase enum member would have tripped
        // CK_sessions_kind, because 'Locked' is not in the vocabulary the column accepts.
        await using NpgsqlConnection read = await host.OpenAppConnectionForUserAsync(userId);
        await using BudgetoidDbContext verify = CreateAppDb(read);
        Session stored = await verify.Sessions.SingleAsync(session => session.Id == sessionId);
        await Assert.That(stored.Kind).IsEqualTo(SessionKind.Locked);
        await Assert.That(stored.CredentialId).IsEqualTo(credentialId);
        await Assert.That(stored.UserId).IsEqualTo(userId);
        await Assert.That(stored.RevokedAtUtc).IsNull();
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The expiry every valid session here is given. It must be strictly after
    /// <see cref="SeedInstant" />, which is the whole content of <c>CK_sessions_lifetime</c>.
    /// </summary>
    private static readonly DateTime ExpiryInstant = new(2026, 6, 13, 13, 14, 15, DateTimeKind.Utc);

    private static async Task<Guid> InsertUserRowAsync(NpgsqlConnection connection, string email)
    {
        Guid userId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            """
            insert into users (id, email, created_at_utc)
            values (@id, @email, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await command.ExecuteNonQueryAsync();
        return userId;
    }

    /// <summary>
    /// Writes the federated Google credential that resolves to <paramref name="userId" /> and returns
    /// its id, which every session row here has to name.
    /// </summary>
    private static async Task<Guid> InsertCredentialAsync(
        NpgsqlConnection connection,
        Guid userId,
        string subject)
    {
        Guid credentialId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            """
            insert into credentials (id, user_id, type, provider, subject, created_at_utc)
            values (@id, @user_id, 'federated', 'google', @subject, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return await command.ExecuteNonQueryAsync() switch
        {
            1 => credentialId,
            var rows => throw new InvalidOperationException($"Inserted {rows} credentials, wanted 1."),
        };
    }

    /// <summary>
    /// Writes a passkey credential onto an existing account and returns its id. Raw SQL because no
    /// domain factory mints one; <c>(passkey, null, null)</c> is the shape
    /// <c>CK_credentials_type_shape</c> permits, and the partial unique index on
    /// <c>(provider, subject)</c> names only federated rows, so it does not collide with the
    /// account's Google credential.
    /// </summary>
    private static async Task<Guid> InsertPasskeyCredentialAsync(
        NpgsqlConnection connection,
        Guid userId)
    {
        Guid credentialId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            """
            insert into credentials (id, user_id, type, provider, subject, created_at_utc)
            values (@id, @user_id, 'passkey', null, null, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return await command.ExecuteNonQueryAsync() switch
        {
            1 => credentialId,
            var rows => throw new InvalidOperationException($"Inserted {rows} credentials, wanted 1."),
        };
    }

    private static async Task InsertSessionAsync(
        NpgsqlConnection connection,
        Guid userId,
        Guid credentialId,
        string credentialType,
        string kind,
        DateTime createdAtUtc,
        DateTime expiresAtUtc)
    {
        await using NpgsqlCommand command = BuildSessionInsert(
            connection, userId, credentialId, credentialType, kind, createdAtUtc, expiresAtUtc);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> ThrowsSessionInsertAsync(
        NpgsqlConnection connection,
        Guid userId,
        Guid credentialId,
        string credentialType,
        string kind,
        DateTime createdAtUtc,
        DateTime expiresAtUtc)
    {
        await using NpgsqlCommand command = BuildSessionInsert(
            connection, userId, credentialId, credentialType, kind, createdAtUtc, expiresAtUtc);

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
    /// Builds the session insert with every column spelled out, so a test can put a value in one of
    /// them that no domain path can produce.
    /// </summary>
    /// <remarks>
    /// <paramref name="credentialType" /> is a parameter rather than a literal because it is now
    /// half of two constraints at once: the composite foreign key compares it against
    /// <c>credentials.type</c>, and <c>CK_sessions_kind_matches_credential</c> compares it against
    /// <paramref name="kind" />. A caller therefore has to state both, and a row that gets only one
    /// of them right is refused for a reason the caller chose rather than one it stumbled into.
    /// </remarks>
    private static NpgsqlCommand BuildSessionInsert(
        NpgsqlConnection connection,
        Guid userId,
        Guid credentialId,
        string credentialType,
        string kind,
        DateTime createdAtUtc,
        DateTime expiresAtUtc)
    {
        NpgsqlCommand command = new(
            """
            insert into sessions
                (id, user_id, credential_id, credential_type, kind,
                 created_at_utc, expires_at_utc, revoked_at_utc)
            values (@id, @user_id, @credential_id, @credential_type, @kind,
                    @created_at_utc, @expires_at_utc, null)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("credential_id", credentialId);
        command.Parameters.AddWithValue("credential_type", credentialType);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("created_at_utc", createdAtUtc);
        command.Parameters.AddWithValue("expires_at_utc", expiresAtUtc);
        return command;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, Guid rowId)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", rowId);
        await command.ExecuteNonQueryAsync();
    }

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
    /// Builds a context over an <b>already open, already configured</b> app-role connection, so that
    /// EF sends its statements on the session carrying <c>app.current_user_id</c> rather than opening
    /// one of its own. No ambient budget: <c>Session</c> and <c>Credential</c> are user-owned and
    /// carry no budget query filter.
    /// </summary>
    private static BudgetoidDbContext CreateAppDb(NpgsqlConnection connection) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(connection, contextOwnsConnection: false)
            .Options);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
