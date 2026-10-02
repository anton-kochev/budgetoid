using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the rules <c>erasure_schedules</c> holds that nothing above it can: an account has <b>at
/// most one scheduled erasure</b>, because <c>user_id</c> is the table's primary key; the row
/// <b>leaves with its account</b>, because the foreign key to <c>users</c> cascades; and the table
/// holds <b>exactly two columns</b>, neither of them nullable.
/// </summary>
/// <remarks>
/// <para>
/// <b>One row per account is what makes a repeat request answer the same instant.</b> Requesting an
/// erasure from a locked session writes the row once, and every repeat reads it back. Two racing
/// requests are two inserts, and the primary key is what turns the loser into a re-read of the
/// winner's date rather than a second date beside it. A "check, then insert" in a handler is two
/// statements with a window between them; a key has no window — ADR 0002 applied literally.
/// </para>
/// <para>
/// <b>The cascade is what keeps the schedule from becoming a deletion record.</b> Once the account
/// is gone, a row still naming it says that this user existed and asked to be erased, which
/// <c>docs/business-logic/erasure.md</c> forbids. The role holds no <c>DELETE</c> on this table, so
/// the cascade is the only way out for a row — and the probe below deletes the user on the
/// <b>application</b> connection to prove the referential action reaches it without one.
/// </para>
/// <para>
/// The duplicate probe runs on the container <b>superuser</b> connection, for the reason
/// <see cref="KeyRotationSchemaTests" /> records: this table is policed by <c>user_isolation</c>, so on
/// an application connection the probe could be refused by the policy instead of by the key, and the
/// SQLSTATE being asserted would be the wrong one arriving for the right-looking reason.
/// </para>
/// </remarks>
public sealed class ErasureScheduleSchemaTests
{
    [Test]
    public async Task Database_RefusesASecondScheduleForOneAccount()
    {
        // Arrange — one account with a schedule already standing. The probe names a LATER instant, so
        // the two rows share nothing but the account and "the newer date wins" would be a different
        // rule that this arrangement catches.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");

        await using NpgsqlCommand seed = BuildScheduleInsert(admin, userId, FirstInstant);
        await Assert.That(await seed.ExecuteNonQueryAsync()).IsEqualTo(1);

        // Act — a second schedule for the same account.
        await using NpgsqlCommand probe = BuildScheduleInsert(admin, userId, LaterInstant);
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert — 23505 from the PRIMARY KEY. The name is asserted and not the SQLSTATE alone: the
        // repository that writes a schedule tells this collision — "a racing request already filed
        // one, read it back" — from every other unique violation by name, and only by name.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(PrimaryKeyName);

        // And nothing landed. A refusal that had replaced the row on its way to failing would leave the
        // count at 1 while the stored date was silently the later one — which is a repeat request
        // pushing the date out, the one thing a repeat must never do.
        await Assert.That(await CountSchedulesAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ReadTakesEffectAtAsync(admin, userId)).IsEqualTo(FirstInstant);
    }

    [Test]
    public async Task Database_RemovesAScheduleWithTheUser()
    {
        // Arrange — two accounts, a schedule on each. The survivor is what stops a cascade wider than
        // its owner — or a sweep of the whole table — from passing: with one account, "this account's
        // row is gone" and "every row is gone" are the same observation.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid survivorId = await host.SeedUserAsync("google-2", "other@example.com");

        await using (NpgsqlCommand seed = BuildScheduleInsert(admin, userId, FirstInstant))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand seedSurvivor = BuildScheduleInsert(admin, survivorId, LaterInstant))
        {
            await seedSurvivor.ExecuteNonQueryAsync();
        }

        // The zero below means nothing unless the row was there first.
        await Assert.That(await CountSchedulesAsync(admin, userId)).IsEqualTo(1L);

        // users is policed, so the session names the owner — an unconfigured connection would meet the
        // policy's 22P02 before the grant. The role holds DELETE on users and none on
        // erasure_schedules, which is the point: the row has to leave by the referential action, run
        // with the referencing table owner's privileges, or it does not leave at all.
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Act
        await using NpgsqlCommand delete = new("delete from users where id = @id", app);
        delete.Parameters.AddWithValue("id", userId);
        int deleted = await delete.ExecuteNonQueryAsync();

