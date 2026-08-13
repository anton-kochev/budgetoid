using Domain.Users;
using Infrastructure.Persistence.Configurations;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the rule <c>wrapped_account_keys</c> holds on its own, which neither the grant matrix nor
/// row-level security can express: a factor identifier names <b>one</b> pair of envelopes, table-wide.
/// </summary>
/// <remarks>
/// <para>
/// Its own file rather than a test in <c>AppRoleGrantsTests</c> or <c>RlsIsolationTests</c>, because it
/// is neither of those things — no privilege and no policy is involved, and both of those files open
/// with a statement of what they measure that this test would falsify. Not in
/// <c>PasskeySchemaTests</c> either: that file scopes itself to "the three passkey tables", and this
/// one holds the keys of whichever factors have a key-encryption key — a set of recovery codes as
/// readily as a passkey.
/// </para>
/// <para>
/// The probe is raw Npgsql on the container <b>superuser</b> connection, following the reasoning
/// <see cref="PasskeySchemaTests" /> records for its own probes and for the same two reasons.
/// <see cref="WrappedAccountKeys.For" /> refuses most malformed rows one layer up, so an EF write would
/// measure the domain rather than the schema; and this table is policed by <c>user_isolation</c>, so on
/// an application connection a probe row could be refused by the policy instead of by the index, and
/// the SQLSTATE being read would be the wrong one.
/// </para>
/// <para>
/// The refusal asserts the index name and not the SQLSTATE alone. This table carries a second unique
/// key — the primary key on <c>credential_id</c> — and <c>23505</c> does not say which one was hit, so
/// the probe is arranged to breach exactly one: a credential of its own, and a factor identifier
/// already taken.
/// </para>
/// </remarks>
public sealed class WrappedAccountKeysSchemaTests
{
    [Test]
    public async Task Database_RefusesASecondFactorIdentifier()
    {
        // Arrange — one account, two registered passkeys, and the account's two keys already filed
        // against the first. One account rather than two, and that is the strictest form of the
        // question rather than a convenience: IX_wrapped_account_keys_factor_id is unique across the
        // WHOLE TABLE rather than per owner, so if the duplicate is refused even here — same user_id,
        // same credential_type, nothing to separate the two rows but the credential — then no scoping
        // is quietly doing the work. A cross-account collision surfaces as this same 23505, from this
        // same index, which is exactly what the global index is for: scoping it per account would make
        // that duplicate storable and leave the associated data ambiguous precisely where it is
        // trusted. The two handles differ, or IX_passkey_public_keys_webauthn_credential_id would
        // refuse the second registration.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid registeredCredentialId = await host.SeedPasskeyAsync(userId, Handle(0x41));
        Guid secondCredentialId = await host.SeedPasskeyAsync(userId, Handle(0x52));
        Guid factorId = await host.SeedWrappedAccountKeysAsync(registeredCredentialId, SharedFactorId);

        // Act — the same factor identifier, filed against the account's other credential. The
        // credential_id differs, so the primary key is not what refuses the row; the envelopes are
        // well-formed at the one legal width and version, so none of the four length and version checks
        // is either; and 'passkey' agrees with the credential's own type, so the composite foreign key
        // is satisfied. The factor identifier is the row's only defect.
        await using NpgsqlCommand insert = new(
            "insert into wrapped_account_keys " +
            "(credential_id, user_id, factor_id, credential_type, " +
            "wrapped_content_key, wrapped_index_key, created_at_utc) " +
            "values (@credential_id, @user_id, @factor_id, 'passkey', " +
            "@wrapped_content_key, @wrapped_index_key, @created_at_utc)",
            admin);
        insert.Parameters.AddWithValue("credential_id", secondCredentialId);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("factor_id", factorId);
        insert.Parameters.AddWithValue(
            "wrapped_content_key",
            RepositoryTestHost.WrappedKeyEnvelope(0x7C));
        insert.Parameters.AddWithValue(
            "wrapped_index_key",
            RepositoryTestHost.WrappedKeyEnvelope(0x8D));
        insert.Parameters.AddWithValue("created_at_utc", SeedInstant);
        PostgresException refusal = await RefusalOfAsync(insert);

        // Assert — the factor identifier is minted by the CLIENT, which is what makes uniqueness a rule
        // here rather than a lookup that happens to hold: nothing else in the system stops two rows
        // carrying the same one. It is also the associated data every envelope involved was sealed
        // with, so a shared factor id would let one factor's keys be opened against another's — and the
        // second registration is the only place anybody would ever learn of the collision, which is why
        // it has to be the thing that fails.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(refusal.ConstraintName)
            .IsEqualTo(WrappedAccountKeysConfiguration.FactorIdIndexName);

        // And nothing landed. The SQLSTATE says the statement was rejected; only this says the factor
        // still names exactly one pair of envelopes, which is the whole of the rule.
        await using NpgsqlCommand count = new(
            "select count(*) from wrapped_account_keys where factor_id = @factor_id", admin);
        count.Parameters.AddWithValue("factor_id", factorId);
        await Assert.That(await count.ExecuteScalarAsync()).IsEqualTo(1L);
    }

    /// <summary>
    /// The factor identifier both rows claim. Fixed rather than minted so a failure message names a
    /// value that can be found in this file.
    /// </summary>
    private static readonly Guid SharedFactorId = new("0199f3a1-0000-7000-8000-0000000000d4");

    /// <summary>
    /// Fixed UTC instant for the probe row. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Builds a WebAuthn credential handle of 32 bytes, every one of them <paramref name="fill" />. The
    /// fill byte is required rather than defaulted, for the reason
    /// <c>RlsIsolationTests.PasskeyHandle</c> requires its own: the column is unique, so two
    /// registrations of "a passkey" would collide on that index and the seeding would fail before the
    /// probe ran.
    /// </summary>
    private static byte[] Handle(byte fill) => [.. Enumerable.Repeat(fill, 32)];

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
