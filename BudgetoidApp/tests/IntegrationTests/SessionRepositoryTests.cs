using System.Data.Common;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
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
public sealed partial class SessionRepositoryTests
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
        await repository.AddAsync(sessionA, AHandleFor(sessionA));
        await repository.AddAsync(sessionB, AHandleFor(sessionB));

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
        await repository.AddAsync(session, AHandleFor(session));

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
        await repository.AddAsync(ended, AHandleFor(ended));
        await repository.AddAsync(survivor, AHandleFor(survivor));

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
        await repository.AddAsync(session, AHandleFor(session));

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
        await repository.AddAsync(session, AHandleFor(session));

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
    /// The discovery lookup hands back a handle the context is not tracking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the change-tracker trap, one table further down.</b> The lookup runs on every request
    /// presenting a cookie, inside the request's own context. Establishing a session now deletes the
    /// account's ended sessions in that same context, and an ended session can be the very one the
    /// cookie named — the locked sign-in reads the cookie and never clears the tracker. With the handle
    /// tracked, EF cascades into it and sends its own <c>DELETE FROM session_tokens</c>, on a table the
    /// role holds no <c>DELETE</c> on, so the sign-in dies with <c>42501</c>. The database cascade from
    /// <c>sessions</c> is what should take the row, and it can only do that if EF leaves it alone.
    /// </para>
    /// <para>
    /// Asserted on the tracker rather than through the sign-in because this is where the cause is.
    /// <c>LockedSignInEndpointTests.LockedSignIn_OverItsOwnAccountsEndedSession_DeletesThatSessionAndItsHandle</c>
    /// is the symptom's test.
    /// </para>
    /// </remarks>
    [Test]
    public async Task FindByTokenHashAsync_ReturnsAnUntrackedEntity()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.FederatedCredentialIdAsync(userId);
        byte[] token = await host.SeedSessionAsync(
            credentialId, 0x11, SessionKind.Locked, SeedInstant, ExpiryInstant);
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new SessionTokenRepository(db);

        // Act
        SessionToken? found = await repository.FindByTokenHashAsync(SessionToken.HashOf(token));

        // Assert — found first, so an empty tracker cannot pass on a lookup that found nothing.
        await Assert.That(found).IsNotNull();
        await Assert.That(db.ChangeTracker.Entries<SessionToken>().Count()).IsEqualTo(0);
    }

    /// <summary>
    /// Establishing a session deletes every session of the same account that has ended — revoked or
    /// expired, on any credential — with its handle, and keeps every live one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two credentials and both ways of ending.</b> A sweep keyed on the establishing credential
    /// passes a one-credential arrangement, and a sweep reading only <c>revoked_at_utc</c> passes an
    /// arrangement with no expired row. The ended rows sit on <b>different</b> credentials, and each
    /// credential also holds a live session, so neither wrong key can delete exactly the right set.
    /// </para>
    /// <para>
    /// <b>One revoked row carries a revocation instant <em>after</em> the establishing instant</b>, and
    /// is not expired. Nothing in the schema orders the two instants, and <c>Session.IsActiveAt</c> reads
    /// any revocation as ended. A sweep that rewrote the rule as
    /// <c>RevokedAtUtc &lt;= created || ExpiresAtUtc &lt;= created</c> deletes every other ended row here
    /// and keeps this one.
    /// </para>
    /// <para>
    /// <b>Every instant is months in the past.</b> "Ended" is judged at the new session's own
    /// <c>created_at_utc</c>. A sweep reading the wall clock instead would find the two live sessions
    /// expired as well and delete them.
    /// </para>
    /// <para>
    /// <b>The app role, with the owner on the connection.</b> The sweep names no owner and leaves the
    /// scoping to <c>user_isolation</c>, so this is measured on the connection production uses.
    /// The handles are checked because the role holds no <c>DELETE</c> on <c>session_tokens</c>: they
    /// can only leave by the database's cascade from <c>sessions</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddAsync_DeletesTheOwnersRevokedAndExpiredSessions_AndKeepsTheLiveOnes()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync("google-1", "person@example.com");
        Guid credentialA = await host.FederatedCredentialIdAsync(owner.UserId);
        Guid credentialB = await host.SeedPasskeyAsync(owner.UserId, PasskeyHandle(0x01));
        Dictionary<Guid, string> labels = new()
        {
            [await SeedEndingAsync(host, credentialA, 0x11, SessionKind.Locked, ExpiryInstant, RevocationInstant)] =
                "revoked on A",
            [await SeedEndingAsync(host, credentialB, 0x22, SessionKind.Full, ExpiredInstant)] = "expired on B",
            [await SeedEndingAsync(
                host, credentialB, 0x55, SessionKind.Full, ExpiryInstant, EstablishingInstant.AddHours(1))] =
                "revoked after the instant on B",
            [await SeedEndingAsync(host, credentialA, 0x33, SessionKind.Locked, ExpiryInstant)] = "live on A",
            [await SeedEndingAsync(host, credentialB, 0x44, SessionKind.Full, ExpiryInstant)] = "live on B",
        };
        Session established = Session.Establish(
            await LoadCredentialAsync(host, credentialA), EstablishingInstant, EstablishedExpiryInstant);
        labels[established.Id] = "established";

        // Act
        await using (BudgetoidDbContext db = AppDb(host, owner))
        {
            await new SessionRepository(db).AddAsync(established, AHandleFor(established));
        }

        // Assert — the account's rows and the account's handles, read on the superuser connection.
        await Assert.That(await RenderSessionsOfAsync(host, owner.UserId, labels))
            .IsEqualTo("established, live on A, live on B");
        await Assert.That(await RenderHandlesOfAsync(host, owner.UserId, labels))
            .IsEqualTo("established, live on A, live on B");
    }

    /// <summary>
    /// A session whose expiry is exactly the establishing instant counts as ended, and one expiring a
    /// microsecond later counts as live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The boundary is <c>Session.IsActiveAt</c>'s: live up to the expiry and not at it. A sweep that
    /// wrote its own comparison with <c>&lt;</c> where the domain has <c>&lt;=</c> keeps the first row;
    /// one that compared against the new session's expiry, or against the wall clock, deletes the
    /// second. A microsecond is the smallest step <c>timestamptz</c> stores, so nothing narrower
    /// survives the round trip.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddAsync_TreatsASessionExpiringAtTheEstablishingInstantAsEnded()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync("google-1", "person@example.com");
        Guid credentialId = await host.FederatedCredentialIdAsync(owner.UserId);
        Dictionary<Guid, string> labels = new()
        {
            [await SeedEndingAsync(host, credentialId, 0x11, SessionKind.Locked, EstablishingInstant)] =
                "expires at the instant",
            [await SeedEndingAsync(
                host, credentialId, 0x22, SessionKind.Locked, EstablishingInstant.AddTicks(OneMicrosecond))] =
                "expires a microsecond later",
        };
        Session established = Session.Establish(
            await LoadCredentialAsync(host, credentialId), EstablishingInstant, EstablishedExpiryInstant);
        labels[established.Id] = "established";

        // Act
        await using (BudgetoidDbContext db = AppDb(host, owner))
        {
            await new SessionRepository(db).AddAsync(established, AHandleFor(established));
        }

        // Assert
        await Assert.That(await RenderSessionsOfAsync(host, owner.UserId, labels))
            .IsEqualTo("established, expires a microsecond later");
    }

    /// <summary>
    /// Establishing a session leaves another account's ended sessions where they are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sweep names no owner, so the policy is the only thing scoping it.</b> On the superuser
    /// connection the same sweep would take the stranger's rows too, which is why this runs on the app
    /// role with the owner published. The stranger holds both kinds of ended row, so neither half of
    /// "ended" can reach across.
    /// </para>
    /// <para>
    /// The owner's own revoked session is the control. Without it this test is green for a repository
    /// that never deletes anything — which is the repository there is today.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddAsync_LeavesAnotherAccountsEndedSessionsInPlace()
    {
        // Arrange — the stranger first, so the owner's rows are not simply the first in the table.
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner stranger = await host.SeedOwnerAsync("google-0", "stranger@example.com");
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync("google-1", "person@example.com");
        Guid strangerCredential = await host.FederatedCredentialIdAsync(stranger.UserId);
        Guid ownerCredential = await host.FederatedCredentialIdAsync(owner.UserId);
        Dictionary<Guid, string> labels = new()
        {
            [await SeedEndingAsync(
                host, strangerCredential, 0x11, SessionKind.Locked, ExpiryInstant, RevocationInstant)] =
                "stranger revoked",
            [await SeedEndingAsync(host, strangerCredential, 0x22, SessionKind.Locked, ExpiredInstant)] =
                "stranger expired",
            [await SeedEndingAsync(
                host, ownerCredential, 0x33, SessionKind.Locked, ExpiryInstant, RevocationInstant)] =
                "owner revoked",
        };
        Session established = Session.Establish(
            await LoadCredentialAsync(host, ownerCredential), EstablishingInstant, EstablishedExpiryInstant);
        labels[established.Id] = "established";

        // Act
        await using (BudgetoidDbContext db = AppDb(host, owner))
        {
            await new SessionRepository(db).AddAsync(established, AHandleFor(established));
        }

        // Assert — the stranger's rows and handles stand; the owner's ended row went.
        await Assert.That(await RenderSessionsOfAsync(host, stranger.UserId, labels))
            .IsEqualTo("stranger expired, stranger revoked");
        await Assert.That(await RenderHandlesOfAsync(host, stranger.UserId, labels))
            .IsEqualTo("stranger expired, stranger revoked");
        await Assert.That(await RenderSessionsOfAsync(host, owner.UserId, labels))
            .IsEqualTo("established");
    }

    /// <summary>
    /// When the new session cannot be stored, no ended session is deleted either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One save, so one outcome.</b> The new session's credential is deleted out of band in the
    /// window before the save, so its insert fails with <c>23503</c>. A sweep run in a save of its own
    /// ahead of the insert would already be committed, and the ended row would be gone from a sign-in
    /// that never happened.
    /// </para>
    /// <para>
    /// <b>The second act is the control, and without it this test proves nothing today.</b> A
    /// repository that never deletes passes the first half. A clean establishment on the same account
    /// afterwards has to take the ended row, which shows the row was the sweep's to take.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddAsync_WhenTheNewSessionCannotBeStored_DeletesNothing()
    {
        // Arrange — the ended session on A, the doomed sign-in over B, so deleting B takes nothing of A's.
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync("google-1", "person@example.com");
        Guid credentialA = await host.FederatedCredentialIdAsync(owner.UserId);
        Guid credentialB = await host.SeedPasskeyAsync(owner.UserId, PasskeyHandle(0x01));
        Dictionary<Guid, string> labels = new()
        {
            [await SeedEndingAsync(host, credentialA, 0x11, SessionKind.Locked, ExpiryInstant, RevocationInstant)] =
                "revoked on A",
        };
        Session doomed = Session.Establish(
            await LoadCredentialAsync(host, credentialB), EstablishingInstant, EstablishedExpiryInstant);
        ConcurrentDeleteInterceptor credentialRemoval = new(
            host.ConnectionString, "delete from credentials where id = @id", credentialB);

        // Act
        Exception? refusal;
        await using (BudgetoidDbContext db = AppDb(host, owner, credentialRemoval))
        {
            refusal = await CaptureAsync(() => new SessionRepository(db).AddAsync(doomed, AHandleFor(doomed)));
        }

        // Assert — the arrangement happened, the insert was refused for the reason arranged, and the
        // ended row is still there.
        await Assert.That(credentialRemoval.Deleted).IsEqualTo(1);
        await Assert.That(SqlStateOf(refusal) ?? "no error").IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(await RenderSessionsOfAsync(host, owner.UserId, labels)).IsEqualTo("revoked on A");

        // Act — the control: a clean establishment on the same account.
        Session control = Session.Establish(
            await LoadCredentialAsync(host, credentialA), EstablishingInstant, EstablishedExpiryInstant);
        labels[control.Id] = "established over A";
        await using (BudgetoidDbContext db = AppDb(host, owner))
        {
            await new SessionRepository(db).AddAsync(control, AHandleFor(control));
        }

        // Assert
        await Assert.That(await RenderSessionsOfAsync(host, owner.UserId, labels))
            .IsEqualTo("established over A");
    }

    /// <summary>
    /// When the delete of an ended session fails, the new session is not stored either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="AddAsync_WhenTheNewSessionCannotBeStored_DeletesNothing" />. A command
    /// interceptor refuses any command carrying <c>DELETE FROM sessions</c> before it reaches the
    /// database. With one save, that command also carries the insert, so nothing is written. A sweep in
    /// a save of its own after the insert leaves the new session committed beside a sweep that never
    /// ran.
    /// </para>
    /// <para>
    /// Today there is no delete for the interceptor to refuse, so the act throws nothing and this fails
    /// on the first assertion.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddAsync_WhenTheDeleteFails_StoresNoSession()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync("google-1", "person@example.com");
        Guid credentialId = await host.FederatedCredentialIdAsync(owner.UserId);
        Dictionary<Guid, string> labels = new()
        {
            [await SeedEndingAsync(host, credentialId, 0x11, SessionKind.Locked, ExpiryInstant, RevocationInstant)] =
                "revoked",
        };
        Session established = Session.Establish(
            await LoadCredentialAsync(host, credentialId), EstablishingInstant, EstablishedExpiryInstant);
        labels[established.Id] = "established";
        SessionDeleteRefusingInterceptor refusing = new();

        // Act
        Exception? refusal;
        await using (BudgetoidDbContext db = AppDb(host, owner, refusing))
        {
            refusal = await CaptureAsync(
                () => new SessionRepository(db).AddAsync(established, AHandleFor(established)));
        }

        // Assert — the interceptor's own refusal, not some other failure, and nothing written.
        await Assert.That(refusal?.GetBaseException().GetType().Name ?? "no exception")
            .IsEqualTo(nameof(SessionDeleteRefusedException));
        await Assert.That(await RenderSessionsOfAsync(host, owner.UserId, labels)).IsEqualTo("revoked");
    }

    /// <summary>
    /// When another sign-in deleted one of the ended rows between this one's read and its save, this
    /// one still establishes and still deletes the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The race is the ordinary case on a busy account.</b> Two devices sign in at once and both read
    /// the same ended rows. The loser's <c>DELETE</c> matches nothing, EF raises
    /// <c>DbUpdateConcurrencyException</c>, and a repository that let that escape would turn a sign-in
    /// into a 500 because somebody else's sign-in tidied up first.
    /// </para>
    /// <para>
    /// <b>Two ended rows, one taken by the racer.</b> The survivor is what a retry has to delete: a
    /// repository that gave up on the sweep and saved only the new session would establish and leave it
    /// behind. Run inside a caller's transaction too, because three of the establishing paths call this
    /// inside one, and a failed save there has to roll back to a savepoint rather than poison the
    /// transaction.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AddAsync_WhenAnotherSignInDeletedTheEndedRowFirst_StillEstablishes(bool insideATransaction)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync("google-1", "person@example.com");
        Guid credentialId = await host.FederatedCredentialIdAsync(owner.UserId);
        Guid takenByTheRacer = await SeedEndingAsync(
            host, credentialId, 0x11, SessionKind.Locked, ExpiryInstant, RevocationInstant);
        Dictionary<Guid, string> labels = new()
        {
            [takenByTheRacer] = "revoked, taken by the racer",
            [await SeedEndingAsync(host, credentialId, 0x22, SessionKind.Locked, ExpiredInstant)] = "expired",
        };
        Session established = Session.Establish(
            await LoadCredentialAsync(host, credentialId), EstablishingInstant, EstablishedExpiryInstant);
        labels[established.Id] = "established";
        ConcurrentDeleteInterceptor racer = new(
            host.ConnectionString, "delete from sessions where id = @id", takenByTheRacer);

        // Act
        await using (BudgetoidDbContext db = AppDb(host, owner, racer))
        {
            await AddInsideAsync(db, established, insideATransaction);
        }

        // Assert — the race really ran, and the account is left holding only the new session.
        await Assert.That(racer.Deleted).IsEqualTo(1);
        await Assert.That(await RenderSessionsOfAsync(host, owner.UserId, labels)).IsEqualTo("established");
    }

    /// <summary>
    /// When an expired row is revoked by somebody else between this sign-in's read and its save, the
    /// row is still deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>revoked_at_utc</c> is a concurrency token, so the <c>DELETE</c> of an expired row carries
    /// <c>revoked_at_utc IS NULL</c>. A credential revocation landing in the window stamps the row and
    /// that predicate matches nothing. The row is still ended — it is now ended twice over — so the
    /// right answer is to read it again and delete it, not to report a failure and not to skip it.
    /// </para>
    /// <para>
    /// The same two shapes as the test above: on its own, and inside a caller's transaction.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AddAsync_WhenAnExpiredRowIsRevokedConcurrently_StillDeletesIt(bool insideATransaction)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync("google-1", "person@example.com");
        Guid credentialId = await host.FederatedCredentialIdAsync(owner.UserId);
        Guid expired = await SeedEndingAsync(host, credentialId, 0x11, SessionKind.Locked, ExpiredInstant);
        Dictionary<Guid, string> labels = new() { [expired] = "expired, then revoked" };
        Session established = Session.Establish(
            await LoadCredentialAsync(host, credentialId), EstablishingInstant, EstablishedExpiryInstant);
        labels[established.Id] = "established";
        ConcurrentRevocationInterceptor revoker = new(host.ConnectionString, expired, RevocationInstant);

        // Act
        await using (BudgetoidDbContext db = AppDb(host, owner, revoker))
        {
            await AddInsideAsync(db, established, insideATransaction);
        }

        // Assert
        await Assert.That(revoker.Revoked).IsEqualTo(1);
        await Assert.That(await RenderSessionsOfAsync(host, owner.UserId, labels)).IsEqualTo("established");
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
    /// A throwaway handle for <paramref name="session" />, for the arrangements that are about a
    /// revocation predicate and have no opinion about the handle beside the row.
    /// </summary>
    /// <remarks>
    /// <see cref="ISessionRepository.AddAsync" /> takes both and offers no shape that writes a session
    /// alone — a session committed without its handle is a sign-in nobody can present — so every
    /// arrangement here has to mint one. Random rather than a fill byte, because the digest is the
    /// primary key of <c>session_tokens</c> and several of these tests seed two sessions in one
    /// arrangement: two handles that happened to be equal would be a duplicate-key failure with
    /// nothing to do with what the test is about.
    /// </remarks>
    private static SessionToken AHandleFor(Session session) =>
        SessionToken.For(session, RandomNumberGenerator.GetBytes(SessionToken.TokenLength));

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

    /// <summary>
    /// The instant the sweep tests establish their new session at. Months in the past, like every
    /// instant here, so a sweep reading the wall clock finds the "live" rows expired.
    /// </summary>
    private static readonly DateTime EstablishingInstant = new(2026, 6, 12, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>Expiry of the new session the sweep tests establish.</summary>
    private static readonly DateTime EstablishedExpiryInstant = new(2026, 6, 26, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The expiry of a session that has run out by <see cref="EstablishingInstant" />. After
    /// <see cref="SeedInstant" />, so <c>CK_sessions_lifetime</c> accepts it.
    /// </summary>
    private static readonly DateTime ExpiredInstant = new(2026, 6, 12, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// One microsecond in ticks: the smallest step PostgreSQL <c>timestamptz</c> stores.
    /// </summary>
    private const long OneMicrosecond = TimeSpan.TicksPerMicrosecond;

    /// <summary>A WebAuthn credential id of the minimum width, every byte <paramref name="fill" />.</summary>
    private static byte[] PasskeyHandle(byte fill) =>
        [.. Enumerable.Repeat(fill, PasskeyPublicKey.MinWebAuthnCredentialIdLength)];

    /// <summary>
    /// Seeds one session created at <see cref="SeedInstant" /> with its handle, and returns the session
    /// id read back through that handle.
    /// </summary>
    /// <remarks>
    /// Seeded on the superuser connection through <c>Session.Establish</c>, so the row is one the product
    /// could write. Revoked when <paramref name="revokedAtUtc" /> is given.
    /// </remarks>
    private static async Task<Guid> SeedEndingAsync(
        RepositoryTestHost host,
        Guid credentialId,
        byte fill,
        SessionKind kind,
        DateTime expiresAtUtc,
        DateTime? revokedAtUtc = null)
    {
        byte[] token = await host.SeedSessionAsync(
            credentialId, fill, kind, SeedInstant, expiresAtUtc, revokedAtUtc);

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select session_id from session_tokens where token_hash = @digest", connection);
        command.Parameters.AddWithValue("digest", SessionToken.HashOf(token));

        return await command.ExecuteScalarAsync() switch
        {
            Guid sessionId => sessionId,
            var unexpected => throw new InvalidOperationException(
                $"Expected the seeded handle's session id, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>Loads a credential on the superuser connection, outside the context under test.</summary>
    private static async Task<Credential> LoadCredentialAsync(RepositoryTestHost host, Guid credentialId)
    {
        await using BudgetoidDbContext db = CreateDb(host);

        return await db.Credentials.AsNoTracking().SingleAsync(credential => credential.Id == credentialId);
    }

    /// <summary>
    /// The account's <c>sessions</c> rows as their labels, sorted and joined, read on the superuser
    /// connection so row-level security cannot hide a row.
    /// </summary>
    /// <remarks>
    /// Labels rather than ids, so a failure names which arranged row is wrongly present or absent. A row
    /// nobody labelled renders as <c>unlabelled</c> rather than vanishing from the comparison.
    /// </remarks>
    private static Task<string> RenderSessionsOfAsync(
        RepositoryTestHost host,
        Guid userId,
        IReadOnlyDictionary<Guid, string> labels) =>
        RenderAsync(host, "select id from sessions where user_id = @user_id", userId, labels);

    /// <summary>
    /// The account's <c>session_tokens</c> rows, each rendered as the label of the session it opens.
    /// </summary>
    private static Task<string> RenderHandlesOfAsync(
        RepositoryTestHost host,
        Guid userId,
        IReadOnlyDictionary<Guid, string> labels) =>
        RenderAsync(host, "select session_id from session_tokens where user_id = @user_id", userId, labels);

    private static async Task<string> RenderAsync(
        RepositoryTestHost host,
        string sql,
        Guid userId,
        IReadOnlyDictionary<Guid, string> labels)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("user_id", userId);

        List<string> rendered = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Guid id = reader.GetGuid(0);
            rendered.Add(labels.TryGetValue(id, out string? label) ? label : $"unlabelled {id}");
        }

        return string.Join(", ", rendered.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Runs the act on its own, or inside a transaction the test opens and commits — the shape the
    /// passkey assertion, the redemption and the regeneration call it in.
    /// </summary>
    private static async Task AddInsideAsync(BudgetoidDbContext db, Session session, bool insideATransaction)
    {
        var repository = new SessionRepository(db);
        if (!insideATransaction)
        {
            await repository.AddAsync(session, AHandleFor(session));
            return;
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        await repository.AddAsync(session, AHandleFor(session));
        await transaction.CommitAsync();
    }

    private static string? SqlStateOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.SqlState
            : null;

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" />.
    /// Untyped on purpose: which exception surfaces is the question.
    /// </summary>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// A context on the <b>least-privilege</b> connection with <c>SessionContextInterceptor</c> wired
    /// in, so the owner is on every connection it opens — the shape production runs the sweep in.
    /// </summary>
    /// <remarks>
    /// The sweep names no owner and leaves the scoping to <c>user_isolation</c>, so a superuser context
    /// would not measure it at all: it skips the policy and would sweep every account.
    /// </remarks>
    private static BudgetoidDbContext AppDb(
        RepositoryTestHost host,
        RepositoryTestHost.SeededOwner owner,
        params IInterceptor[] interceptors)
    {
        TestBudgetContext budgetContext = new(owner.BudgetId);
        TestUserContext userContext = new(owner.UserId);

        return new BudgetoidDbContext(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.AppConnectionString)
                .AddInterceptors([new SessionContextInterceptor(budgetContext, userContext), .. interceptors])
                .Options,
            budgetContext);
    }

    /// <summary>
    /// Refuses, before it reaches the database, any command that deletes from <c>sessions</c>.
    /// </summary>
    /// <remarks>
    /// Refused while executing rather than after, on purpose. A multi-statement command with no
    /// explicit transaction commits as one implicit transaction, so throwing after it ran would leave
    /// its rows committed and blame the repository for it.
    /// </remarks>
    private sealed partial class SessionDeleteRefusingInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            SessionDelete().IsMatch(command.CommandText)
                ? throw new SessionDeleteRefusedException()
                : ValueTask.FromResult(result);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            SessionDelete().IsMatch(command.CommandText)
                ? throw new SessionDeleteRefusedException()
                : ValueTask.FromResult(result);

        [GeneratedRegex("""DELETE\s+FROM\s+"?sessions"?\s""", RegexOptions.IgnoreCase)]
        private static partial Regex SessionDelete();
    }

    /// <summary>The refusal <see cref="SessionDeleteRefusingInterceptor" /> raises, told apart by type.</summary>
    private sealed class SessionDeleteRefusedException()
        : Exception("A test interceptor refused the DELETE FROM sessions.");

    /// <summary>
    /// Stands in for a credential revocation landing between a sweep's read and its save: revokes one
    /// session on its own superuser connection, once, just before the first save.
    /// </summary>
    /// <remarks>
    /// <see cref="ConcurrentDeleteInterceptor" />'s shape with an <c>UPDATE</c> in place of the delete.
    /// Its own connection, so the write is committed and visible to the statement it got in front of.
    /// </remarks>
    private sealed class ConcurrentRevocationInterceptor(
        string connectionString,
        Guid sessionId,
        DateTime revokedAtUtc) : SaveChangesInterceptor
    {
        private bool _fired;

        /// <summary>Rows the out-of-band revocation stamped, so the arrangement cannot be a no-op.</summary>
        public int Revoked { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_fired)
            {
                return result;
            }

            _fired = true;

            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand command = new(
                "update sessions set revoked_at_utc = @revoked_at where id = @id", connection);
            command.Parameters.AddWithValue("revoked_at", revokedAtUtc);
            command.Parameters.AddWithValue("id", sessionId);
            Revoked = await command.ExecuteNonQueryAsync(cancellationToken);

            return result;
        }
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
