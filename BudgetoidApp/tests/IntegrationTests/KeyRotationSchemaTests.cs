using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the one rule <c>key_rotations</c> holds that nothing above it can: an account has <b>at most
/// one rotation in flight</b>, because <c>user_id</c> is the table's primary key.
/// </summary>
/// <remarks>
/// <para>
/// <b>This rule cannot be tested anywhere else, and that is why the file exists.</b> A key rotation is
/// chunked across several requests — re-wrapping every narrative column in an account under a new
/// content key is not one request's worth of work — so the new generation is staged here while the old
/// one stays readable, and a single completion step promotes it. Two concurrent runs would each rewrap
/// a subset of the same rows under a <em>different</em> new content key, and the account would end
/// holding columns sealed under two keys with nothing recording which column got which. Nothing
/// downstream could tell them apart: both are well-formed envelopes of the one legal width carrying the
/// one legal version, and the server can open neither.
/// </para>
/// <para>
/// <b>What this file no longer covers, said so nobody looks for it here.</b> The table used to carry a
/// <c>factor_id</c> and a composite foreign key to <c>wrapped_account_keys</c>, and this file's probes
/// carried both. A run now encapsulates the new account keys to every surviving factor's public half
/// rather than being performed under one factor, so the per-factor value moved to
/// <c>key_rotation_seals</c> and its rules moved with it —
/// <see cref="KeyRotationSealSchemaTests" /> owns the composite key, the duplicate and the policy. What
/// is left here is the one rule this table still holds alone.
/// </para>
/// <para>
/// <b>Keyed on the user rather than guarded in a handler, which is
/// <see href="../../../docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md">ADR 0002</see>
/// applied literally.</b> A "check whether one is already running, then insert" in the application is
/// two statements with a window between them, and the window is exactly wide enough for the second
/// browser tab that caused somebody to press the button twice. A primary key has no window. The cost of
/// the choice is that a rotation cannot be modelled as one row per attempt with a status column — an
/// abandoned run has to be deleted rather than marked, or the account it belongs to can never start
/// another. That is a deliberate trade and the reason to read it here first.
/// </para>
/// <para>
/// <b>It cannot be a unit test, and a unit test pretending to hold it would be worse than no test.</b>
/// <c>KeyRotationTests</c> covers what the factory decides and says so; a factory cannot know whether a
/// row already exists, so an in-memory double asserting "the second Begin is refused" would be
/// asserting the behaviour of the double. Only the database knows, and only a statement it actually
/// executes can show that it does.
/// </para>
/// <para>
/// The probe is raw Npgsql on the container <b>superuser</b> connection, following the reasoning
/// <see cref="WrappedAccountKeysSchemaTests" /> and <see cref="PasskeySchemaTests" /> both record. Two
/// reasons, and the second is the one that would silently spoil this file: the domain refuses malformed
/// rows one layer up, so an EF write would measure the domain rather than the schema; and this table is
/// policed by <c>user_isolation</c>, so on an application connection the probe could be refused by the
/// <em>policy</em> instead of by the key, and the SQLSTATE being asserted would be the wrong one
/// arriving for the right-looking reason.
/// </para>
/// <para>
/// <b>The column list below is written out from <c>KeyRotation</c>'s contract, not read off a
/// configuration.</b> That is the sibling idiom — <see cref="SessionTokenSchemaTests" /> spells every
/// column of its insert for the same reason — and it is what lets a probe put a value in a column no
/// domain path can produce. It also means this file has to be edited when the entity gains a column,
/// which is the intended cost: a test that built its statement from the mapping would keep passing
/// while the mapping drifted.
/// </para>
/// </remarks>
public sealed class KeyRotationSchemaTests
{
    [Test]
    public async Task Database_RefusesASecondRotationForOneAccount()
    {
        // Arrange — one account and a rotation already in flight. The probe row below differs from the
        // stored row in every column that could carry a uniqueness rule of its own, so that the only
        // thing the two rows share is the account: a different rotation id, a different staged manifest,
        // a different epoch, a later instant. With the rows alike, a refusal could be PK_key_rotations
        // or an index over rotation_id, and the test would report whichever the planner evaluated first.
        //
        // NO FACTOR IS SEEDED FOR THIS PROBE ANY MORE, and that is the reshape rather than a
        // simplification. The table used to name a factor_id and reach wrapped_account_keys through a
        // composite foreign key, so both rows needed a real factor of their own and the account needed
        // two passkeys to supply them. A run now stages the factor SET it committed to — the manifest and
        // the epoch — and the per-factor value lives in key_rotation_seals, so this table's only edge is
        // FK_key_rotations_users and a user is all a row needs to exist.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");

        await using NpgsqlCommand seed = BuildRotationInsert(admin, userId, FirstRotationId, 0x7C);
        await Assert.That(await seed.ExecuteNonQueryAsync()).IsEqualTo(1);

        // Act — a second rotation for the same account. The manifest is built inside the length band and
        // the epoch above the floor, so neither of the table's two check constraints is what answers.
        await using NpgsqlCommand probe = BuildRotationInsert(admin, userId, SecondRotationId, 0x9E);
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert — 23505 from the PRIMARY KEY, which is the executable form of "at most one rotation in
        // flight per account". The name is asserted and not the SQLSTATE alone, for the reason
        // WrappedAccountKeysSchemaTests gives: the handler that starts a rotation has to tell this
        // 23505 — "you already have one running", a 409 with a resumable rotation behind it — from
        // every other unique violation the same statement can raise, and it can only do that by name.
        //
        // A surrogate id added beside user_id would demote this to an ordinary index and make the
        // duplicate storable. That is not a hypothetical edit: it is the shape every other table in
        // this schema has, so it is the shape somebody tidying will reach for.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(PrimaryKeyName);

        // And nothing landed. The SQLSTATE says the statement was rejected; only this says the account
        // still has exactly one rotation and that it is the FIRST one. A refusal that had replaced the
        // row on its way to failing would leave the count at 1 while the run in flight was silently the
        // other one — and the chunks already written would then be re-wrapped under a key nothing
        // records, which is precisely the state this rule exists to make unreachable.
        await Assert.That(await CountRotationsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ReadRotationIdAsync(admin, userId)).IsEqualTo(FirstRotationId);
    }

