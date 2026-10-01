using Domain.Users;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the rules <c>key_rotation_seals</c> holds that nothing above it can: one copy per factor per
/// account, a payload of exactly one width carrying exactly one framing version, <b>a seal staged
/// against another account's factor is unstorable</b>, and one account's seals are invisible to another.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third of those is the rule the table exists for, and it is the one to read first.</b> A run
/// draws a new content key and a new index key once and encapsulates that one pair to every public key
/// the staged manifest names — so the value is per factor, and the question "whose factor" is the
/// question. The composite foreign key to <c>wrapped_account_keys(factor_id, user_id)</c> is what makes
/// a cross-account seal unstorable rather than merely unlikely: <c>user_isolation</c> on this table
/// reads <c>user_id</c> and never looks at the factor, so shortened to <c>factor_id</c> alone the
/// database would accept this account's next generation staged against somebody else's factor — and a
/// promotion would file it over their keys, at the one moment in an account's life when the old
/// generation has already gone.
/// </para>
/// <para>
/// <b>Proved by refusal, and each refusal is read by SQLSTATE <em>and</em> constraint name.</b> The
/// SQLSTATE alone cannot tell which rule answered — the width check and the version check both raise
/// <c>23514</c>, and this table can raise <c>23503</c> from either of two foreign keys — so the name is
/// the half of the answer that carries information. The names are spelled out here rather than read off
/// <c>KeyRotationSealConfiguration</c>, the idiom <c>WrappedAccountKeysSchemaTests.PrimaryKeyName</c>
/// keeps: a test taking its expectation from the thing under test agrees with whatever that thing later
/// decides.
/// </para>
/// <para>
/// <b>Nothing below asserts that a test passes.</b> Every probe states the code PostgreSQL returns and
/// the constraint it returns it under, and the ones that are meant to land assert an affected count —
/// because a refusal test whose insert never reached the table would pass for the wrong reason, and the
/// count is what says the probe had something to be refused about.
/// </para>
/// <para>
/// The probes are raw Npgsql on the container <b>superuser</b> connection for the two reasons
/// <see cref="WrappedAccountKeysSchemaTests" /> records, with one exception: the isolation test is the
/// point of an application connection and uses one. <see cref="KeyRotationSeal.For" /> refuses most
/// malformed rows one layer up, so an EF write would measure the domain rather than the schema; and
/// this table is policed, so a probe on an application connection could be refused by the policy instead
/// of by the constraint under test.
/// </para>
/// <para>
/// <b>The app role holds <c>SELECT</c> on this table and no write grant of any shape</b>, which is why
/// every write here is on the superuser and why the isolation test reads rather than writes. That is
/// not a gap in this file: the grant matrix pins the verb set, and a write probe on the app connection
/// would be answered by <c>42501</c> before any policy ran.
/// </para>
/// </remarks>
public sealed class KeyRotationSealSchemaTests
{
    /// <summary>
    /// A payload of any width but the one the framing defines is refused, from both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both sides, because neither may be repaired: a padded or truncated value is a well-formed row
    /// holding bytes whose tag cannot verify, and a promotion would copy it into
    /// <c>wrapped_account_keys</c> after the generation that did open has already been overwritten.
    /// </para>
    /// <para>
    /// <b>The short row is one byte under and the long row one byte over, and the short one is the
    /// interesting half.</b> 157 bytes clears the encapsulation floor of 94 comfortably and carries a
    /// recognised version byte, so nothing about the framing refuses it — only the exact width does.
    /// The <c>= 158</c> spelling is what makes both sides fail; written <c>&gt;= 94</c> the short row
    /// would store, and nothing else in this file would notice.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(-1)]
    [Arguments(1)]
    public async Task Database_RefusesASealOfTheWrongWidth(int offset)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Seeded account = await SeedAccountAsync(host, admin, "google-1", "person@example.com", 0x41, OwnFactorId);

