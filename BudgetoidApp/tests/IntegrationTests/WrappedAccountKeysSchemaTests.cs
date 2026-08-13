using Domain.Users;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the two rules <c>wrapped_account_keys</c> holds on its own, which neither the grant matrix nor
/// row-level security can express: a factor identifier names <b>one</b> pair of envelopes, table-wide,
/// and a credential names <b>as many pairs as it has factors</b>.
/// </summary>
/// <remarks>
/// <para>
/// Its own file rather than a test in <c>AppRoleGrantsTests</c> or <c>RlsIsolationTests</c>, because it
/// is neither of those things — no privilege and no policy is involved, and both of those files open
/// with a statement of what they measure that these tests would falsify. Not in
/// <c>PasskeySchemaTests</c> either: that file scopes itself to "the three passkey tables", and this
/// one holds the keys of whichever factors have a key-encryption key — a set of recovery codes as
/// readily as a passkey.
/// </para>
/// <para>
/// The probe is raw Npgsql on the container <b>superuser</b> connection, following the reasoning
/// <see cref="PasskeySchemaTests" /> records for its own probes and for the same two reasons.
/// <see cref="WrappedAccountKeys.For" /> refuses most malformed rows one layer up, so an EF write would
/// measure the domain rather than the schema; and this table is policed by <c>user_isolation</c>, so on
/// an application connection a probe row could be refused by the policy instead of by the key, and the
/// SQLSTATE being read would be the wrong one.
/// </para>
/// <para>
/// <b>The two tests are a matched pair over one key, and neither says enough without the other.</b> The
/// identity of a row is the FACTOR and not the credential, and a set of recovery codes is what makes the
/// difference visible: a passkey is one factor under one credential, but a set is <em>ten separate
/// secrets</em> under one <c>credentials</c> row, and the client derives a key-encryption key from each
/// CODE. Ten codes are therefore ten key-encryption keys and ten pairs of envelopes, no one of which can
/// stand for the set — keyed on <c>credential_id</c> the table stored exactly one of them, so nine codes
/// of every set opened nothing at all, and the holder would learn that only by redeeming one, getting a
/// session, and finding the account still locked. So <c>factor_id</c> is the primary key and
/// <c>credential_id</c> is an ordinary, non-unique column. What did <em>not</em> relax is the rule the
/// other test states, and moving the key is what now carries it.
/// </para>
/// <para>
/// The refusal asserts the constraint name and not the SQLSTATE alone. The name is the half of the
/// answer that carries information: a repository filters a <c>23505</c> on it to tell a
/// client-minted-identifier collision — a claim on a factor that already exists — from every other
/// unique violation the same statement could raise. It is also why the primary key is the table's
/// <em>only</em> unique constraint rather than one beside a separate unique index over the same column:
/// a second constraint saying the same thing is a second name the same duplicate could arrive under.
/// </para>
/// </remarks>
public sealed class WrappedAccountKeysSchemaTests
{
    [Test]
    public async Task Database_RefusesASecondFactorIdentifier()
    {
        // Arrange — one account, two registered passkeys, and the account's two keys already filed
        // against the first. One account rather than two, and that is the strictest form of the
        // question rather than a convenience: PK_wrapped_account_keys is unique across the WHOLE TABLE
        // rather than per owner, so if the duplicate is refused even here — same user_id, same
        // credential_type, nothing to separate the two rows but the credential — then no scoping is
        // quietly doing the work. A cross-account collision surfaces as this same 23505, from this same
        // key, which is exactly what a table-wide key is for: scoping it per account would make that
        // duplicate storable and leave the associated data ambiguous precisely where it is trusted. The
        // two handles differ, or IX_passkey_public_keys_webauthn_credential_id would refuse the second
        // registration.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid registeredCredentialId = await host.SeedPasskeyAsync(userId, Handle(0x41));
        Guid secondCredentialId = await host.SeedPasskeyAsync(userId, Handle(0x52));
        Guid factorId = await host.SeedWrappedAccountKeysAsync(registeredCredentialId, SharedFactorId);

        // Act — the same factor identifier, filed against the account's other credential. The
        // credential_id differs, and that is now deliberately NOT a second defect the row could be
        // refused for: a credential may carry as many rows as it has factors, which the test below
        // states, so nothing about this row is wrong except the factor identifier it claims. The
        // envelopes are well-formed at the one legal width and version, so none of the four length and
        // version checks refuses it either, and 'passkey' agrees with the credential's own type, so the
        // composite foreign key is satisfied.
        await using NpgsqlCommand insert = BuildWrappedKeysInsert(
            admin,
            secondCredentialId,
            userId,
            factorId,
            PasskeyColumnValue,
            ProbeContentKeyFiller,
            ProbeIndexKeyFiller);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — the factor identifier is minted by the CLIENT, which is what makes uniqueness a rule
        // here rather than a lookup that happens to hold: nothing else in the system stops two rows
        // carrying the same one. It is also the associated data every envelope involved was sealed
        // with, so a shared factor id would let one factor's keys be opened against another's — and the
        // second registration is the only place anybody would ever learn of the collision, which is why
        // it has to be the thing that fails.
        //
        // The name is the primary key's, and it moved here from a separate unique index over the same
        // column. That is the whole of what changed about this rule: the uniqueness the client-minted
        // identifier needs is now carried by the key rather than beside it, so there is exactly one
        // constraint a repository has to recognise instead of two spellings of one fact.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(PrimaryKeyName);

        // And nothing landed. The SQLSTATE says the statement was rejected; only this says the factor
        // still names exactly one pair of envelopes, which is the whole of the rule.
        await using NpgsqlCommand count = new(
            "select count(*) from wrapped_account_keys where factor_id = @factor_id", admin);
        count.Parameters.AddWithValue("factor_id", factorId);
        await Assert.That(await count.ExecuteScalarAsync()).IsEqualTo(1L);
    }