    /// <summary>
    /// The name PostgreSQL reports when a second rotation is begun for one account.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than read off a configuration, for the reason
    /// <c>WrappedAccountKeysSchemaTests.PrimaryKeyName</c> gives: a test taking its expectation from the
    /// thing under test agrees with whatever that thing later decides, and what this assertion is worth
    /// depends entirely on the name being the one the start-a-rotation handler filters a <c>23505</c> on.
    /// </remarks>
    private const string PrimaryKeyName = "PK_key_rotations";

    /// <summary>
    /// The two rotation identifiers. Fixed rather than minted so a failure message names values that can
    /// be found in this file, and visibly different from each other so a row read back at the wrong
    /// ordinal is a failure rather than a coincidence.
    /// </summary>
    private static readonly Guid FirstRotationId = new("0199f3a1-0000-7000-8000-0000000000e1");

    private static readonly Guid SecondRotationId = new("0199f3a1-0000-7000-8000-0000000000e2");

    /// <summary>
    /// Fixed UTC instant for the stored row. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The instant the refused probe claims. Later than <see cref="SeedInstant" /> on purpose, so that
    /// "the second row is newer" is true and the key still refuses it — a rule that quietly admitted the
    /// later of two rows would be a different rule, and this is the arrangement that would catch it.
    /// </summary>
    private static readonly DateTime ProbeInstant = new(2026, 6, 12, 14, 15, 16, DateTimeKind.Utc);

    /// <summary>
    /// Builds one well-formed <c>key_rotations</c> INSERT, every column named.
    /// </summary>
    /// <remarks>
    /// The staged manifest is built inside the length band and the epoch above the floor, so neither
    /// check constraint is ever what answers a probe here — the only thing that can refuse a row built
    /// by this method is a key, a foreign key or a policy. The filler is a parameter so two rows can be
    /// told apart by eye in a failure message.
    /// </remarks>
    private static NpgsqlCommand BuildRotationInsert(
        NpgsqlConnection connection,
        Guid userId,
        Guid rotationId,
        byte manifestFiller)
    {
        NpgsqlCommand insert = new(
            "insert into key_rotations " +
            "(user_id, rotation_id, staged_manifest, staged_rotation_epoch, started_at_utc) " +
            "values (@user_id, @rotation_id, " +
            "@staged_manifest, @staged_rotation_epoch, @started_at_utc)",
            connection);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("rotation_id", rotationId);
        insert.Parameters.AddWithValue("staged_manifest", Manifest(manifestFiller));
        insert.Parameters.AddWithValue(
            "staged_rotation_epoch",
            rotationId == FirstRotationId ? FirstEpoch : SecondEpoch);
        insert.Parameters.AddWithValue(
            "started_at_utc",
            rotationId == FirstRotationId ? SeedInstant : ProbeInstant);

        return insert;
    }

    /// <summary>
    /// A staged factor manifest of <see cref="ManifestBytes" /> bytes, every one of them
    /// <paramref name="fill" />.
    /// </summary>
    /// <remarks>
    /// Nothing on this side parses a manifest — it is authenticated client-side material — so any run of
    /// bytes inside the band is a well-formed value as far as the schema is concerned.
    /// </remarks>
    private static byte[] Manifest(byte fill) => [.. Enumerable.Repeat(fill, ManifestBytes)];

    /// <summary>
    /// How wide the seeded manifests are. Comfortably inside <c>CK_key_rotations_staged_manifest_length</c>
    /// from both sides, so that constraint is never what refuses a probe.
    /// </summary>
    private const int ManifestBytes = 32;

    /// <summary>
    /// The two generations the two rows name. Above <c>CK_key_rotations_staged_rotation_epoch</c>'s floor
    /// and different from each other, so the two rows differ in every column but the account.
    /// </summary>
    private const int FirstEpoch = 2;

    private const int SecondEpoch = 3;

    private static async Task<long> CountRotationsAsync(NpgsqlConnection connection, Guid userId)
    {
        await using NpgsqlCommand count = new(
            "select count(*) from key_rotations where user_id = @user_id", connection);
        count.Parameters.AddWithValue("user_id", userId);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the query
        // changed shape, and that should fail loudly here instead of at the assertion.
        return await count.ExecuteScalarAsync() switch
        {
            long rows => rows,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from 'key_rotations', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The rotation identifier of the one row the account holds — the half of the assertion a count
    /// cannot make, because a replaced row and a refused one are both a count of 1.
    /// </summary>
    private static async Task<Guid> ReadRotationIdAsync(NpgsqlConnection connection, Guid userId)
    {
        await using NpgsqlCommand read = new(
            "select rotation_id from key_rotations where user_id = @user_id", connection);
        read.Parameters.AddWithValue("user_id", userId);

        return await read.ExecuteScalarAsync() switch
        {
            Guid rotationId => rotationId,
            var unexpected => throw new InvalidOperationException(
                $"Expected one rotation id, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Runs a statement that must be refused and returns the refusal, throwing if it went through — the
    /// shape <see cref="PasskeySchemaTests" /> uses, because a probe that succeeds is a defect in the
    /// schema rather than an assertion to report.
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
