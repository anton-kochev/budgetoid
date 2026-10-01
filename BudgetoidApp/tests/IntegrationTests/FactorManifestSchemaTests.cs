using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers what <c>factor_manifests</c> refuses, as refusals PostgreSQL actually issued: the epoch
/// floor, both ends of the manifest length band, one row per account, an owner that exists, and one
/// account's session being unable to see another account's row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Until this file existed nothing had ever watched this table refuse anything.</b> Its two check
/// constraints were pinned in two censuses — <c>BudgetoidDbContextConstructionTests</c> reads the
/// model's rendering and <c>SchemaConstraintSnapshotTests</c> reads <c>pg_get_constraintdef</c> — and
/// both of those are assertions about <em>text</em>. A predicate can be spelled correctly, pinned
/// twice, and still be attached to nothing a statement passes through; the censuses would stay green.
/// Both sibling tables have a statement-shaped test for exactly that reason —
/// <see cref="KeyRotationSchemaTests" /> and <see cref="WrappedAccountKeysSchemaTests" /> — and this
/// table had none.
/// </para>
/// <para>
/// <b>The probes are raw Npgsql on the container superuser connection, following the reasoning both
/// siblings record</b>, and on this table there is a third reason on top of their two. The first two
/// are theirs unchanged: <c>FactorManifest.For</c> refuses most malformed rows one layer up, so an EF
/// write would measure the Domain rather than the schema, and the table is policed by
/// <c>user_isolation</c>, so on an application connection a probe could be refused by the policy
/// rather than by the constraint and the SQLSTATE read back would be the wrong one arriving for the
/// right-looking reason.
/// </para>
/// <para>
/// <b>The third is this table's own, and registration taking the <c>INSERT</c> made it stronger rather
/// than retiring it.</b> The app role now holds <c>SELECT</c> <em>and</em> <c>INSERT</c> here —
/// <see cref="AppRoleGrantMatrixTests" /> pins that pair — where it once held <c>SELECT</c> alone. The
/// old argument was that an application-connection probe was <em>impossible</em>: every INSERT answered
/// <c>42501</c> before a constraint was consulted, which is a refusal nobody could mistake for the rule
/// under test. What replaces it is worse, because <b><c>42501</c> is also what a <c>WITH CHECK</c>
/// violation answers</b> — "new row violates row-level security policy", the SQLSTATE
/// <c>RlsIsolationTests.Database_RefusesAWrappedKeyInsertNamingAnotherAccount</c> asserts over the
/// identical policy shape one table across. So the one code that used to mean "no grant, this probe
/// never ran" now means "no grant <em>or</em> the policy refused the row", and a probe whose session
/// named the wrong owner would read as a privilege problem while being an isolation refusal. A probe
/// whose session names the <em>right</em> owner clears both and then meets a queue — this table's two
/// <c>CHECK</c> constraints, its primary key, its foreign key and the policy's own <c>WITH CHECK</c> —
/// whose order of evaluation is PostgreSQL's business and not this schema's, so which of them answers
/// is not a fact these tests may assert. The superuser connection takes the grant and the policy out of
/// the statement altogether, which is the whole of why the named constraint is the only thing left that
/// can answer.
/// </para>
/// <para>
/// <b>The one test that must run on the application connection is the isolation one, and "it only
/// reads" is now the load-bearing half of that sentence rather than an aside.</b> A read exercises the
/// policy's <c>USING</c> half and nothing else, so
/// <see cref="Database_HidesAnotherAccountsManifest_FromASessionNamingThisUser" /> is a claim about
/// what a session may <em>see</em> and never about what it may <em>write</em>. <b>Nothing in this file
/// — and nothing anywhere in this suite — has watched <c>user_isolation</c> refuse a manifest
/// write.</b> That policy's <c>WITH CHECK</c> arm is reachable today, because the role holds the
/// <c>INSERT</c> that reaches it, and it is unobserved. <c>RlsIsolationTests</c> holds exactly that
/// shape for <c>budgets</c>, <c>sessions</c>, <c>passkey_signature_counters</c> and
/// <c>wrapped_account_keys</c>, and holds it for this table for none.
/// </para>
/// <para>
/// <b>The column list is written out from the table's contract rather than built from the mapping</b>,
/// which is the sibling idiom and the same intended cost: the day the entity gains a column this file
/// has to be edited, where a statement rendered from the model would keep passing while the model
/// drifted.
/// </para>
/// <para>
/// <b>Every refusal asserts the constraint name and not the SQLSTATE alone.</b> Three of the six
/// probes here would answer <c>23514</c>, and two of those three are the two ends of one band — so the
/// SQLSTATE on its own cannot tell an epoch refusal from a width refusal, and a constraint accidentally
/// dropped would be covered by whichever of its neighbours happened to fire. The name is also the only
/// handle a caller has: a handler that one day writes a manifest can only tell "this account already
/// has one" from every other unique violation the same statement raises by matching
/// <c>PK_factor_manifests</c>.
/// </para>
/// </remarks>
public sealed class FactorManifestSchemaTests
{
    // EVERY ACCOUNT HERE IS SEEDED WITHOUT A MANIFEST, and the flag is the whole reason this file still
    // measures the schema. RepositoryTestHost.SeedUserAsync files an account's first manifest by
    // default, because that is what registration does and every suite driving a route needs the account
    // to be in the state the product leaves it in. This file is about the TABLE: it writes its own rows
    // with raw INSERTs so it can offer the column values a domain factory refuses, and a row already
    // standing would turn every one of those probes into a primary-key violation — a green SQLSTATE
    // assertion for a constraint nobody was testing, and a count that never returns to zero.
    //
    // It is not a workaround for the seeding either. An account with no manifest is a state the schema
    // permits and the application refuses, which is exactly the boundary a schema test is written on
    // the far side of.

