using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers what <see cref="SessionRepository" />'s revocation sweep does against a real database:
/// that it reaches exactly the sessions one credential established, and that running it again
/// converges instead of restamping. Both are claims about the predicate and about where the
/// transition runs, and neither can be measured anywhere but here.
/// </summary>
/// <remarks>
/// The second credential is seeded with raw SQL because no domain factory mints a passkey yet;
/// <c>type = 'passkey'</c> with <c>provider</c> and <c>subject</c> both NULL is the shape
/// <c>CK_credentials_type_shape</c> permits, and <c>AppRoleGrantsTests</c> writes the same row the
/// same way. It is read back through EF so that the session it opens is produced by
/// <see cref="Session.Establish" />, exactly as production produces one.
/// </remarks>
public sealed class SessionRepositoryTests
{
    [Test]
    public async Task RevokeForCredentialAsync_RevokesOnlyThatCredentialsSessions()
    {
        // Arrange — one account, two ways into it, one live session each. One account is the whole
        // arrangement: two users would let a predicate keyed on the wrong column pass.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid passkeyId = await SeedPasskeyCredentialAsync(host, userId);
        await using BudgetoidDbContext db = CreateDb(host);
        Credential credentialA = await db.Credentials.SingleAsync(
            credential => credential.UserId == userId && credential.Id != passkeyId);
        Credential credentialB = await db.Credentials.SingleAsync(
            credential => credential.Id == passkeyId);
        var repository = new SessionRepository(db);
        Session sessionA = Session.Establish(credentialA, SeedInstant, ExpiryInstant);
        Session sessionB = Session.Establish(credentialB, SeedInstant, ExpiryInstant);
        await repository.AddAsync(sessionA);
        await repository.AddAsync(sessionB);

        // Act
        int revoked = await repository.RevokeForCredentialAsync(credentialA.Id, RevocationInstant);

        // Assert — the most likely wrong implementation narrows on user_id rather than
        // credential_id, and every other test in the suite passes under it, because every other
        // arrangement holds sessions for a single credential. Under that implementation withdrawing
        // one sign-in method signs the person out of the device in their hand. Read back on a fresh
        // context so the answer comes off the rows rather than off the tracked entities the sweep
        // just mutated.
        await Assert.That(revoked).IsEqualTo(1);
        await using BudgetoidDbContext verify = CreateDb(host);
        Session storedA = await verify.Sessions.SingleAsync(session => session.Id == sessionA.Id);
        Session storedB = await verify.Sessions.SingleAsync(session => session.Id == sessionB.Id);
        await Assert.That(storedA.RevokedAtUtc).IsEqualTo(RevocationInstant);
        await Assert.That(storedB.RevokedAtUtc).IsNull();

        // The other half of what a credential decides, asserted here because this is the only place
        // in the suite it can be. Session.KindFor maps passkey to Full and federated to Locked, and
        // every unit test covers the federated arm alone — there is no Credential.CreatePasskey, so
        // the unit level cannot build the credential that exercises this one, and returning Locked
        // unconditionally would leave the whole suite green. Session B was established from the
        // raw-seeded passkey above and read back through the real column, so the same two lines also
        // pin the 'full' <-> SessionKind.Full converter in both directions at no extra cost.
        await Assert.That(storedB.Kind).IsEqualTo(SessionKind.Full);
        await Assert.That(storedB.ReadsBudgetContent).IsTrue();
    }

    [Test]
    public async Task RevokeForCredentialAsync_RunTwice_KeepsTheFirstRevocationInstant()
    {
        // Arrange — the second call names a later instant, so a restamp would be visible rather than
        // hidden behind an identical value.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        Credential credential = await db.Credentials.SingleAsync(
            stored => stored.UserId == userId);
        var repository = new SessionRepository(db);
        Session session = Session.Establish(credential, SeedInstant, ExpiryInstant);
        await repository.AddAsync(session);

        // Act
        int first = await repository.RevokeForCredentialAsync(credential.Id, RevocationInstant);
        int second = await repository.RevokeForCredentialAsync(credential.Id, LaterRevocationInstant);

        // Assert — this is what makes a retried revocation honest about having ended nothing new: a
        // second non-zero count would tell whoever asked that they had cut off access they had
        // already cut off, and a restamp would move the record of a compromise forward to whenever
        // someone last pressed the button. It is also the property the ExecuteUpdate ban buys — a
        // set-based UPDATE would rewrite every matched row on every call and report the retry as a
        // second ending.
        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(0);
        await using BudgetoidDbContext verify = CreateDb(host);
        Session stored = await verify.Sessions.SingleAsync(row => row.Id == session.Id);
        await Assert.That(stored.RevokedAtUtc).IsEqualTo(RevocationInstant);
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Expiry of every session established here. Strictly after <see cref="SeedInstant" />, which is
    /// the whole content of <c>CK_sessions_lifetime</c>.
    /// </summary>
    private static readonly DateTime ExpiryInstant = new(2026, 6, 13, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>The instant the first sweep ends access at.</summary>
    private static readonly DateTime RevocationInstant =
        new(2026, 6, 12, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The instant a retried sweep names. Distinct from <see cref="RevocationInstant" /> so that a
    /// restamp is a failing assertion rather than an invisible rewrite of the same value.
    /// </summary>
    private static readonly DateTime LaterRevocationInstant =
        new(2026, 6, 12, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Writes a second credential onto an existing account and returns its id. Raw SQL on the
    /// superuser connection because no domain factory mints a passkey yet; the partial unique index
    /// on <c>(provider, subject)</c> names only federated rows, so <c>(NULL, NULL)</c> does not
    /// collide with the account's Google credential.
    /// </summary>
    private static async Task<Guid> SeedPasskeyCredentialAsync(
        RepositoryTestHost host,
        Guid userId)
    {
        Guid credentialId = Guid.CreateVersion7();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
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
    /// Builds a context with no ambient budget, which is safe here because neither <c>Session</c>
    /// nor <c>Credential</c> carries a budget query filter. The container superuser connection, so
    /// what these tests measure is the repository's predicate rather than the isolation policy —
    /// <c>RlsIsolationTests</c> owns the policy.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