        // Assert — the affected count first, because a delete matching nothing raises nothing and would
        // make the zero below mean "there was never a user" rather than "the cascade took the row".
        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await CountSchedulesAsync(admin, userId)).IsEqualTo(0L);
        await Assert.That(await CountSchedulesAsync(admin, survivorId)).IsEqualTo(1L);
        await Assert.That(await ReadTakesEffectAtAsync(admin, survivorId)).IsEqualTo(LaterInstant);
    }

    [Test]
    public async Task Schema_PinsTheColumnsOfTheErasureSchedule()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        await using NpgsqlCommand read = new(
            """
            select column_name || ' ' || data_type || ' ' || is_nullable
            from information_schema.columns
            where table_schema = 'public' and table_name = 'erasure_schedules'
            order by column_name
            """,
            admin);
        List<string> columns = [];
        await using (NpgsqlDataReader reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        // Assert — two columns, and the absences are the half worth reading. No requested_at: the
        // instant that matters is when the account goes, and a second timestamp is a second thing an
        // export, a log or an erasure would have to answer for. No cancelled_at or status: a cancel
        // removes the row, because a stamped one is a remnant. timestamptz and NOT NULL on the date,
        // because a schedule with no instant — or one read in the server's local zone — is a date
        // nobody can be told.
        string[] expected =
        [
            "takes_effect_at_utc timestamp with time zone NO",
            "user_id uuid NO",
        ];
        await Assert.That(columns).IsEquivalentTo(expected);
    }

    /// <summary>
    /// The name PostgreSQL reports when a second schedule is filed for one account. Spelled out rather
    /// than read off a configuration, for the reason <c>KeyRotationSchemaTests.PrimaryKeyName</c> gives.
    /// </summary>
    private const string PrimaryKeyName = "PK_erasure_schedules";

    /// <summary>
    /// The instant the stored schedule takes effect at. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime FirstInstant = new(2026, 6, 19, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The instant the refused probe claims, later than <see cref="FirstInstant" /> on purpose.
    /// </summary>
    private static readonly DateTime LaterInstant = new(2026, 6, 26, 9, 0, 0, DateTimeKind.Utc);

    private static NpgsqlCommand BuildScheduleInsert(
        NpgsqlConnection connection,
        Guid userId,
        DateTime takesEffectAtUtc)
    {
        NpgsqlCommand insert = new(
            "insert into erasure_schedules (user_id, takes_effect_at_utc) " +
            "values (@user_id, @takes_effect_at_utc)",
            connection);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("takes_effect_at_utc", takesEffectAtUtc);
        return insert;
    }

    private static async Task<long> CountSchedulesAsync(NpgsqlConnection connection, Guid userId)
    {
        await using NpgsqlCommand count = new(
            "select count(*) from erasure_schedules where user_id = @user_id", connection);
        count.Parameters.AddWithValue("user_id", userId);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the query
        // changed shape, and that should fail loudly here instead of at the assertion.
        return await count.ExecuteScalarAsync() switch
        {
            long rows => rows,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from 'erasure_schedules', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The instant the account's one schedule names — the half of the assertion a count cannot make,
    /// because a replaced row and a refused one are both a count of 1.
    /// </summary>
    private static async Task<DateTime> ReadTakesEffectAtAsync(NpgsqlConnection connection, Guid userId)
    {
        await using NpgsqlCommand read = new(
            "select takes_effect_at_utc from erasure_schedules where user_id = @user_id", connection);
        read.Parameters.AddWithValue("user_id", userId);

        return await read.ExecuteScalarAsync() switch
        {
            DateTime instant => instant,
            var unexpected => throw new InvalidOperationException(
                $"Expected one scheduled instant, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Runs a statement that must be refused and returns the refusal, throwing if it went through.
    /// </summary>
    private static async Task<PostgresException> RefusalOfAsync(NpgsqlCommand command)
    {
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

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();

        return host;
    }
}