    [Test]
    public async Task Database_RefusesAnEpochBelowTheFloor()
    {
        // Arrange — one account and nothing else. This row is well-formed in every other respect: the
        // owner exists, the manifest sits inside the band, and no row is there to collide with, so the
        // epoch is the only thing left that can answer.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com", withFactorManifest: false);

        // Act — epoch 0, which is not merely a small number. Epoch 0 is the ABSENCE of a manifest: an
        // account with no row answers 0, which is the state of every account that exists today, so a
        // stored row claiming it would assert its own absence and the one read that has to tell
        // "never rotated" from "rotated to generation zero" could not.
        await using NpgsqlCommand probe = BuildManifestInsert(
            admin, userId, Manifest(length: 64, filler: 0xAB), rotationEpoch: 0);
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(RotationEpochCheckName);

        // And nothing landed. A CHECK that rejected the row on its way to storing it would be a
        // contradiction, but the count is what says so rather than the SQLSTATE, which only reports
        // that the statement was refused.
        await Assert.That(await CountManifestsAsync(admin, userId)).IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesAManifestWiderThanTheCap()
    {
        // Arrange — one account, and a manifest one byte over the cap. One byte over rather than
        // comfortably over, because the interesting failure is a band written with the wrong
        // comparison rather than one written with the wrong number.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com", withFactorManifest: false);

        // Act
        await using NpgsqlCommand probe = BuildManifestInsert(
            admin, userId, Manifest(length: 4097, filler: 0xAB), rotationEpoch: 1);
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert — REFUSED AND NOT TRUNCATED, which is the whole reason the ceiling is a constraint
        // rather than a column width somebody could soften. This blob is the sole carrier of every
        // recovery factor's public key, so a cut lands on whichever factor sat past the line: that
        // factor stops being encapsulatable-to, the row left behind is well formed and carries a
        // plausible epoch, and the loss surfaces on the day somebody reaches for the factor that is
        // gone.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(ManifestLengthCheckName);
        await Assert.That(await CountManifestsAsync(admin, userId)).IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesAnEmptyManifest()
    {
        // Arrange — one account, and zero bytes. Its own test rather than a second argument to the one
        // above although both ends belong to one constraint: an empty bytea is exactly what an unset
        // member sends, so this end is the one a real caller reaches by forgetting rather than by
        // overflowing, and a band whose lower bound was dropped would still refuse the wide probe.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com", withFactorManifest: false);

        // Act
        await using NpgsqlCommand probe = BuildManifestInsert(
            admin, userId, [], rotationEpoch: 1);
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert — without this end a caller that forgot to attach the manifest files a row naming no
        // factor at all: an account with no way back in, stored as though it had one, and nothing about
        // the row saying so.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(ManifestLengthCheckName);
        await Assert.That(await CountManifestsAsync(admin, userId)).IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesASecondManifestForOneAccount()
    {
        // Arrange — one account with a manifest already filed. The stored row and the probe differ in
        // both of the columns that are not the key: different bytes, a later epoch. That is deliberate
        // rather than decorative — the only thing the two rows share is the account, so nothing but the
        // primary key can be what refuses the second.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com", withFactorManifest: false);

        await using NpgsqlCommand seed = BuildManifestInsert(
            admin, userId, Manifest(length: 64, filler: StoredFiller), rotationEpoch: 1);
        await Assert.That(await seed.ExecuteNonQueryAsync()).IsEqualTo(1);

        // Act
        await using NpgsqlCommand probe = BuildManifestInsert(
            admin, userId, Manifest(length: 96, filler: ProbeFiller), rotationEpoch: 2);
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert — one manifest per account is the rule the whole design leans on, because the manifest
        // is authenticated as a SET: a second row would be a second claim about which factors exist,
        // and a client choosing what to encapsulate to would have no way to ask which of them it was
        // looking at. A surrogate id added beside user_id would demote this key to an ordinary index
        // and make the duplicate storable — the shape every other table in this schema has, and
        // therefore the shape somebody tidying reaches for first.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(PrimaryKeyName);

        // And the account still holds exactly one manifest, and it is the FIRST one. The count alone
        // would not say that: a probe that had replaced the row on its way to failing leaves the count
        // at 1 while the stored set of factor public keys is silently the other one.
        await Assert.That(await CountManifestsAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await ReadFirstManifestByteAsync(admin, userId)).IsEqualTo(StoredFiller);
    }

    [Test]
    public async Task Database_RefusesAManifestForAnAccountThatDoesNotExist()
    {
        // Arrange — a real account is seeded and then deliberately not used, so the database is not
        // empty and the foreign key has a populated users table to fail against rather than a vacant
        // one.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        await host.SeedUserAsync("google-1", "person@example.com", withFactorManifest: false);

        // Act — an owner nobody minted. Every other column is well-formed, so the key is the only thing
        // that can answer.
        await using NpgsqlCommand probe = BuildManifestInsert(
            admin, UnknownUserId, Manifest(length: 64, filler: 0xAB), rotationEpoch: 1);
        PostgresException refusal = await RefusalOfAsync(probe);

        // Assert — the key names users directly rather than reaching the account through a credential,
        // which is the point of the table: a manifest belongs to the ACCOUNT and names every factor at
        // once, so a key to any one credential would be a claim that the set belonged to one member of
        // it. An orphan row here would be a list of public keys kept against nobody, and — since the
        // cascade from users is the only thing that ever deletes from this table — one nothing would
        // ever carry away.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(UserForeignKeyName);
        await Assert.That(await CountManifestsAsync(admin, UnknownUserId)).IsEqualTo(0L);
    }

    [Test]
    public async Task Database_HidesAnotherAccountsManifest_FromASessionNamingThisUser()
    {
        // Arrange — two accounts, each with a manifest of its own, and one app-role connection naming
        // the first. Two accounts because this rule is about isolation: a single account's row would be
        // invisible to it, and the foreign count would come back zero with or without a policy.
        //
        // SEEDED ON THE SUPERUSER CONNECTION, AND NO LONGER BECAUSE THE APP ROLE CANNOT INSERT — it can,
        // since registration took the grant. It is because the second row belongs to the OTHER account,
        // so filing it from an app connection would need a second session naming that owner, and each
        // seeding statement would then be judged by the same policy's WITH CHECK arm. This test is about
        // the USING arm. Arranging it through the arm it is measuring would make a green here mean
        // "whatever the policy does, it does it consistently", which is true of a policy that does
        // nothing.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        Guid sessionUserId = await host.SeedUserAsync("google-1", "person@example.com", withFactorManifest: false);
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com", withFactorManifest: false);

        await using (NpgsqlCommand seedOwn = BuildManifestInsert(
            admin, sessionUserId, Manifest(length: 64, filler: StoredFiller), rotationEpoch: 1))
        {
            await Assert.That(await seedOwn.ExecuteNonQueryAsync()).IsEqualTo(1);
        }

        await using (NpgsqlCommand seedForeign = BuildManifestInsert(
            admin, otherUserId, Manifest(length: 64, filler: ProbeFiller), rotationEpoch: 1))
        {
            await Assert.That(await seedForeign.ExecuteNonQueryAsync()).IsEqualTo(1);
        }

        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(sessionUserId);

        // Act — both halves on one session. The own count is not decoration: a policy that hid every
        // row from everybody would satisfy the foreign half on its own, and only this notices.
        long own = await CountManifestsAsync(app, sessionUserId);
        long foreign = await CountManifestsAsync(app, otherUserId);

        // Assert — the application DOES read this table now, through AccountKeyReadService's single
        // statement behind GET /api/me/account-keys, and FactorManifestConfiguration still declares no
        // query filter of any kind. That pair is what makes this the whole of the rule rather than the
        // lower of two layers: there is a real read, and user_isolation is the only thing between one
        // account's session and another account's list of factor public keys. What leaks is not money: these are
        // public keys, and publishing a public key is what a public key is for. What leaks is HOW MANY
        // recovery factors somebody holds and what each of them is, which is a map of another person's
        // recovery arrangements.
        await Assert.That(own).IsEqualTo(1L);
        await Assert.That(foreign).IsEqualTo(0L);
    }

    /// <summary>
    /// The three names PostgreSQL reports for this table's rules.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than read off <c>FactorManifestConfiguration</c>, which pins two of the three
    /// as public constants for a future caller's benefit. The reason is the one
    /// <c>WrappedAccountKeysSchemaTests.PrimaryKeyName</c> gives: a test taking its expectation from
    /// the thing under test agrees with whatever that thing later decides, and what these assertions
    /// are worth depends entirely on the names being the ones a handler would filter on. This file is
    /// the executable form of "and they are still called that".
    /// </remarks>
    private const string PrimaryKeyName = "PK_factor_manifests";

    private const string RotationEpochCheckName = "CK_factor_manifests_rotation_epoch";

    private const string ManifestLengthCheckName = "CK_factor_manifests_manifest_length";

    private const string UserForeignKeyName = "FK_factor_manifests_users";

    /// <summary>
    /// The filler of the manifest that is expected to survive, and the filler of every manifest that is
    /// not. Distinct so that a row read back names which write produced it — nothing else on these rows
    /// could, since one account holds at most one and every constraint reads the same on all of them.
    /// </summary>
    private const byte StoredFiller = 0x7C;

    private const byte ProbeFiller = 0x8D;

    /// <summary>
    /// An owner id nothing ever mints. Fixed rather than generated so a failure message names a value
    /// that can be found in this file, and visibly outside the version-7 range every seeded id falls in.
    /// </summary>
    private static readonly Guid UnknownUserId = new("0199f3a1-0000-7000-8000-0000000000aa");

    /// <summary>
    /// Builds a manifest of <paramref name="length" /> bytes, every one <paramref name="filler" />.
    /// </summary>
    /// <remarks>
    /// Filler bytes rather than a real envelope, and not because the column holds anything else: a real
    /// manifest is an AEAD envelope sealed under the account's content key, in the same framing
    /// <c>RepositoryTestHost.WrappedPrivateKeyPayload</c> builds. Filler is honest here because the
    /// column checks only a length band and an owner, and a probe that looked like a real envelope would
    /// suggest it checks more. Nothing on this side can verify the envelope — the tag is checkable only
    /// by a client holding the account's content key, and no such client exists in this file — so the
    /// length band and the owner are the whole of what a row has to satisfy.
    /// </remarks>
    private static byte[] Manifest(int length, byte filler) => [.. Enumerable.Repeat(filler, length)];

    /// <summary>
    /// Builds one <c>factor_manifests</c> INSERT, every column named.
    /// </summary>
    /// <remarks>
    /// Three columns and no fourth: this table carries no timestamp. Written out rather than rendered
    /// from the mapping, for the reason the class remarks give.
    /// </remarks>
    private static NpgsqlCommand BuildManifestInsert(
        NpgsqlConnection connection,
        Guid userId,
        byte[] manifest,
        int rotationEpoch)
    {
        NpgsqlCommand insert = new(
            "insert into factor_manifests (user_id, manifest, rotation_epoch) " +
            "values (@user_id, @manifest, @rotation_epoch)",
            connection);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("manifest", manifest);
        insert.Parameters.AddWithValue("rotation_epoch", rotationEpoch);

        return insert;
    }

    private static async Task<long> CountManifestsAsync(NpgsqlConnection connection, Guid userId)
    {
        await using NpgsqlCommand count = new(
            "select count(*) from factor_manifests where user_id = @user_id", connection);
        count.Parameters.AddWithValue("user_id", userId);

        // Pattern-matched rather than cast-and-null-forgive, the shape KeyRotationSchemaTests keeps: a
        // null or unexpected scalar means the query changed shape, and that should fail loudly here
        // rather than at the assertion.
        return await count.ExecuteScalarAsync() switch
        {
            long rows => rows,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from 'factor_manifests', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The first byte of the manifest the account holds — the half of the assertion a count cannot
    /// make, because a replaced row and a refused one are both a count of 1.
    /// </summary>
    private static async Task<byte> ReadFirstManifestByteAsync(
        NpgsqlConnection connection,
        Guid userId)
    {
        await using NpgsqlCommand read = new(
            "select manifest from factor_manifests where user_id = @user_id", connection);
        read.Parameters.AddWithValue("user_id", userId);

        return await read.ExecuteScalarAsync() switch
        {
            byte[] { Length: > 0 } manifest => manifest[0],
            var unexpected => throw new InvalidOperationException(
                $"Expected one manifest, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Runs a statement that must be refused and returns the refusal, throwing if it went through — the
    /// shape both sibling schema files use, because a probe that succeeds is a defect in the schema
    /// rather than an assertion to report.
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

    private static async Task<NpgsqlConnection> OpenAdminAsync(RepositoryTestHost host)
    {
        NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        return admin;
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();

        return host;
    }
}
