using Application.Sessions.RevokeSessionsForCredential;
using Domain.Sessions;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class RevokeSessionsForCredentialHandlerTests
{
    [Test]
    public async Task HandleAsync_RevokesTheSessionsTheCredentialEstablished()
    {
        // Arrange — one credential that signed in twice, so the sweep has to reach more than the
        // session that happened to be first.
        Guid userId = Guid.CreateVersion7();
        Credential credential = GoogleCredentialFor(userId, "google-1");
        var sessions = new InMemorySessionRepository();
        await sessions.AddAsync(SessionFor(credential));
        await sessions.AddAsync(SessionFor(credential));
        var handler = new RevokeSessionsForCredentialHandler(sessions, FixedClock());

        // Act
        int revoked = await handler.HandleAsync(new RevokeSessionsForCredentialCommand(credential.Id));

        // Assert — a credential is withdrawn as a whole, so leaving any of the sessions it opened
        // alive would leave the withdrawn credential's holder still inside the account.
        await Assert.That(revoked).IsEqualTo(2);

        foreach (Session session in sessions.Sessions)
        {
            await Assert.That(session.RevokedAtUtc).IsNotNull();
            await Assert.That(session.IsActiveAt(UtcNow())).IsFalse();
        }
    }

    [Test]
    public async Task HandleAsync_LeavesAnotherCredentialsSessionsActive()
    {
        // Arrange — one account, two ways into it, one live session each.
        Guid userId = Guid.CreateVersion7();
        Credential credentialA = GoogleCredentialFor(userId, "google-a");
        Credential credentialB = GoogleCredentialFor(userId, "google-b");
        Session sessionA = SessionFor(credentialA);
        Session sessionB = SessionFor(credentialB);
        var sessions = new InMemorySessionRepository();
        await sessions.AddAsync(sessionA);
        await sessions.AddAsync(sessionB);
        var handler = new RevokeSessionsForCredentialHandler(sessions, FixedClock());

        // Act
        int revoked = await handler.HandleAsync(new RevokeSessionsForCredentialCommand(credentialA.Id));

        // Assert — this is the assertion the whole rule turns on. The most likely wrong implementation
        // narrows on user_id instead of credential_id, and every other test in this file passes
        // under it, because every other test holds sessions for only one credential. Under that
        // implementation removing one sign-in method signs the user out of the device they are
        // holding, which turns a routine credential cleanup into an account-wide lockout.
        await Assert.That(revoked).IsEqualTo(1);
        await Assert.That(sessionA.RevokedAtUtc).IsNotNull();
        await Assert.That(sessionB.RevokedAtUtc).IsNull();
        await Assert.That(sessionB.IsActiveAt(UtcNow())).IsTrue();
    }

    [Test]
    public async Task HandleAsync_RevokesAtTheInstantTheClockReports()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        Credential credential = GoogleCredentialFor(userId, "google-1");
        Session session = SessionFor(credential);
        var sessions = new InMemorySessionRepository();
        await sessions.AddAsync(session);
        var handler = new RevokeSessionsForCredentialHandler(sessions, FixedClock());

        // Act
        await handler.HandleAsync(new RevokeSessionsForCredentialCommand(credential.Id));

        // Assert — the instant travels down from the handler rather than being read where the rows
        // are written. One sweep is one decision to end access, and it should read as one instant in
        // the record; a repository reading its own clock would stamp a sweep that touched many
        // sessions with as many different instants, and nobody auditing it later could tell that
        // spread apart from sessions genuinely ended at different times.
        await Assert.That(sessions.LastRevokedAtUtc).IsEqualTo(UtcNow());
        await Assert.That(session.RevokedAtUtc).IsEqualTo(UtcNow());
    }

    [Test]
    public async Task HandleAsync_RunTwice_KeepsTheFirstRevocationInstant()
    {
        // Arrange — the clock moves between the two calls, so a second write would be visible.
        Guid userId = Guid.CreateVersion7();
        Credential credential = GoogleCredentialFor(userId, "google-1");
        Session session = SessionFor(credential);
        var sessions = new InMemorySessionRepository();
        await sessions.AddAsync(session);
        FakeTimeProvider clock = FixedClock();
        var handler = new RevokeSessionsForCredentialHandler(sessions, clock);

        // Act
        int first = await handler.HandleAsync(new RevokeSessionsForCredentialCommand(credential.Id));
        clock.Advance(TimeSpan.FromMinutes(5));
        int second = await handler.HandleAsync(new RevokeSessionsForCredentialCommand(credential.Id));

        // Assert — access ended once, at the moment it actually ended, and the retry says so by
        // returning zero. Both halves matter: restamping would move the record of a compromise
        // forward to whenever someone last pressed the button, and a second non-zero count would
        // tell the person who pressed it that they had cut off access they had already cut off.
        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(0);
        await Assert.That(session.RevokedAtUtc).IsEqualTo(UtcNow());
        await Assert.That(sessions.RevokeForCredentialCallCount).IsEqualTo(2);
    }

    [Test]
    public async Task HandleAsync_WithNoSessionsForTheCredential_ReturnsZero()
    {
        // Arrange — a credential that never opened a session, next to one that did.
        Guid userId = Guid.CreateVersion7();
        Credential establishedNothing = GoogleCredentialFor(userId, "google-unused");
        Credential signedIn = GoogleCredentialFor(userId, "google-1");
        Session session = SessionFor(signedIn);
        var sessions = new InMemorySessionRepository();
        await sessions.AddAsync(session);
        var handler = new RevokeSessionsForCredentialHandler(sessions, FixedClock());

        // Act
        int revoked = await handler.HandleAsync(new RevokeSessionsForCredentialCommand(establishedNothing.Id));

        // Assert — nothing to end is an ordinary answer, not a failure: a credential can be
        // withdrawn from an account nobody is currently signed in to, and the caller still needs a
        // truthful count to report.
        await Assert.That(revoked).IsEqualTo(0);
        await Assert.That(session.RevokedAtUtc).IsNull();
    }

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static FakeTimeProvider FixedClock() => new(new DateTimeOffset(UtcNow()));

    private static Credential GoogleCredentialFor(Guid userId, string subject) =>
        Credential.CreateFederated(userId, Credential.GoogleProvider, subject, UtcNow());

    private static Session SessionFor(Credential credential) =>
        Session.Establish(credential, UtcNow(), UtcNow().AddHours(1));
}
