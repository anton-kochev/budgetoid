using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the one claim <c>CK_credentials_type_shape</c> stopped making. Its <c>recovery_codes</c>
/// arm carries a predicate byte-identical to its <c>passkey</c> arm — <c>provider is null and subject
/// is null</c> — so that constraint cannot tell those two types apart at all. Both are self-contained
/// credentials with no issuer and no issuer-assigned identifier, and there is nothing on a
/// <c>credentials</c> row left for a CHECK to look at.
/// </summary>
/// <remarks>
/// <para>
/// <c>CredentialConfiguration</c> states the consequence and says what carries the difference
/// instead: each child table pins its own <c>credential_type</c> copy with a CHECK of its own, and
/// ties that copy back to <c>credentials.type</c> through a composite foreign key over
/// <c>(credential_id, user_id, credential_type)</c> targeting the
/// <c>AK_credentials_id_user_id_type</c> alternate key. <b>Nothing asserted it.</b> These two tests
/// are the executable form of that comment, and they are owed from both directions, because one
/// alone leaves the other believed rather than checked.
/// </para>
/// <para>
/// What they refuse is a specific pair of harms. A redeemable code filed against a passkey credential
/// is a second, silent way into an account whose owner registered an authenticator and was never
/// issued a set of codes — and because the codes would hang off the passkey, revoking that passkey
/// would take them with it, so nobody would ever see them listed to revoke. Key material filed
/// against a set of recovery codes is the same door from the other side: an assertion would verify
/// against a public key hanging off a credential no authenticator ever registered.
/// </para>
/// <para>
/// Every row here is written by raw Npgsql on the container superuser connection. Neither table needs
/// an application-role grant to make the point, and using one would weaken it: an app-role statement
/// can be refused on privilege or by a policy instead of by the constraint, which is a different
/// measurement — the surrounding schema tests seed what they cannot mint the same way, for the same
/// reason.
/// </para>
/// <para>
/// Each probe row is arranged to breach exactly one rule, so the name PostgreSQL reports is
/// deterministic rather than an artifact of evaluation order: the <c>credential_type</c> column
/// always carries the one spelling its own table's CHECK permits, which leaves the disagreement with
/// the credential as the row's only defect. And each test writes the permitted row <b>first</b>, as a
/// control: without it the refusal that follows is satisfied just as well by a malformed statement, a
/// renamed column or a table that is not there, every one of which would make the test green while
/// proving nothing about which credential the row was allowed to hang off.
/// </para>
/// </remarks>
public sealed class CredentialTypeDiscriminationTests
{
    [Test]
    public async Task Database_RefusesARecoveryCodeHashAgainstAPasskeyCredential()
    {
        // Arrange — one account holding both self-contained credential types. Under
        // CK_credentials_type_shape these two rows are indistinguishable: each is
        // (provider null, subject null), and only the type column separates them.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid recoveryCodesId = await InsertCredentialAsync(admin, userId, RecoveryCodesType);
        Guid passkeyId = await InsertCredentialAsync(admin, userId, PasskeyType);

        // The control, and it runs first on purpose (see the class remarks): the identical statement
        // against the credential that does stand for a set of codes has to land.
        await using NpgsqlCommand permitted = BuildRecoveryCodeHashInsert(
            admin, VerifierHash(0x11), recoveryCodesId, userId, RecoveryCodesType);
        await Assert.That(await permitted.ExecuteNonQueryAsync()).IsEqualTo(1);

        // Act — the same row against the passkey credential, under a different hash so the primary
        // key is free. credential_type stays 'recovery_codes', which is what keeps the attribution
        // deterministic: CK_recovery_code_hashes_credential_type refuses every other spelling, so a
        // row saying 'passkey' would breach that check as well and leave which name PostgreSQL
        // reports an accident.
        await using NpgsqlCommand refused = BuildRecoveryCodeHashInsert(
            admin, VerifierHash(0x22), passkeyId, userId, RecoveryCodesType);
        PostgresException refusal = await RefusalOfAsync(refused);

        // Assert — the composite foreign key is what refuses this, and since the shape check gave up
        // discriminating the two types it is the only thing that could. The check on this table
        // cannot: the row says 'recovery_codes' and is telling the truth about itself; what it is
        // wrong about is the credential it names, which no single-row CHECK can see. Shorten that
        // foreign key to credential_id alone and this row becomes storable with nothing to stop it.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(RecoveryCodeHashForeignKeyName);
        await Assert.That(
                await CountRowsAsync(admin, "recovery_code_hashes", "credential_id", passkeyId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesAPasskeyPublicKeyAgainstARecoveryCodesCredential()
    {
        // Arrange — the same two credentials, because the claim is symmetric and only a mirrored
        // arrangement can say so.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid recoveryCodesId = await InsertCredentialAsync(admin, userId, RecoveryCodesType);
        Guid passkeyId = await InsertCredentialAsync(admin, userId, PasskeyType);

        // The control, for the reason its twin above gives.
        await using NpgsqlCommand permitted = BuildPublicKeyInsert(
            admin, passkeyId, userId, PasskeyType, Handle(0x33));
        await Assert.That(await permitted.ExecuteNonQueryAsync()).IsEqualTo(1);

        // Act — the same key material against the set of recovery codes, under a different WebAuthn
        // handle so the unique index over that column is not what refuses the row, and against a
        // different credential so the primary key on credential_id is free. credential_type stays
        // 'passkey' for the attribution reason above: CK_passkey_public_keys_credential_type refuses
        // every other spelling.
        await using NpgsqlCommand refused = BuildPublicKeyInsert(
            admin, recoveryCodesId, userId, PasskeyType, Handle(0x44));
        PostgresException refusal = await RefusalOfAsync(refused);

        // Assert — the other direction of the same rule, and it is owed separately rather than
        // inferred from its twin: the two tables carry their own foreign key each, so one holding is
        // no evidence about the other. PasskeySchemaTests already refuses a public key against a
        // FEDERATED credential; that refusal is a different one and does not cover this, because
        // CK_credentials_type_shape still discriminates federated from everything else — a federated
        // row must carry a provider and a subject. Recovery codes are the type the shape check went
        // blind to, so this is the direction whose only remaining guard is the foreign key.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(PublicKeyForeignKeyName);
        await Assert.That(
                await CountRowsAsync(admin, "passkey_public_keys", "credential_id", recoveryCodesId))
            .IsEqualTo(0L);
    }

    /// <summary>
    /// The two composite foreign keys this file exists to hold to their reason. Restated here rather
    /// than referenced: <c>RecoveryCodeHashConfiguration</c> keeps its name private, and a pinned name
    /// exists so that one defect reports one name — a test reading the same constant the schema was
    /// rendered from would agree with itself no matter what either said.
    /// </summary>
    private const string RecoveryCodeHashForeignKeyName = "FK_recovery_code_hashes_credentials";

    private const string PublicKeyForeignKeyName = "FK_passkey_public_keys_credentials";

    /// <summary>
    /// The two <c>credentials.type</c> spellings the shape check can no longer tell apart, as the
    /// column stores them. Literals rather than the enum, because these tests are about what the
    /// schema does with the string on the row.
    /// </summary>
    private const string PasskeyType = "passkey";

    private const string RecoveryCodesType = "recovery_codes";

    /// <summary>
    /// The exact length <c>CK_recovery_code_hashes_verifier_hash_length</c> demands: SHA-256, so 32
    /// bytes. A probe aimed at the foreign key must never be refused by that check instead.
    /// </summary>
    private const int VerifierHashLength = 32;

    /// <summary>
    /// A COSE key of a length <c>CK_passkey_public_keys_public_key_length</c> accepts, and ES256's
    /// IANA identifier. Nothing here verifies a signature; these values exist only so that a probe
    /// aimed at the foreign key is never refused by one of the column checks instead.
    /// </summary>
    private static readonly byte[] ValidCoseKey = [0xA5, 0x01, 0x02, 0x03];

    private const int Es256 = -7;

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// A verifier hash of the one length the column accepts, filled with <paramref name="fill" />.
    /// The fill byte is what keeps the control row and the probe row in one test from colliding on
    /// the primary key, so it is a required argument rather than a detail.
    /// </summary>
    private static byte[] VerifierHash(byte fill) =>
        [.. Enumerable.Repeat(fill, VerifierHashLength)];

    /// <summary>
    /// A WebAuthn credential handle inside the bounds the column accepts, filled with
    /// <paramref name="fill" /> for the reason <see cref="VerifierHash" /> takes one — here it is the
    /// unique index over <c>webauthn_credential_id</c> rather than a primary key.
    /// </summary>
    private static byte[] Handle(byte fill) => [.. Enumerable.Repeat(fill, 32)];

    /// <summary>
    /// Writes a bare credential of <paramref name="type" /> onto an existing account and returns its
    /// id. Raw SQL, and bare on purpose: both dependent tables key on <c>credential_id</c>, so a
    /// probe aimed at either needs a credential whose slot is still free. <c>(null, null)</c> is the
    /// shape <c>CK_credentials_type_shape</c> permits for both spellings this file passes in — which
    /// is precisely the collapse these tests exist to compensate for — and the partial unique index
    /// on <c>(provider, subject)</c> names only federated rows, so neither collides with the
    /// account's Google credential.
    /// </summary>
    private static async Task<Guid> InsertCredentialAsync(
        NpgsqlConnection connection,
        Guid userId,
        string type)
    {
        Guid credentialId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            """
            insert into credentials (id, user_id, type, provider, subject, created_at_utc)
            values (@id, @user_id, @type, null, null, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return await command.ExecuteNonQueryAsync() switch
        {
            1 => credentialId,
            var rows => throw new InvalidOperationException($"Inserted {rows} credentials, wanted 1."),
        };
    }

    /// <summary>
    /// Builds the recovery-code-hash insert with every column spelled out, so a test can put a value
    /// in one of them that no domain path can produce.
    /// </summary>
    /// <remarks>
    /// <paramref name="credentialType" /> is a parameter rather than a literal because it is half of
    /// two rules at once: <c>CK_recovery_code_hashes_credential_type</c> bounds what it may say on
    /// its own, and the composite foreign key compares it against <c>credentials.type</c>. A caller
    /// therefore has to state it, and a row breaching both is refused for a reason the caller chose
    /// rather than one it stumbled into.
    /// </remarks>
    private static NpgsqlCommand BuildRecoveryCodeHashInsert(
        NpgsqlConnection connection,
        byte[] verifierHash,
        Guid credentialId,
        Guid userId,
        string credentialType)
    {
        NpgsqlCommand command = new(
            """
            insert into recovery_code_hashes
                (verifier_hash, credential_id, user_id, credential_type, created_at_utc)
            values (@verifier_hash, @credential_id, @user_id, @credential_type, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("verifier_hash", verifierHash);
        command.Parameters.AddWithValue("credential_id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("credential_type", credentialType);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return command;
    }

    /// <summary>
    /// Builds the public-key insert, with <paramref name="credentialType" /> a parameter for the
    /// reason <see cref="BuildRecoveryCodeHashInsert" /> gives.
    /// </summary>
    private static NpgsqlCommand BuildPublicKeyInsert(
        NpgsqlConnection connection,
        Guid credentialId,
        Guid userId,
        string credentialType,
        byte[] webAuthnCredentialId)
    {
        NpgsqlCommand command = new(
            """
            insert into passkey_public_keys
                (credential_id, user_id, credential_type,
                 webauthn_credential_id, public_key_cose, cose_algorithm)
            values (@credential_id, @user_id, @credential_type,
                    @webauthn_credential_id, @public_key_cose, @cose_algorithm)
            """,
            connection);
        command.Parameters.AddWithValue("credential_id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("credential_type", credentialType);
        command.Parameters.AddWithValue("webauthn_credential_id", webAuthnCredentialId);
        command.Parameters.AddWithValue("public_key_cose", ValidCoseKey);
        command.Parameters.AddWithValue("cose_algorithm", Es256);
        return command;
    }

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
