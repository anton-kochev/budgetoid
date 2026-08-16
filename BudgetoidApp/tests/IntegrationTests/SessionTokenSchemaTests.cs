using Domain.Sessions;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the rules the <c>session_tokens</c> table holds on its own: that a stored handle can never
/// name a session belonging to somebody else, that a value which is not a 32-byte digest is
/// unstorable from both sides, that a token leaves with the session it opens and with the account
/// that owns it, and that two sessions cannot share one handle. Every statement is raw Npgsql on the
/// container superuser connection, because <see cref="SessionToken.For" /> already refuses some of
/// these one layer up — an EF-based write would measure the domain rather than the schema, which is
/// exactly the layer this file exists to be independent of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this table's rules are worth more than the shape of the file suggests.</b>
/// <c>session_tokens</c> is exempt from row-level security: a presented handle has to be looked up
/// <i>before</i> the request has an identity, and <c>user_isolation</c> is keyed on
/// <c>app.current_user_id</c>, which is exactly the value that lookup exists to produce. So no policy
/// is underneath any of these rows, and the <c>user_id</c> a row carries is the one the request then
/// adopts. On every other user-owned table a wrong owner is a misfiled row a policy would hide; here
/// it is a handover of somebody else's account. The composite foreign key is what makes that
/// unstorable rather than merely unlikely, which is why the first test below is the one to read first.
/// </para>
/// <para>
/// The credentials and sessions these tests hang off are seeded with raw SQL for the reason
/// <c>SessionSchemaTests</c> records: a test about what the schema refuses has to be able to reach the
/// table by a route the domain does not police, and a factory-minted arrangement would make each
/// negative a statement about the factory's arithmetic as much as about the constraint.
/// </para>
/// </remarks>
public sealed class SessionTokenSchemaTests
{
    [Test]
    public async Task Database_RefusesASessionTokenWhoseSessionBelongsToAnotherUser()
    {
        // Arrange — two accounts, each with its own credential and its own live session, so the only
        // thing wrong with the row below is that the two ids name different people.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        (Guid userA, _, _) = await InsertAccountWithSessionAsync(connection, "a@example.com", "google-a");
        (Guid userB, _, Guid sessionB) =
            await InsertAccountWithSessionAsync(connection, "b@example.com", "google-b");

        // Act — B's session under A's name. The digest is well-formed at exactly HashLength bytes, so
        // CK_session_tokens_token_hash_length refuses nothing and the disagreement about whose session
        // it is is the row's only defect.
        PostgresException refusal = await ThrowsTokenInsertAsync(
            connection, Digest(0xA1), sessionB, userA);

        // Assert — this is the whole reason the foreign key is composite, and it is the sharpest
        // version of that argument anywhere on the schema. A single-column reference to sessions(id)
        // would accept this row, and nothing else would object: the table is exempt from row-level
        // security, so no policy compares the two columns, and the lookup that reads this row runs
        // anonymously and adopts the user_id it finds. Presenting the handle would sign the caller into
        // account A while naming account B's session — with a perfectly valid session row behind it.
        //
        // Nothing landed, because a refusal that had already written the row would leave the rule true
        // only about the SQLSTATE.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo("FK_session_tokens_sessions");
        await Assert.That(await CountRowsAsync(connection, "session_tokens", "user_id", userA))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "session_tokens", "user_id", userB))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesATokenHashThatIsNotThirtyTwoBytes()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        (Guid userId, _, Guid sessionId) =
            await InsertAccountWithSessionAsync(connection, "person@example.com", "google-1");

        // Act — both directions, because the constraint is an equality and a range would accept one of
        // them. The short one is the direction a reader assumes is covered by "at least 32"; the long
        // one is the direction a reader assumes nobody would write. Neither is a digest SHA-256 can
        // have produced, and the two lengths differ from each other so the primary key is never what
        // refuses the second statement.
        PostgresException shortRefusal = await ThrowsTokenInsertAsync(
            connection, Digest(0xB2, SessionToken.HashLength - 1), sessionId, userId);
        PostgresException longRefusal = await ThrowsTokenInsertAsync(
            connection, Digest(0xC3, SessionToken.HashLength + 1), sessionId, userId);

        // Assert — equality rather than a range is the honest rule here for the reason
        // CK_recovery_code_hashes_verifier_hash_length states: the value is computed server-side, so it
        // is 32 bytes or it is not a digest this table can have produced, and a row of any other width
        // is a bug in the code that wrote it rather than input somebody supplied.
        //
        // What this constraint deliberately cannot see is the other half of the rule and is worth
        // stating beside it: it watches the DIGEST, which is 32 bytes whatever went into it, so a
        // SHORT TOKEN hashes to a perfectly well-formed row nothing in the database could tell from a
        // real one. SessionToken.For is the only place a token of the wrong width stops. These two
        // statements are therefore not a test of handle strength; they are a test that the column holds
        // digests.
        //
        // The name is asserted rather than the SQLSTATE alone: each row breaches exactly one
        // constraint — the session is this account's own so the composite foreign key holds — so the
        // attribution is deterministic rather than an artifact of evaluation order.
        await Assert.That(shortRefusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(shortRefusal.ConstraintName)
            .IsEqualTo("CK_session_tokens_token_hash_length");
        await Assert.That(longRefusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(longRefusal.ConstraintName)
            .IsEqualTo("CK_session_tokens_token_hash_length");
        await Assert.That(await CountRowsAsync(connection, "session_tokens", "user_id", userId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RemovesASessionTokenWithTheSessionThatOpenedIt()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        (Guid userId, Guid credentialId, Guid sessionId) =
            await InsertAccountWithSessionAsync(connection, "person@example.com", "google-1");
        await InsertTokenAsync(connection, Digest(0xD4), sessionId, userId);

        // Act — raw Npgsql on purpose: under a cascade EF would delete the dependent itself, so an
        // EF-based delete proves nothing about what the schema does.
        await ExecuteAsync(connection, "delete from sessions where id = @id", sessionId);

        // Assert — CASCADE rather than RESTRICT, and that is the decision: a stored handle must never
        // be able to hold up the removal of the session it names, and a token whose session is gone
        // names nothing anyway — the row it would leave behind is a handle resolving to a dangling id.
        //
        // Note what this cascade is NOT, because it is the trap one level up: removing a token row is
        // not revocation. A path that deleted the token instead of stamping sessions.revoked_at_utc
        // would sign the browser out while leaving nothing that says when access ended — which is why
        // the application role holds no DELETE on this table at all.
        //
        // The credential survives, so this is the session's own cascade rather than the one below.
        await Assert.That(await CountRowsAsync(connection, "session_tokens", "user_id", userId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "credentials", "id", credentialId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_RemovesASessionTokenWithTheUserThatOwnsIt()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        (Guid userId, _, Guid sessionId) =
            await InsertAccountWithSessionAsync(connection, "person@example.com", "google-1");
        await InsertTokenAsync(connection, Digest(0xE5), sessionId, userId);

        // Act
        await ExecuteAsync(connection, "delete from users where id = @id", userId);

        // Assert — three hops, and the chain is the whole mechanism: users -> credentials cascades,
        // credentials -> sessions cascades, sessions -> session_tokens cascades. There is deliberately
        // no direct foreign key from this table to users, so a broken link anywhere along that chain
        // strands the handle rather than removing it.
        //
        // This is the claim erasure rests on. The application role holds DELETE on users and on no
        // other owned table, so the only thing that takes a token row away when somebody asks to be
        // forgotten is this transitive cascade — and ErasureAtomicityTests counts it from the other
        // side, over the whole database, without knowing which foreign keys carried it.
        await Assert.That(await CountRowsAsync(connection, "session_tokens", "user_id", userId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "sessions", "user_id", userId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "credentials", "user_id", userId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesASecondSessionTokenWithTheSameHash()
    {
        // Arrange — one account, one credential, TWO live sessions. Two sessions is the arrangement:
        // the same digest against the same session would be one row written twice, which says nothing
        // about the rule, while two sessions sharing a handle is the state the rule exists to refuse.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        (Guid userId, Guid credentialId, Guid firstSessionId) =
            await InsertAccountWithSessionAsync(connection, "person@example.com", "google-1");
        Guid secondSessionId = await InsertSessionAsync(connection, userId, credentialId);

        byte[] shared = Digest(0xF6);
        await InsertTokenAsync(connection, shared, firstSessionId, userId);

        // Act — the same digest against the second session. Every other rule on the row is satisfied:
        // the width is exact, and the session is this account's own so the composite foreign key holds.
        PostgresException refusal = await ThrowsTokenInsertAsync(
            connection, shared, secondSessionId, userId);

        // Assert — the hash being the PRIMARY KEY is what makes this unstorable rather than a duplicate
        // nothing would notice, and the difference between those two outcomes is not academic. The
        // lookup is a SingleOrDefault by that key; with an ordinary column, two rows under one digest
        // would be two sessions reachable by one cookie, resolved by whichever row the read happened to
        // return — and, once the same collision spans two accounts, by whichever account it happened to
        // return. A surrogate id added beside this column would demote the key to an ordinary index and
        // permit exactly that.
        //
        // The name is asserted rather than the SQLSTATE alone, because the repository that will one day
        // translate this violation has to tell it from every other 23505 the same statement can raise.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo("PK_session_tokens");

        // Exactly one row survives, and it is the first one: a refusal that had replaced the row on its
        // way to failing would leave the count at 1 while the handle now named the other session.
        await Assert.That(await CountRowsAsync(connection, "session_tokens", "user_id", userId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(
                connection, "session_tokens", "session_id", firstSessionId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(
                connection, "session_tokens", "session_id", secondSessionId))
            .IsEqualTo(0L);
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The expiry every session here is given. Strictly after <see cref="SeedInstant" />, which is the
    /// whole content of <c>CK_sessions_lifetime</c>.
    /// </summary>
    private static readonly DateTime ExpiryInstant = new(2026, 6, 13, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// A stand-in digest of <paramref name="length" /> bytes, every one of them
    /// <paramref name="fill" />.
    /// </summary>
    /// <remarks>
    /// Nothing here hashes anything: these tests are about what the column accepts, and the column
    /// cannot tell a SHA-256 from 32 bytes of anything else — which is itself part of what the first
    /// test's remarks say. The fill byte is required rather than defaulted because <c>token_hash</c> is
    /// the primary key, so two calls meaning two different handles have to differ. The default length
    /// reads <see cref="SessionToken.HashLength" /> rather than a local 32, so a test aiming at the
    /// bound moves with the domain instead of drifting away from it.
    /// </remarks>
    private static byte[] Digest(byte fill, int length = SessionToken.HashLength) =>
        [.. Enumerable.Repeat(fill, length)];

    /// <summary>
    /// Writes an account, its federated credential and one live session, and returns all three ids.
    /// </summary>
    /// <remarks>
    /// The three travel together because a token row names two of them and cannot exist without the
    /// third: <c>sessions</c> references <c>credentials</c> over a composite key, so a session with no
    /// credential is unstorable and a test holding a session id without its owner cannot build the row
    /// it wants to be refused for one reason only.
    /// </remarks>
    private static async Task<(Guid UserId, Guid CredentialId, Guid SessionId)>
        InsertAccountWithSessionAsync(NpgsqlConnection connection, string email, string subject)
    {
        Guid userId = await InsertUserRowAsync(connection, email);
        Guid credentialId = await InsertCredentialAsync(connection, userId, subject);
        Guid sessionId = await InsertSessionAsync(connection, userId, credentialId);

        return (userId, credentialId, sessionId);
    }

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
        await command.ExecuteNonQueryAsync();

        return credentialId;
    }

    /// <summary>
    /// Writes one live session established by <paramref name="credentialId" /> and returns its id.
    /// </summary>
    /// <remarks>
    /// <c>('federated', 'locked')</c> because every credential seeded here is federated and
    /// <c>CK_sessions_kind_matches_credential</c> refuses a full session opened by one. Nothing in this
    /// file is about the kind, so the cheapest row the schema accepts is the right one: a row a CHECK
    /// refused would never reach the rule a test is reading.
    /// </remarks>
    private static async Task<Guid> InsertSessionAsync(
        NpgsqlConnection connection,
        Guid userId,
        Guid credentialId)
    {
        Guid sessionId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            """
            insert into sessions
                (id, user_id, credential_id, credential_type, kind,
                 created_at_utc, expires_at_utc, revoked_at_utc)
            values (@id, @user_id, @credential_id, 'federated', 'locked',
                    @created_at_utc, @expires_at_utc, null)
            """,
            connection);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("credential_id", credentialId);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        command.Parameters.AddWithValue("expires_at_utc", ExpiryInstant);
        await command.ExecuteNonQueryAsync();

        return sessionId;
    }

    private static async Task InsertTokenAsync(
        NpgsqlConnection connection,
        byte[] tokenHash,
        Guid sessionId,
        Guid userId)
    {
        await using NpgsqlCommand command = BuildTokenInsert(connection, tokenHash, sessionId, userId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> ThrowsTokenInsertAsync(
        NpgsqlConnection connection,
        byte[] tokenHash,
        Guid sessionId,
        Guid userId)
    {
        await using NpgsqlCommand command = BuildTokenInsert(connection, tokenHash, sessionId, userId);

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
    /// Builds the token insert with every column spelled out, so a test can put a value in one of them
    /// that no domain path can produce.
    /// </summary>
    /// <remarks>
    /// <paramref name="sessionId" /> and <paramref name="userId" /> are separate parameters rather than
    /// a loaded session, and that is the point of writing the statement out: <see cref="SessionToken.For" />
    /// reads both off one session and therefore cannot express the disagreement the first test needs.
    /// </remarks>
    private static NpgsqlCommand BuildTokenInsert(
        NpgsqlConnection connection,
        byte[] tokenHash,
        Guid sessionId,
        Guid userId)
    {
        NpgsqlCommand command = new(
            """
            insert into session_tokens (token_hash, session_id, user_id)
            values (@token_hash, @session_id, @user_id)
            """,
            connection);
        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);

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

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();

        return host;
    }
}