    [Test]
    public async Task Database_AcceptsSeveralWrappedKeyRowsForOneCredential()
    {
        // Arrange — one account and ONE credential, of type recovery_codes, which is the whole shape of
        // the problem in one row: an issued set is ten separate secrets filed under a single credentials
        // row, because a set is revoked and counted as a unit. Raw SQL for the credential rather than
        // Credential.CreateRecoveryCodes, for the reason AppRoleGrantsTests keeps for its own copy of
        // this helper: what is under test is the schema, so the arrangement must not depend on a write
        // path that is itself being measured.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await InsertRecoveryCodesCredentialAsync(admin, userId);

        // Act — one row per code, each with its own client-minted factor id and its own pair of
        // envelopes. Ten rather than two, because ten is the number the generation route requires and
        // the count is the point rather than the plural: the client derives a key-encryption key from
        // each CODE, so the set's twenty envelopes are sealed under ten different keys and no one pair
        // of them can stand for the others. The affected count is asserted on every insert rather than
        // only on the last, so a row silently replacing its predecessor could not pass for a row landing
        // beside it.
        for (int ordinal = 0; ordinal < RequiredCodeCount; ordinal++)
        {
            await using NpgsqlCommand insert = BuildWrappedKeysInsert(
                admin,
                credentialId,
                userId,
                CodeFactorId(ordinal),
                RecoveryCodesColumnValue,
                ContentKeyFiller(ordinal),
                IndexKeyFiller(ordinal));
            await Assert.That(await insert.ExecuteNonQueryAsync()).IsEqualTo(1);
        }

        // Assert — every row is back, and every row is its own. Ordered by factor_id, which the fillers
        // are ordered to match, so a row read at the wrong ordinal is a failure rather than a shuffle
        // nobody notices.
        List<(Guid FactorId, byte[] ContentKey, byte[] IndexKey)> rows = await ReadRowsAsync(
            admin, credentialId);

        // The count first: with credential_id as the key this is one, and the nine codes that never got
        // a row are the nine that open nothing. A person holding such a set redeems any code, is handed
        // a session, and nine times out of ten still cannot unlock the account — a failure the server
        // sees no symptom of at any point, because it can open neither envelope and holds no value that
        // could.
        await Assert.That(rows.Count).IsEqualTo(RequiredCodeCount);

        // And the bytes, per row, rather than the count alone. Both envelope columns are the same width
        // carrying the same version byte, so a write that landed all ten rows while copying one code's
        // envelopes into the other nine would satisfy every constraint this table has and every count
        // above it — and the discovery that nine codes are the same code would happen in the browser, on
        // the day somebody needed the second one.
        for (int ordinal = 0; ordinal < RequiredCodeCount; ordinal++)
        {
            await Assert.That(rows[ordinal].FactorId).IsEqualTo(CodeFactorId(ordinal));
            await Assert.That(rows[ordinal].ContentKey)
                .IsEquivalentTo(RepositoryTestHost.WrappedKeyEnvelope(ContentKeyFiller(ordinal)));
            await Assert.That(rows[ordinal].IndexKey)
                .IsEquivalentTo(RepositoryTestHost.WrappedKeyEnvelope(IndexKeyFiller(ordinal)));
        }
    }

