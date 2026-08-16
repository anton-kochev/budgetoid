using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers what <see cref="SessionRepository" /> does against a real database: that the credential
/// sweep reaches exactly the sessions one credential established, that the single-session revocation
/// reaches exactly the one it names, and that running either again converges instead of restamping.
/// Every one of those is a claim about a predicate and about where the transition runs, and none can
/// be measured anywhere but here.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SessionTokenRepository" /> is exercised here too, and it belongs beside these rather than
/// in a file of its own: it has one member, that member's argument is produced by
/// <see cref="SessionToken.HashOf" />, and the row it reads is written in the same
/// <c>SaveChangesAsync</c> as the session it names — so an arrangement for it is an arrangement for
/// these. What that test pins is the value-converter comparison the lookup translates to, which is the
/// one failure on that path with no symptom: every session in the system stops being found and every
/// request arrives unauthenticated. The exemption that lookup rests on is pinned separately, by
/// <c>RlsIsolationTests.Database_ReadsASessionTokenWithNoUserOnTheSession</c>, because it is a claim
/// about the database rather than about the repository.
/// </para>
/// </remarks>
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

    [Test]
    public async Task RevokeAsync_EndsOnlyTheNamedSession()
    {
        // Arrange — one account, ONE credential, two live sessions on it. One credential rather than
        // two is the stronger arrangement: it makes this the control for a predicate keyed on user_id
        // AND for one keyed on credential_id at the same time, and both of those are implementations
        // every other test in this file passes under.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        Credential credential = await db.Credentials.SingleAsync(stored => stored.UserId == userId);
        var repository = new SessionRepository(db);
        Session ended = Session.Establish(credential, SeedInstant, ExpiryInstant);
        Session survivor = Session.Establish(credential, SeedInstant, ExpiryInstant);
        await repository.AddAsync(ended);
        await repository.AddAsync(survivor);

        // Act
        bool revoked = await repository.RevokeAsync(ended.Id, RevocationInstant);

        // Assert — the surviving row is the whole content of this test. Signing one browser out must
        // leave the device in the person's hand signed in, and a predicate wide enough to take the
        // account would end both while reporting the same true and leaving the same instant on the row
        // this test names. Read back on a fresh context so the answer comes off the rows rather than
        // off the tracked entity the revocation just mutated.
        await Assert.That(revoked).IsTrue();
        await using BudgetoidDbContext verify = CreateDb(host);
        Session storedEnded = await verify.Sessions.SingleAsync(row => row.Id == ended.Id);
        Session storedSurvivor = await verify.Sessions.SingleAsync(row => row.Id == survivor.Id);
        await Assert.That(storedEnded.RevokedAtUtc).IsEqualTo(RevocationInstant);
        await Assert.That(storedSurvivor.RevokedAtUtc).IsNull();
    }

    [Test]
    public async Task RevokeAsync_RunTwice_KeepsTheFirstInstantAndReportsEndingNothing()
    {
        // Arrange — the second call names a later instant, so a restamp would be visible rather than
        // hidden behind an identical value.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        Credential credential = await db.Credentials.SingleAsync(stored => stored.UserId == userId);
        var repository = new SessionRepository(db);
        Session session = Session.Establish(credential, SeedInstant, ExpiryInstant);
        await repository.AddAsync(session);

        // Act
        bool first = await repository.RevokeAsync(session.Id, RevocationInstant);
        bool second = await repository.RevokeAsync(session.Id, LaterRevocationInstant);

        // Assert — the boolean means "this call ended it", never "it is ended", and the two readings
        // come apart on exactly this sequence. A caller reporting a revocation to the person who asked
        // for it needs the distinction, and a caller retrying after a timeout needs it not to lie: a
        // second true would tell somebody they had just cut off access they had already cut off.
        //
        // The instant is the other half and it is the half with a record attached. Session.Revoke keeps
        // the first one, so the row goes on saying when access ACTUALLY ended rather than when somebody
        // last pressed the button — which is the fact an incident report is written from.
        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        Session stored = await verify.Sessions.SingleAsync(row => row.Id == session.Id);
        await Assert.That(stored.RevokedAtUtc).IsEqualTo(RevocationInstant);
    }

    [Test]
    public async Task RevokeAsync_ForASessionThatWasNeverEstablished_ReportsEndingNothing()
    {
        // Arrange — one real session, so the repository has rows to look through and "found nothing" is
        // a verdict rather than an empty table.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        Credential credential = await db.Credentials.SingleAsync(stored => stored.UserId == userId);
        var repository = new SessionRepository(db);
        Session session = Session.Establish(credential, SeedInstant, ExpiryInstant);
        await repository.AddAsync(session);

        // Act
        bool revoked = await repository.RevokeAsync(Guid.CreateVersion7(), RevocationInstant);

        // Assert — false, and deliberately the SAME false an already-revoked session reports and the
        // same one another account's session reports. That is not laziness about error reporting: a
        // caller who could tell "no such session" from "not yours" could learn that a session id they
        // named is real, which is a fact about somebody else's account. The session that does exist is
        // untouched, which is what says the miss was a miss rather than a sweep.
        await Assert.That(revoked).IsFalse();
        await using BudgetoidDbContext verify = CreateDb(host);
        Session stored = await verify.Sessions.SingleAsync(row => row.Id == session.Id);
        await Assert.That(stored.RevokedAtUtc).IsNull();
    }

    [Test]
    public async Task FindByTokenHashAsync_FindsOnlyTheSessionItsOwnTokenNames()
    {
        // Arrange — one account, two live sessions, and a stored handle against each. Two handles is
        // the arrangement: with one, "the lookup found a row" is satisfied by a translation that
        // ignores the predicate entirely and returns whatever is there.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid firstSessionId;
        byte[] firstToken = SessionTokenBytes(0x11);
        byte[] secondToken = SessionTokenBytes(0x22);
        await using (BudgetoidDbContext seed = CreateDb(host))
        {
            Credential credential = await seed.Credentials.SingleAsync(
                stored => stored.UserId == userId);
            Session first = Session.Establish(credential, SeedInstant, ExpiryInstant);
            Session second = Session.Establish(credential, SeedInstant, ExpiryInstant);
            firstSessionId = first.Id;
            seed.Sessions.AddRange(first, second);

            // Through SessionToken.For and in the same SaveChangesAsync as the sessions, which is the
            // shape the establishing path will write: a handle committed without its session names
            // nothing, and the factory reads both ids off the session so nothing here can file one
            // against the wrong sign-in.
            seed.SessionTokens.Add(SessionToken.For(first, firstToken));
            seed.SessionTokens.Add(SessionToken.For(second, secondToken));
            await seed.SaveChangesAsync();
        }

        // Act — on a fresh context, hashing the way the caller does. The repository takes the digest
        // rather than the token, so the raw handle stops at the boundary that decoded it and no
        // persistence port has a member a live token can travel through.
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new SessionTokenRepository(db);
        SessionToken? found = await repository.FindByTokenHashAsync(SessionToken.HashOf(firstToken));
        SessionToken? missing = await repository.FindByTokenHashAsync(
            SessionToken.HashOf(SessionTokenBytes(0x33)));

        // Assert — this is the one place the bytea comparison the property's value converter produces
        // is exercised end to end, and the failure it guards against is silent. TokenHash is a
        // ReadOnlyMemory<byte>, which declares no equality operator: the repository compares with
        // Equals so the provider translates it to a comparison of CONTENT, and a translation that
        // bound to the object overload — or that compared buffer identity — would find nothing, ever.
        // Every session in the system would simply stop being found, and every request would arrive
        // unauthenticated with no error naming a cause.
        //
        // The miss is the other half and it is not decoration: a lookup that returned the first row it
        // saw would satisfy the hit alone, and this arrangement holds two rows for it to choose wrongly
        // between.
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.SessionId).IsEqualTo(firstSessionId);
        await Assert.That(found.UserId).IsEqualTo(userId);
        await Assert.That(missing).IsNull();
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// A token of <see cref="SessionToken.TokenLength" /> bytes, every one of them
    /// <paramref name="fill" />.
    /// </summary>
    /// <remarks>
    /// The fill byte is required rather than defaulted because two handles must differ: the digest is
    /// the primary key of <c>session_tokens</c>, so two identical tokens would be one row, and the
    /// lookup would then have nothing to choose wrongly between. The width is read off the domain
    /// because <see cref="SessionToken.For" /> refuses any other, from both sides.
    /// </remarks>
    private static byte[] SessionTokenBytes(byte fill) =>
        [.. Enumerable.Repeat(fill, SessionToken.TokenLength)];

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