        // Act
        await using NpgsqlCommand probe = BuildSealInsert(
            admin,
            account.UserId,
            account.FactorId,
            Payload(
                WrappedAccountKeys.EncapsulatedAccountKeysLength + offset,
                WrappedAccountKeys.EncapsulatedAccountKeysVersion,
                ProbeFiller));
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert — 23514 under the LENGTH check by name. The version check on this table raises the
        // same SQLSTATE, and both are on the same column, so the name is the only thing that says which
        // of the two answered.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(LengthCheckName);
        await Assert.That(await CountSealsAsync(admin, account.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// A payload of the right width whose leading byte names a framing version this deployment does not
    /// implement is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Version 2 is a client claiming a suite this deployment has never implemented; version 0 is a
    /// field nobody set — an all-zero buffer of the legal width is what an uninitialised member, a
    /// zero-filled allocation and a stubbed client all send. Both are the legal width, so only the
    /// version check can tell either of them from a value this system could interpret.
    /// </para>
    /// <para>
    /// <b>The version constant belongs to the encapsulation suite and never to the AEAD one beside
    /// it.</b> The two hold the same number today, so a probe built from the wrong one renders
    /// byte-identical bytes and would go on passing while asserting the wrong contract — which is
    /// exactly why the entity's own remarks say prose is what holds the two apart.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments((byte)(WrappedAccountKeys.EncapsulatedAccountKeysVersion - 1))]
    [Arguments((byte)(WrappedAccountKeys.EncapsulatedAccountKeysVersion + 1))]
    public async Task Database_RefusesASealCarryingAnUnknownFramingVersion(byte version)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Seeded account = await SeedAccountAsync(host, admin, "google-1", "person@example.com", 0x41, OwnFactorId);

        // Act — the exact width the framing defines, so the version byte is the only fault.
        await using NpgsqlCommand probe = BuildSealInsert(
            admin,
            account.UserId,
            account.FactorId,
            Payload(WrappedAccountKeys.EncapsulatedAccountKeysLength, version, ProbeFiller));
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(VersionCheckName);
        await Assert.That(await CountSealsAsync(admin, account.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// A second seal for one factor of one account is refused by the primary key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run produces one copy per factor, so a second row for one <c>(user_id, factor_id)</c> is two
    /// claims about which value a promotion should copy — both well-formed, both the right width, and
    /// nothing on this side able to open either to choose between them. The composite key is what makes
    /// that unstorable rather than a duplicate the promotion would have to guess about.
    /// </para>
    /// <para>
    /// The second payload carries a different filler, so a refusal that had replaced the row on its way
    /// to failing is visible: the read-back names the value the <em>first</em> insert wrote, which a
    /// count of 1 could not distinguish from a silent overwrite.
    /// </para>
    /// <para>
    /// A surrogate id added beside the pair would demote this to an ordinary index and make the
    /// duplicate storable. That is not a hypothetical edit — it is the shape every other table in this
    /// schema has, so it is the shape somebody tidying will reach for.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Database_RefusesASecondSealForOneFactor()
    {
        // Arrange — one account, one factor, one run, and one seal already staged.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Seeded account = await SeedAccountAsync(host, admin, "google-1", "person@example.com", 0x41, OwnFactorId);

        await using NpgsqlCommand seed = BuildSealInsert(
            admin, account.UserId, account.FactorId, WellFormedPayload(SeededFiller));
        await Assert.That(await seed.ExecuteNonQueryAsync()).IsEqualTo(1);

        // Act
        await using NpgsqlCommand probe = BuildSealInsert(
            admin, account.UserId, account.FactorId, WellFormedPayload(ProbeFiller));
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(PrimaryKeyName);
        await Assert.That(await CountSealsAsync(admin, account.UserId)).IsEqualTo(1L);
        await Assert.That(await ReadSealLeadingFillerAsync(admin, account.UserId, account.FactorId))
            .IsEqualTo(SeededFiller);
    }

    /// <summary>
    /// <b>The rule this table exists for.</b> A seal staged against another account's factor is refused
    /// by the composite foreign key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves of the tuple are real: the account owns a run, the other account owns a factor, and
    /// each row exists and is well-formed on its own. What does not exist is the PAIR — there is no
    /// <c>wrapped_account_keys</c> row whose <c>(factor_id, user_id)</c> is this factor under this
    /// owner — and that is the whole of what the composite key refuses. A key over <c>factor_id</c>
    /// alone would find the factor, accept the row, and leave this account's next generation staged
    /// against somebody else's authenticator.
    /// </para>
    /// <para>
    /// <b>The payload is well formed and the run is this account's own</b>, so neither check constraint
    /// and neither of the table's other rules can be what answers. The second account's factor is a row
    /// the seeding really wrote — the test asserts it — so this cannot pass on a factor that was never
    /// there.
    /// </para>
    /// <para>
    /// <see cref="KeyRotationSeal.For" /> refuses the same thing one ring up, by reading the owner off
    /// the loaded rotation and the factor off the loaded factor and comparing them. That is not a
    /// duplicate of this rule: the entity makes the mistake <em>unconstructable</em>, so it never
    /// reaches a <c>SaveChanges</c> part-way through a run, and this makes it <em>unstorable</em> by any
    /// path at all, raw SQL included. Delete neither.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Database_RefusesASealStagedAgainstAnotherAccountsFactor()
    {
        // Arrange — two accounts, each with its own passkey and its own factor, and a run in flight on
        // the first.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Seeded own = await SeedAccountAsync(host, admin, "google-1", "person@example.com", 0x41, OwnFactorId);
        Seeded other = await SeedAccountAsync(host, admin, "google-2", "other@example.com", 0x52, OtherFactorId);

        // The other account's factor is really there, so the refusal below cannot be about a row that
        // does not exist.
        await Assert.That(await CountFactorsAsync(admin, other.UserId)).IsEqualTo(1L);

        // Act — this account's run, that account's factor, a perfectly well-formed payload.
        await using NpgsqlCommand probe = BuildSealInsert(
            admin, own.UserId, other.FactorId, WellFormedPayload(ProbeFiller));
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert — 23503 from the composite key to wrapped_account_keys by name. The other foreign key
        // on this table raises the same SQLSTATE, so the name is what says the FACTOR half is what
        // refused rather than the run half.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(FactorForeignKeyName);
        await Assert.That(await CountSealsAsync(admin, own.UserId)).IsEqualTo(0L);
        await Assert.That(await CountSealsAsync(admin, other.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// A seal staged against a run that does not exist is refused by the other foreign key.
    /// </summary>
    /// <remarks>
    /// The control for the case above, and a rule in its own right. It is what makes a seal die with
    /// its run: <c>key_rotations</c> holds no <c>DELETE</c> grant of any shape, so a second begin
    /// replaces the staging row and this cascade is the only thing that clears the previous run's seals.
    /// Without the edge, a completion would find seals from two generations under one account and no
    /// column saying which is which.
    /// <para>
    /// The factor is this account's own and the payload is well formed, so the FACTOR key cannot be
    /// what answers — which is the half the constraint name proves, and the reason both names are
    /// asserted across this pair of tests rather than a bare <c>23503</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Database_RefusesASealWithNoRunToBelongTo()
    {
        // Arrange — an account with a factor and NO rotation staged.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, Handle(0x41));
        Guid factorId = await host.SeedWrappedAccountKeysAsync(credentialId, OwnFactorId);

        // Act
        await using NpgsqlCommand probe = BuildSealInsert(
            admin, userId, factorId, WellFormedPayload(ProbeFiller));
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(RotationForeignKeyName);
        await Assert.That(await CountSealsAsync(admin, userId)).IsEqualTo(0L);
    }

    /// <summary>
    /// <c>user_isolation</c> hides another account's seals from an application connection, and shows the
    /// session's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves, and the visible half is what makes the hidden half mean anything.</b> A read
    /// answering zero rows proves nothing on its own — a broken connection, a missing grant and a
    /// policy that hides everything all answer zero. The session's own row coming back is what says the
    /// read worked and the policy is discriminating rather than refusing.
    /// </para>
    /// <para>
    /// Read on an application connection with an identity and NO ambient budget, because this table is
    /// policed on the user. The superuser counts the same two rows beside it, which is what says the
    /// hidden row is genuinely there and genuinely hidden rather than never written.
    /// </para>
    /// <para>
    /// <b>This is a read probe and stays one, and the reason it gives has changed.</b> It used to be
    /// that the role held <c>SELECT</c> here and nothing else, so an INSERT on this connection would be
    /// answered by <c>42501</c> before any policy ran. That is no longer true — a begin took
    /// <c>INSERT</c> and <c>UPDATE (encapsulated_account_keys)</c> — so the two write arms are live, and
    /// they are observed where the other policed tables' are:
    /// <c>RlsIsolationTests.Database_RefusesASealInsertNamingAnotherAccount</c> and
    /// <c>Database_RefusesToUpdateAnotherAccountsSeal_WhileStillAllowingItsOwn</c>, each with the
    /// positive control a bare <c>42501</c> or a bare affected count cannot do without. What stays here
    /// is the <c>USING</c> arm, read-side, which is the half those two cannot reach.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Database_HidesAnotherAccountsSeals()
    {
        // Arrange — two accounts, each with a run in flight and a seal staged for its own factor.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Seeded own = await SeedAccountAsync(host, admin, "google-1", "person@example.com", 0x41, OwnFactorId);
        Seeded other = await SeedAccountAsync(host, admin, "google-2", "other@example.com", 0x52, OtherFactorId);

        await using (NpgsqlCommand seedOwn = BuildSealInsert(
            admin, own.UserId, own.FactorId, WellFormedPayload(SeededFiller)))
        {
            await Assert.That(await seedOwn.ExecuteNonQueryAsync()).IsEqualTo(1);
        }

        await using (NpgsqlCommand seedOther = BuildSealInsert(
            admin, other.UserId, other.FactorId, WellFormedPayload(ProbeFiller)))
        {
            await Assert.That(await seedOther.ExecuteNonQueryAsync()).IsEqualTo(1);
        }

        // Both rows are really there, read without a policy in the way.
        await Assert.That(await CountSealsAsync(admin, own.UserId)).IsEqualTo(1L);
        await Assert.That(await CountSealsAsync(admin, other.UserId)).IsEqualTo(1L);

        // Act — the first account's session, reading the whole table as the application role sees it.
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(own.UserId);
        long visible = await CountAllSealsAsync(app);
        long visibleOwn = await CountSealsAsync(app, own.UserId);
        long visibleOther = await CountSealsAsync(app, other.UserId);

        // Assert — one row visible, and it is this account's. The unqualified count is what catches a
        // policy that had been dropped: with none in force the table holds two rows and this line would
        // say so, while both keyed counts would still answer 1 and 1 and look correct.
        await Assert.That(visible).IsEqualTo(1L);
        await Assert.That(visibleOwn).IsEqualTo(1L);
        await Assert.That(visibleOther).IsEqualTo(0L);
    }

    /// <summary>
    /// The names PostgreSQL reports, spelled out rather than read off
    /// <c>KeyRotationSealConfiguration</c>.
    /// </summary>
    /// <remarks>
    /// The idiom <c>WrappedAccountKeysSchemaTests.PrimaryKeyName</c> keeps and for its reason: a test
    /// taking its expectation from the thing under test agrees with whatever that thing later decides,
    /// and what these assertions are worth is that the names are the ones a repository would filter a
    /// violation on.
    /// </remarks>
    private const string PrimaryKeyName = "PK_key_rotation_seals";

    private const string LengthCheckName = "CK_key_rotation_seals_encapsulated_account_keys_length";

    private const string VersionCheckName = "CK_key_rotation_seals_encapsulated_account_keys_version";

    private const string RotationForeignKeyName = "FK_key_rotation_seals_key_rotations";

    private const string FactorForeignKeyName = "FK_key_rotation_seals_wrapped_account_keys";

    /// <summary>
    /// The two client-minted factor identifiers. Fixed rather than minted so a failure message names
    /// values that can be found in this file, and visibly different so a row read back under the wrong
    /// owner is a failure rather than a coincidence.
    /// </summary>
    private static readonly Guid OwnFactorId = new("0199f3a1-0000-7000-8000-0000000000c1");

    private static readonly Guid OtherFactorId = new("0199f3a1-0000-7000-8000-0000000000c2");

    /// <summary>
    /// The filler the seeded seal carries, and the one every refused probe carries. Different from each
    /// other, so a read-back can say which insert wrote the row that is there.
    /// </summary>
    private const byte SeededFiller = 0x5A;

    private const byte ProbeFiller = 0x6B;

    /// <summary>
    /// How wide the seeded staged manifests are. Comfortably inside
    /// <c>CK_key_rotations_staged_manifest_length</c> from both sides, so that constraint is never what
    /// refuses a row this file seeds.
    /// </summary>
    private const int ManifestBytes = 32;

    /// <summary>
    /// The generation the seeded runs name. Above <c>CK_key_rotations_staged_rotation_epoch</c>'s floor,
    /// which is 1 because epoch 0 is the absence of a manifest.
    /// </summary>
    private const int StagedEpoch = 2;

    /// <summary>
    /// Fixed UTC instant for every seeded row. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>One account with a passkey, a factor and a rotation in flight.</summary>
    private sealed record Seeded(Guid UserId, Guid FactorId);

    /// <summary>
    /// Seeds an account, its passkey, one factor and one staged rotation, and hands back the two ids the
    /// probes need.
    /// </summary>
    /// <remarks>
    /// The rotation is written on the superuser connection, because the app role holds an <c>INSERT</c>
    /// on <c>key_rotations</c> but the identity plumbing a policed write needs is not what any test here
    /// is measuring. The factor goes through <see cref="RepositoryTestHost.SeedWrappedAccountKeysAsync" />
    /// so that the row is the shape production writes.
    /// </remarks>
    private static async Task<Seeded> SeedAccountAsync(
        RepositoryTestHost host,
        NpgsqlConnection admin,
        string googleSubject,
        string email,
        byte handleFill,
        Guid factorId)
    {
        Guid userId = await host.SeedUserAsync(googleSubject, email);
        Guid credentialId = await host.SeedPasskeyAsync(userId, Handle(handleFill));
        Guid seededFactorId = await host.SeedWrappedAccountKeysAsync(credentialId, factorId);

        await using NpgsqlCommand rotation = new(
            "insert into key_rotations "
            + "(user_id, rotation_id, staged_manifest, staged_rotation_epoch, started_at_utc) "
            + "values (@user_id, @rotation_id, @staged_manifest, @staged_rotation_epoch, @started_at_utc)",
            admin);
        rotation.Parameters.AddWithValue("user_id", userId);
        rotation.Parameters.AddWithValue("rotation_id", Guid.CreateVersion7());
        rotation.Parameters.AddWithValue("staged_manifest", Manifest(handleFill));
        rotation.Parameters.AddWithValue("staged_rotation_epoch", StagedEpoch);
        rotation.Parameters.AddWithValue("started_at_utc", SeedInstant);
        await rotation.ExecuteNonQueryAsync();

        return new Seeded(userId, seededFactorId);
    }

    /// <summary>
    /// Builds one <c>key_rotation_seals</c> INSERT, every column named.
    /// </summary>
    /// <remarks>
    /// The column list is written out from <see cref="KeyRotationSeal" />'s contract rather than read
    /// off a configuration — the sibling idiom, and what lets a probe put a value in a column no domain
    /// path can produce. It also means this file has to be edited when the entity gains a column, which
    /// is the intended cost: a test that built its statement from the mapping would keep passing while
    /// the mapping drifted.
    /// </remarks>
    private static NpgsqlCommand BuildSealInsert(
        NpgsqlConnection connection,
        Guid userId,
        Guid factorId,
        byte[] encapsulatedAccountKeys)
    {
        NpgsqlCommand insert = new(
            "insert into key_rotation_seals (user_id, factor_id, encapsulated_account_keys) "
            + "values (@user_id, @factor_id, @encapsulated_account_keys)",
            connection);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("factor_id", factorId);
        insert.Parameters.AddWithValue("encapsulated_account_keys", encapsulatedAccountKeys);

        return insert;
    }

    /// <summary>
    /// A payload at the framing's exact width carrying its own version, so no check constraint is what
    /// answers a probe built with it.
    /// </summary>
    /// <remarks>
    /// Both numbers are read off <see cref="WrappedAccountKeys" />, which is where the check constraints
    /// on this table are rendered from as well — so a probe cannot drift into being refused for a bound
    /// that has moved. The AEAD suite's constants are never read here: they hold the same version number
    /// today, so a cross-read would render plausible bytes and refuse for the wrong reason.
    /// </remarks>
    private static byte[] WellFormedPayload(byte filler) =>
        Payload(
            WrappedAccountKeys.EncapsulatedAccountKeysLength,
            WrappedAccountKeys.EncapsulatedAccountKeysVersion,
            filler);

    private static byte[] Payload(int length, byte version, byte filler)
    {
        byte[] payload = new byte[length];
        Array.Fill(payload, filler);

        if (length > 0)
        {
            payload[0] = version;
        }

        return payload;
    }

    /// <summary>A staged factor manifest of <see cref="ManifestBytes" /> bytes.</summary>
    /// <remarks>
    /// Nothing on this side parses a manifest — it is authenticated client-side material — so any run of
    /// bytes inside the band is a well-formed value as far as the schema is concerned.
    /// </remarks>
    private static byte[] Manifest(byte fill) => [.. Enumerable.Repeat(fill, ManifestBytes)];

    /// <summary>
    /// A WebAuthn credential handle of 32 bytes, every one of them <paramref name="fill" />. The fill
    /// byte is required rather than defaulted because the column is unique, so two registrations of "a
    /// passkey" would collide on that index and the seeding would fail before a probe ran.
    /// </summary>
    private static byte[] Handle(byte fill) => [.. Enumerable.Repeat(fill, 32)];

    private static Task<long> CountSealsAsync(NpgsqlConnection connection, Guid userId) =>
        ScalarCountAsync(
            connection, "select count(*) from key_rotation_seals where user_id = @id", userId);

    private static Task<long> CountFactorsAsync(NpgsqlConnection connection, Guid userId) =>
        ScalarCountAsync(
            connection, "select count(*) from wrapped_account_keys where user_id = @id", userId);

    private static Task<long> CountAllSealsAsync(NpgsqlConnection connection) =>
        ScalarCountAsync(connection, "select count(*) from key_rotation_seals", id: null);

    private static async Task<long> ScalarCountAsync(
        NpgsqlConnection connection,
        string sql,
        Guid? id)
    {
        await using NpgsqlCommand command = new(sql, connection);

        if (id is { } value)
        {
            command.Parameters.AddWithValue("id", value);
        }

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the query
        // changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The byte after the version on the stored seal — the filler, which is what says WHICH insert wrote
    /// the row that is there.
    /// </summary>
    /// <remarks>
    /// A count cannot tell a refused second insert from one that silently replaced the first: both leave
    /// the table at one row. This reads the value back.
    /// </remarks>
    private static async Task<byte> ReadSealLeadingFillerAsync(
        NpgsqlConnection connection,
        Guid userId,
        Guid factorId)
    {
        await using NpgsqlCommand read = new(
            "select get_byte(encapsulated_account_keys, 1) from key_rotation_seals "
            + "where user_id = @user_id and factor_id = @factor_id",
            connection);
        read.Parameters.AddWithValue("user_id", userId);
        read.Parameters.AddWithValue("factor_id", factorId);

        return await read.ExecuteScalarAsync() switch
        {
            int value => (byte)value,
            var unexpected => throw new InvalidOperationException(
                $"Expected one seal payload byte, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Runs a statement that must be refused and returns the refusal, throwing if it went through — the
    /// shape <see cref="KeyRotationSchemaTests" /> uses, because a probe that succeeds is a defect in
    /// the schema rather than an assertion to report.
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