    /// <summary>
    /// The name PostgreSQL reports when two rows claim one factor identifier.
    /// </summary>
    /// <remarks>
    /// Spelled out here rather than read off <c>WrappedAccountKeysConfiguration</c>, for the reason
    /// <c>CredentialTypeSpellingTests</c> spells out its own column value: a test taking its expectation
    /// from the thing under test agrees with whatever that thing later decides, and what this assertion
    /// is worth depends entirely on the name being the one a repository filters a <c>23505</c> on. The
    /// configuration pins the same string as a public constant for that repository's benefit; this is
    /// the executable form of the sentence "and it is still called that".
    /// </remarks>
    private const string PrimaryKeyName = "PK_wrapped_account_keys";

    /// <summary>The token <c>credentials.type</c> holds for a passkey.</summary>
    private const string PasskeyColumnValue = "passkey";

    /// <summary>The token <c>credentials.type</c> holds for a set of recovery codes.</summary>
    private const string RecoveryCodesColumnValue = "recovery_codes";

    /// <summary>How many codes an issued set holds, as the generation route requires.</summary>
    private const int RequiredCodeCount = 10;

    /// <summary>
    /// The factor identifier both rows of the refusal claim. Fixed rather than minted so a failure
    /// message names a value that can be found in this file.
    /// </summary>
    private static readonly Guid SharedFactorId = new("0199f3a1-0000-7000-8000-0000000000d4");

    /// <summary>
    /// The fillers the refused probe's envelopes carry. Neither is a filler the seeding helper writes
    /// and neither is one a code below writes, so a row read back carrying either would name the probe.
    /// </summary>
    private const byte ProbeContentKeyFiller = 0x7C;

    private const byte ProbeIndexKeyFiller = 0x8D;

    /// <summary>
    /// Fixed UTC instant for the probe rows. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The client-minted factor identifier of the code at <paramref name="ordinal" /> in a set. Fixed
    /// and ascending with the ordinal, so ordering the read-back by <c>factor_id</c> puts each row back
    /// beside the envelopes it was written with.
    /// </summary>
    private static Guid CodeFactorId(int ordinal) =>
        new($"0199f3a1-0000-7000-8000-00000000e0{ordinal:D2}");

    /// <summary>
    /// The filler of the content-key envelope written for the code at <paramref name="ordinal" />, and
    /// its index-key counterpart. Distinct per ordinal and distinct between the two columns, which is
    /// what lets a read-back tell one code's envelopes from another's and from each other — nothing else
    /// on these rows can, since every check constraint reads the same on all of them.
    /// </summary>
    private static byte ContentKeyFiller(int ordinal) => (byte)(0x40 + ordinal);

    private static byte IndexKeyFiller(int ordinal) => (byte)(0x80 + ordinal);

    /// <summary>
    /// Builds a WebAuthn credential handle of 32 bytes, every one of them <paramref name="fill" />. The
    /// fill byte is required rather than defaulted, for the reason
    /// <c>RlsIsolationTests.PasskeyHandle</c> requires its own: the column is unique, so two
    /// registrations of "a passkey" would collide on that index and the seeding would fail before the
    /// probe ran.
    /// </summary>
    private static byte[] Handle(byte fill) => [.. Enumerable.Repeat(fill, 32)];

