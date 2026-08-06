using Domain.Users;
using Infrastructure.Persistence.Configurations;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the rules the three passkey tables hold on their own: that a public key and a signature
/// counter can never disagree with the credential they hang off about whose it is or what kind it is,
/// that one authenticator credential resolves to exactly one account, that an algorithm the product
/// cannot verify and a counter outside the protocol's range are both unstorable, that a nonce is
/// exactly 32 bytes of a named ceremony and is live for a positive interval, and that all of this key
/// material leaves with the credential and with the account.
/// </summary>
/// <remarks>
/// <para>
/// Every negative is raw Npgsql on the container superuser connection, because
/// <see cref="PasskeyPublicKey.Register" /> and <see cref="PasskeySignatureCounter.Start" /> already
/// refuse most of these one layer up — an EF-based write would measure the domain rather than the
/// schema, which is exactly the layer this file exists to be independent of. The superuser connection
/// also settles the other half: <c>passkey_signature_counters</c> is policed by
/// <c>user_isolation</c>, so on an application connection a probe row could be refused by the policy
/// instead of by the constraint, and the SQLSTATE being read would be the wrong one.
/// </para>
/// <para>
/// Each refusal asserts the constraint or index name and not the SQLSTATE alone, so one defect
/// reports one name. That is only deterministic because every probe row here is arranged to breach
/// exactly one rule: the credential-type check and the composite foreign key overlap almost
/// completely, and a row that gets both wrong reports whichever PostgreSQL evaluated first.
/// </para>
/// </remarks>
public sealed class PasskeySchemaTests
{
    [Test]
    public async Task Database_RefusesAPublicKeyAgainstAFederatedCredential()
    {
        // Arrange — an account holding only the federated Google credential SeedUserAsync gives it.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid federatedCredentialId = await ReadFederatedCredentialIdAsync(admin, userId);

        // Act — the row claims 'passkey', which is what keeps the attribution deterministic:
        // CK_passkey_public_keys_credential_type refuses any other spelling on the column, so a row
        // saying 'federated' would breach the check as well and leave which name PostgreSQL reports
        // an accident of evaluation order. Saying 'passkey' satisfies the check and leaves the
        // disagreement with the credential as the row's only defect.
        await using NpgsqlCommand insert = BuildPublicKeyInsert(
            admin, federatedCredentialId, userId, "passkey", Handle(0x11), ValidCoseKey, Es256);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — this is the schema half of "only a passkey opens a budget-reading session".
        // CK_sessions_kind_matches_credential says a full session needs a passkey credential, and a
        // passkey credential is only usable because key material is filed against it; if a public key
        // could be filed against a federated credential, an assertion could be verified against key
        // material an identity provider's sign-in had quietly acquired, and the pairing the sessions
        // check enforces would be satisfiable without an authenticator ever signing anything.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(PublicKeyCredentialForeignKeyName);
        await Assert.That(await CountRowsAsync(admin, "passkey_public_keys", "user_id", userId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesAPublicKeyWhoseCredentialBelongsToAnotherUser()
    {
        // Arrange — two accounts, the first holding a bare passkey credential with no key filed
        // against it yet, so the primary key is free and the only thing wrong with the row below is
        // that the two ids name different people.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userA = await host.SeedUserAsync("google-1", "a@example.com");
        Guid userB = await host.SeedUserAsync("google-2", "b@example.com");
        Guid credentialOfA = await InsertPasskeyCredentialAsync(admin, userA);

        // Act
        await using NpgsqlCommand insert = BuildPublicKeyInsert(
            admin, credentialOfA, userB, "passkey", Handle(0x22), ValidCoseKey, Es256);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — the composite foreign key is what refuses this, and it is the only thing that
        // could. Nothing on this table's own row cross-checks the two ids, and nothing downstream
        // would either: the counter's user_isolation policy decides tenancy on user_id alone and
        // never looks at the credential, so a stored disagreement would be key material one person
        // registered sitting under another person's name.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(PublicKeyCredentialForeignKeyName);
        await Assert.That(await CountRowsAsync(admin, "passkey_public_keys", "user_id", userB))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesASecondRegistrationOfTheSameWebAuthnCredentialId()
    {
        // Arrange — one account holding a fully registered passkey, and a second account holding a
        // bare passkey credential. Two accounts on purpose: the harm this rule prevents is one
        // authenticator credential resolving to two people, so the probe has to come from somebody
        // else. The two credential ids differ, so the primary key is not what refuses the row.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userA = await host.SeedUserAsync("google-1", "a@example.com");
        Guid userB = await host.SeedUserAsync("google-2", "b@example.com");
        byte[] handle = Handle(0x33);
        await host.SeedPasskeyAsync(userA, handle);
        Guid credentialOfB = await InsertPasskeyCredentialAsync(admin, userB);

        // Act — the same handle, byte for byte, filed against the second account's credential.
        await using NpgsqlCommand insert = BuildPublicKeyInsert(
            admin, credentialOfB, userB, "passkey", handle, ValidCoseKey, Es256);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — this is what stops one authenticator credential resolving to two accounts. An
        // assertion arrives naming only a WebAuthn credential id, before anybody has said who they
        // are, so that handle is the whole of the discovery read; two rows carrying it would leave
        // that read with two accounts and nothing to choose between them. The index name rather than
        // the SQLSTATE alone, because this table carries a second unique key — the primary key on
        // credential_id — and 23505 does not say which one was hit.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(refusal.ConstraintName)
            .IsEqualTo(PasskeyPublicKeyConfiguration.WebAuthnCredentialIdIndexName);
        await Assert.That(await CountRowsAsync(admin, "passkey_public_keys", "user_id", userB))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesAnUnsupportedCoseAlgorithm()
    {
        // Arrange — a bare passkey credential, so the row below is refused on the algorithm rather
        // than on a primary key that is already taken.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await InsertPasskeyCredentialAsync(admin, userId);

        // Act — an IANA COSE identifier the product does not verify. Raw SQL is what reaches the
        // check at all: Register refuses anything Enum.IsDefined rejects, so no domain path can
        // produce this row.
        await using NpgsqlCommand insert = BuildPublicKeyInsert(
            admin, credentialId, userId, "passkey", Handle(0x44), ValidCoseKey, UnsupportedAlgorithm);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — the vocabulary is a dictionary the database owns. cose_algorithm is a plain
        // integer, so this CHECK is the only thing standing between the two algorithms the product
        // verifies and any number a writer felt like storing; a row holding a third one would move
        // the failure from registration, where a person can retry with another authenticator, to
        // sign-in, where they are locked out by a key nothing can verify.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName)
            .IsEqualTo(PasskeyPublicKeyConfiguration.CoseAlgorithmCheckName);
    }

    [Test]
    public async Task Database_RefusesAWebAuthnCredentialIdOutsideTheProtocolsBounds()
    {
        // Arrange — one bare passkey credential serves both halves: neither row lands, so neither
        // takes the primary key from the other.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await InsertPasskeyCredentialAsync(admin, userId);

        // Act — both directions, because the constraint is a range and refuses both ends. One byte
        // under the floor and one byte over the ceiling, so a bound written with the wrong comparison
        // — or off by one — is caught rather than stepped over.
        await using NpgsqlCommand tooShort = BuildPublicKeyInsert(
            admin,
            credentialId,
            userId,
            "passkey",
            Handle(0x55, PasskeyPublicKey.MinWebAuthnCredentialIdLength - 1),
            ValidCoseKey,
            Es256);
        PostgresException shortRefusal = await RefusalOfAsync(tooShort);

        await using NpgsqlCommand tooLong = BuildPublicKeyInsert(
            admin,
            credentialId,
            userId,
            "passkey",
            Handle(0x66, PasskeyPublicKey.MaxWebAuthnCredentialIdLength + 1),
            ValidCoseKey,
            Es256);
        PostgresException longRefusal = await RefusalOfAsync(tooLong);

        // Assert — the two ends refuse different things. Below the floor the handle could not have
        // come from a conforming authenticator, and a short enough one is a value somebody could
        // guess and have filed as a credential handle; above the ceiling it is not a WebAuthn
        // credential id at all, and the column is the unique key the whole discovery read runs on.
        await Assert.That(shortRefusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(shortRefusal.ConstraintName)
            .IsEqualTo(PasskeyPublicKeyConfiguration.WebAuthnCredentialIdLengthCheckName);
        await Assert.That(longRefusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(longRefusal.ConstraintName)
            .IsEqualTo(PasskeyPublicKeyConfiguration.WebAuthnCredentialIdLengthCheckName);
        await Assert.That(await CountRowsAsync(admin, "passkey_public_keys", "user_id", userId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesASignatureCounterAboveTheProtocolsCeiling()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await InsertPasskeyCredentialAsync(admin, userId);

        // Act — one past the top of the unsigned 32-bit range WebAuthn's signCount lives in. Raw SQL
        // is what reaches the check: the property is a uint, so no domain path can hand this over.
        await using NpgsqlCommand insert = BuildSignatureCounterInsert(
            admin, credentialId, userId, "passkey", (long)uint.MaxValue + 1);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — the column is bigint, which is deliberate and is exactly why this check has to
        // exist: bigint was chosen so the top half of the unsigned range does not wrap, and it
        // accepts everything above that range and everything below zero as a side effect. A counter
        // outside the range is one no authenticator can ever advance past, so it would freeze clone
        // detection for that passkey permanently.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName)
            .IsEqualTo(PasskeySignatureCounterConfiguration.ValueCheckName);
    }

    [Test]
    public async Task Database_RefusesAChallengeThatIsNot32Bytes()
    {
        // Arrange — a challenge belongs to a ceremony rather than to a person, so there is nothing to
        // seed beside it.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — one byte short. Exact rather than a floor is the rule the column holds, so the
        // shorter probe is the one that says the check is an equality: a "between 16 and 32" written
        // by mistake would accept this row.
        await using NpgsqlCommand insert = BuildChallengeInsert(
            admin, Handle(0x77, ChallengeLength - 1), "authentication", SeedInstant, ExpiryInstant);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — a short challenge is a guessable one, and guessing it is the whole attack the
        // nonce exists to prevent. The length is fixed rather than bounded because one line of this
        // system generates every challenge, so any other length is a bug in that line rather than a
        // caller's choice.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(ChallengeLengthCheckName);
        await Assert.That(await CountChallengesAsync(admin)).IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesAChallengeCeremonyOutsideTheVocabulary()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — the length and the lifetime are both correct, so the ceremony is the row's only
        // defect and the name PostgreSQL reports is not an accident of evaluation order.
        await using NpgsqlCommand insert = BuildChallengeInsert(
            admin, Handle(0x88), "recovery", SeedInstant, ExpiryInstant);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — ceremony is a varchar rather than a native enum, which makes this CHECK the only
        // thing standing between the vocabulary and any string a writer felt like storing. The column
        // is what binds a nonce to the ceremony that issued it, so a value outside the vocabulary is
        // a challenge that matches neither leg and can therefore be spent by whichever one asks.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(ChallengeCeremonyCheckName);
    }

    [Test]
    public async Task Database_RefusesAChallengeThatExpiresBeforeItWasCreated()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — an expiry equal to the creation instant, which is the boundary the constraint draws:
        // a row that was never live for an instant was never a challenge. Equal rather than earlier,
        // so a check written with >= instead of > is caught rather than stepped over.
        await using NpgsqlCommand insert = BuildChallengeInsert(
            admin, Handle(0x99), "registration", SeedInstant, SeedInstant);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — the expiry is the only thing limiting how long a nonce can be replayed, so a row
        // that reversed the two instants would be one the sweep deletes but the verifier never
        // accepts, or the other way round the moment either comparison is written differently.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(ChallengeLifetimeCheckName);
    }

    [Test]
    public async Task Database_RemovesAPasskeysKeyAndCounterWithItsCredential()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, Handle(0xAA));

        // Both rows are there before the delete, and that is a precondition rather than a
        // convenience: every assertion below is a count of zero, which a seeding call that quietly
        // wrote nothing satisfies just as well as a cascade that fired.
        await Assert.That(
                await CountRowsAsync(admin, "passkey_public_keys", "credential_id", credentialId))
            .IsEqualTo(1L);
        await Assert.That(
                await CountRowsAsync(
                    admin, "passkey_signature_counters", "credential_id", credentialId))
            .IsEqualTo(1L);

        // Act — raw Npgsql on purpose: under a cascade EF would delete the dependents itself, so an
        // EF-based delete proves nothing about what the schema does.
        await ExecuteAsync(admin, "delete from credentials where id = @id", credentialId);

        // Assert — CASCADE rather than RESTRICT on both foreign keys, and that is the decision:
        // RESTRICT would let a row of key bookkeeping hold up the removal of a credential, and
        // through it an account erasure. The user survives, so this is the credential's cascade
        // rather than the one below.
        await Assert.That(
                await CountRowsAsync(admin, "passkey_public_keys", "credential_id", credentialId))
            .IsEqualTo(0L);
        await Assert.That(
                await CountRowsAsync(
                    admin, "passkey_signature_counters", "credential_id", credentialId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(admin, "users", "id", userId)).IsEqualTo(1L);
    }

    [Test]
    public async Task Database_RemovesAPasskeysKeyAndCounterWithItsUser()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await host.SeedPasskeyAsync(userId, Handle(0xBB));

        // Both rows are there before the delete, for the reason the credential cascade above states:
        // three counts of zero are satisfied by a seeding call that wrote nothing.
        await Assert.That(await CountRowsAsync(admin, "passkey_public_keys", "user_id", userId))
            .IsEqualTo(1L);
        await Assert.That(
                await CountRowsAsync(admin, "passkey_signature_counters", "user_id", userId))
            .IsEqualTo(1L);

        // Act
        await ExecuteAsync(admin, "delete from users where id = @id", userId);

        // Assert — there is deliberately no second foreign key from either table to users; the
        // transitive cascade is the whole mechanism, exactly as on sessions. users -> credentials
        // cascades and credentials -> both of these cascades, so the key material goes two hops
        // rather than one. This is what makes an erasure complete: a public key and a counter left
        // behind would be an authenticator handle and a device fingerprint outliving the account they
        // described.
        await Assert.That(await CountRowsAsync(admin, "passkey_public_keys", "user_id", userId))
            .IsEqualTo(0L);
        await Assert.That(
                await CountRowsAsync(admin, "passkey_signature_counters", "user_id", userId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(admin, "credentials", "user_id", userId))
            .IsEqualTo(0L);
    }

    /// <summary>
    /// The composite foreign key both passkey tables carry to <c>credentials</c>. Private to the
    /// configuration that declares it, so it is restated here rather than referenced — the point of a
    /// pinned name is that a schema test states it independently.
    /// </summary>
    private const string PublicKeyCredentialForeignKeyName = "FK_passkey_public_keys_credentials";

    /// <summary>
    /// The three checks <c>webauthn_challenges</c> carries. Restated here rather than referenced:
    /// <c>WebAuthnChallengeConfiguration</c> is internal — as is the row type it configures, which is
    /// the point of that visibility — so nothing outside <c>Infrastructure</c> can name its constants.
    /// The restatement is not a loss. A pinned name exists so that one defect reports one name, and a
    /// test that read the same constant the schema was rendered from would agree with itself no
    /// matter what either said.
    /// </summary>
    private const string ChallengeCeremonyCheckName = "CK_webauthn_challenges_ceremony";

    private const string ChallengeLifetimeCheckName = "CK_webauthn_challenges_lifetime";

    private const string ChallengeLengthCheckName = "CK_webauthn_challenges_length";

    /// <summary>
    /// The COSE identifier of ES256, the algorithm every valid row here carries. Read off the enum so
    /// a probe cannot drift from the vocabulary the check constraint is rendered from.
    /// </summary>
    private const int Es256 = (int)CoseAlgorithm.Es256;

    /// <summary>
    /// An IANA COSE identifier the product does not verify. EdDSA's assigned value, so it is a real
    /// algorithm outside the pair rather than a number nothing could ever mean — the rule is that the
    /// product verifies two, not that the column holds something numeric.
    /// </summary>
    private const int UnsupportedAlgorithm = -8;

    /// <summary>
    /// The exact length <c>CK_webauthn_challenges_length</c> demands. Restated here rather than
    /// referenced, because the configuration keeps it private and a schema test that read the same
    /// constant the schema was rendered from would agree with itself.
    /// </summary>
    private const int ChallengeLength = 32;

    /// <summary>
    /// A COSE key of a length the column accepts. Nothing here verifies a signature; the only rule
    /// this value has to satisfy is <c>CK_passkey_public_keys_public_key_length</c>, so that a probe
    /// aimed at another rule is never refused by this one.
    /// </summary>
    private static readonly byte[] ValidCoseKey = [0xA5, 0x01, 0x02, 0x03];

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The expiry every valid challenge here is given. It must be strictly after
    /// <see cref="SeedInstant" />, which is the whole content of
    /// <c>CK_webauthn_challenges_lifetime</c>.
    /// </summary>
    private static readonly DateTime ExpiryInstant = new(2026, 6, 12, 13, 19, 15, DateTimeKind.Utc);

    /// <summary>
    /// Builds a byte string of <paramref name="length" /> bytes, every one of them
    /// <paramref name="fill" />. The fill byte is what keeps two probes in one test from colliding on
    /// the unique index over <c>webauthn_credential_id</c>, so it is a required argument rather than
    /// a detail: two handles of the same length would otherwise be the same handle.
    /// </summary>
    private static byte[] Handle(byte fill, int length = 32) => [.. Enumerable.Repeat(fill, length)];

    /// <summary>
    /// Writes a passkey credential with <b>no key and no counter filed against it</b>, and returns
    /// its id. Raw SQL rather than <see cref="RepositoryTestHost.SeedPasskeyAsync" /> precisely
    /// because of that gap: <c>credential_id</c> is the primary key of both dependent tables, so a
    /// probe row aimed at either of them needs a credential whose slot is still free. The
    /// <c>(passkey, null, null)</c> shape is what <c>CK_credentials_type_shape</c> permits, and the
    /// partial unique index on <c>(provider, subject)</c> names only federated rows, so it does not
    /// collide with the account's Google credential.
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

    /// <summary>
    /// Reads back the id of the federated Google credential <see cref="RepositoryTestHost.SeedUserAsync" />
    /// gave an account.
    /// </summary>
    private static async Task<Guid> ReadFederatedCredentialIdAsync(
        NpgsqlConnection connection,
        Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select id from credentials where user_id = @id and type = 'federated'",
            connection);
        command.Parameters.AddWithValue("id", userId);
        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"Expected one federated credential id, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Builds the public key insert with every column spelled out, so a test can put a value in one
    /// of them that no domain path can produce.
    /// </summary>
    /// <remarks>
    /// <paramref name="credentialType" /> is a parameter rather than a literal because it is half of
    /// two rules at once: <c>CK_passkey_public_keys_credential_type</c> bounds what it may say on its
    /// own, and the composite foreign key compares it against <c>credentials.type</c>. A caller
    /// therefore has to state it, and a row breaching both is refused for a reason the caller chose
    /// rather than one it stumbled into.
    /// </remarks>
    private static NpgsqlCommand BuildPublicKeyInsert(
        NpgsqlConnection connection,
        Guid credentialId,
        Guid userId,
        string credentialType,
        byte[] webAuthnCredentialId,
        byte[] coseKey,
        int coseAlgorithm)
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
        command.Parameters.AddWithValue("public_key_cose", coseKey);
        command.Parameters.AddWithValue("cose_algorithm", coseAlgorithm);
        return command;
    }

    private static NpgsqlCommand BuildSignatureCounterInsert(
        NpgsqlConnection connection,
        Guid credentialId,
        Guid userId,
        string credentialType,
        long signatureCounter)
    {
        NpgsqlCommand command = new(
            """
            insert into passkey_signature_counters
                (credential_id, user_id, credential_type, signature_counter)
            values (@credential_id, @user_id, @credential_type, @signature_counter)
            """,
            connection);
        command.Parameters.AddWithValue("credential_id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("credential_type", credentialType);
        command.Parameters.AddWithValue("signature_counter", signatureCounter);
        return command;
    }

    private static NpgsqlCommand BuildChallengeInsert(
        NpgsqlConnection connection,
        byte[] challenge,
        string ceremony,
        DateTime createdAtUtc,
        DateTime expiresAtUtc)
    {
        NpgsqlCommand command = new(
            """
            insert into webauthn_challenges
                (id, challenge, ceremony, created_at_utc, expires_at_utc)
            values (@id, @challenge, @ceremony, @created_at_utc, @expires_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("challenge", challenge);
        command.Parameters.AddWithValue("ceremony", ceremony);
        command.Parameters.AddWithValue("created_at_utc", createdAtUtc);
        command.Parameters.AddWithValue("expires_at_utc", expiresAtUtc);
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
        return await ScalarCountAsync(command, table);
    }

    /// <summary>
    /// Counts the whole challenge table. A challenge is keyed on no owner at all — that is the point
    /// of the table — so there is no id to narrow on the way the other counts do.
    /// </summary>
    private static async Task<long> CountChallengesAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from webauthn_challenges",
            connection);
        return await ScalarCountAsync(command, "webauthn_challenges");
    }

    /// <summary>
    /// Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the query
    /// changed shape, and that should fail loudly here instead of at the assertion.
    /// </summary>
    private static async Task<long> ScalarCountAsync(NpgsqlCommand command, string table) =>
        await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{table}', got '{unexpected ?? "null"}'."),
        };

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