    /// <summary>
    /// Builds one well-formed <c>wrapped_account_keys</c> INSERT. Every column is named, and both
    /// envelopes are built by <see cref="RepositoryTestHost.WrappedKeyEnvelope" /> at the one legal width
    /// and version, so no length or version check is ever what answers a probe here.
    /// </summary>
    private static NpgsqlCommand BuildWrappedKeysInsert(
        NpgsqlConnection connection,
        Guid credentialId,
        Guid userId,
        Guid factorId,
        string credentialType,
        byte contentKeyFiller,
        byte indexKeyFiller)
    {
        NpgsqlCommand insert = new(
            "insert into wrapped_account_keys " +
            "(credential_id, user_id, factor_id, credential_type, " +
            "wrapped_content_key, wrapped_index_key, created_at_utc) " +
            "values (@credential_id, @user_id, @factor_id, @credential_type, " +
            "@wrapped_content_key, @wrapped_index_key, @created_at_utc)",
            connection);
        insert.Parameters.AddWithValue("credential_id", credentialId);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("factor_id", factorId);
        insert.Parameters.AddWithValue("credential_type", credentialType);
        insert.Parameters.AddWithValue(
            "wrapped_content_key",
            RepositoryTestHost.WrappedKeyEnvelope(contentKeyFiller));
        insert.Parameters.AddWithValue(
            "wrapped_index_key",
            RepositoryTestHost.WrappedKeyEnvelope(indexKeyFiller));
        insert.Parameters.AddWithValue("created_at_utc", SeedInstant);

        return insert;
    }

    /// <summary>
    /// Writes a recovery-codes credential onto an existing account, on the superuser connection, and
    /// returns its id. One credential stands for the whole issued set, and an account holds
    /// <b>at most one</b>: <c>IX_credentials_user_id_recovery_codes</c> is a partial unique index over
    /// <c>user_id</c> filtered to this type, so a second call for the same account raises <c>23505</c>.
    /// </summary>
    /// <remarks>
    /// Raw SQL although <see cref="Credential.CreateRecoveryCodes" /> exists, for the reason the class
    /// remarks give for every probe here: these tests measure the schema, so seeding through the domain
    /// would put a write path under test inside the arrangement.
    /// <c>(recovery_codes, null, null)</c> is the shape <c>CK_credentials_type_shape</c> permits, and
    /// the <c>(provider, subject)</c> index names only federated rows, so the two NULLs never collide
    /// there.
    /// </remarks>
    private static async Task<Guid> InsertRecoveryCodesCredentialAsync(
        NpgsqlConnection connection,
        Guid userId)
    {
        Guid credentialId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            "insert into credentials (id, user_id, type, provider, subject, created_at_utc) " +
            "values (@id, @user_id, 'recovery_codes', null, null, @created_at_utc)",
            connection);
        command.Parameters.AddWithValue("id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await command.ExecuteNonQueryAsync();

        return credentialId;
    }

    /// <summary>
    /// Reads every <c>wrapped_account_keys</c> row filed against <paramref name="credentialId" />,
    /// ordered by factor identifier. Both envelopes come back as bytes rather than as a count, because a
    /// replaced or duplicated envelope is the one defect nothing else in this system could notice.
    /// </summary>
    private static async Task<List<(Guid FactorId, byte[] ContentKey, byte[] IndexKey)>> ReadRowsAsync(
        NpgsqlConnection connection,
        Guid credentialId)
    {
        await using NpgsqlCommand read = new(
            "select factor_id, wrapped_content_key, wrapped_index_key from wrapped_account_keys " +
            "where credential_id = @credential_id order by factor_id",
            connection);
        read.Parameters.AddWithValue("credential_id", credentialId);

        List<(Guid FactorId, byte[] ContentKey, byte[] IndexKey)> rows = [];
        await using NpgsqlDataReader reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetFieldValue<Guid>(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetFieldValue<byte[]>(2)));
        }

        return rows;
    }

    /// <summary>
    /// Runs a statement that must be refused and returns the refusal, throwing if it went through —
    /// the shape <see cref="PasskeySchemaTests" /> uses, because a probe that succeeds is a defect in
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
