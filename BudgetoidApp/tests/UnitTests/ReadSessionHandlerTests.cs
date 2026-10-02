using Application.Sessions.ReadSession;
using Domain.Erasure;
using Domain.Sessions;
using Domain.Users;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// What the handler behind <c>GET /api/me/session</c> answers: the kind and expiry of the session the
/// request authenticated with, and the account's own pending erasure, if it holds one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The session is named by the query, and the query is filled from the request's own
/// authentication</b> — the <c>session_id</c> claim, exactly as the sign-out route fills
/// <c>RevokeSessionCommand</c>. So every case here holds more than one session where it matters: a
/// handler answering "a session on this account" would pass any case with only one.
/// </para>
/// <para>
/// <b>The schedule is the resolved account's and nobody else's.</b> <c>IUserContext</c> is the identity,
/// for the reason <c>ScheduleErasureHandlerTests</c> gives; the stranger's row below carries a date the
/// right answer cannot equal, so "whatever schedule exists" shows up as a wrong instant.
/// </para>
/// <para>
/// The expiries are fixed literals that differ per session, never read off <c>SessionPolicy</c>, so the
/// answer can only have come from the row.
/// </para>
/// </remarks>
public sealed class ReadSessionHandlerTests
{
    [Test]
    public async Task HandleAsync_ForALockedSessionWithNoSchedule_AnswersLockedItsExpiryAndNoErasure()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        InMemorySessionRepository sessions = new();
        Session locked = Session.Establish(FederatedCredential(userId), CreatedAtUtc, LockedExpiresAtUtc);
        await sessions.AddAsync(locked);
        ReadSessionHandler handler = new(
            sessions, new InMemoryErasureScheduleRepository(), new StubUserContext(userId));

        // Act
        SessionSummary summary = await handler.HandleAsync(new ReadSessionQuery(locked.Id));

        // Assert
        await Assert.That(summary.Kind).IsEqualTo(SessionKind.Locked);
        await Assert.That(summary.ExpiresAtUtc).IsEqualTo(LockedExpiresAtUtc);
        await Assert.That(summary.ErasureTakesEffectAtUtc).IsNull();
    }

    [Test]
    public async Task HandleAsync_ForAFullSession_AnswersFullAndItsExpiry()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        InMemorySessionRepository sessions = new();
        Session full = Session.Establish(PasskeyCredential(userId), CreatedAtUtc, FullExpiresAtUtc);
        await sessions.AddAsync(full);
        ReadSessionHandler handler = new(
            sessions, new InMemoryErasureScheduleRepository(), new StubUserContext(userId));

        // Act
        SessionSummary summary = await handler.HandleAsync(new ReadSessionQuery(full.Id));

        // Assert
        await Assert.That(summary.Kind).IsEqualTo(SessionKind.Full);
        await Assert.That(summary.ExpiresAtUtc).IsEqualTo(FullExpiresAtUtc);
        await Assert.That(summary.ErasureTakesEffectAtUtc).IsNull();
    }

    /// <summary>
    /// Two live sessions on one account, of different kinds and expiries; each read answers the one it
    /// names.
    /// </summary>
    /// <remarks>
    /// The full session is stored first, so a handler answering the account's first (or only full)
    /// session gets the locked read wrong, and one answering the latest gets the full read wrong.
    /// </remarks>
    [Test]
    public async Task HandleAsync_AnswersTheNamedSession_NotAnotherOnTheSameAccount()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        InMemorySessionRepository sessions = new();
        Session full = Session.Establish(PasskeyCredential(userId), CreatedAtUtc, FullExpiresAtUtc);
        Session locked = Session.Establish(FederatedCredential(userId), CreatedAtUtc, LockedExpiresAtUtc);
        await sessions.AddAsync(full);
        await sessions.AddAsync(locked);
        ReadSessionHandler handler = new(
            sessions, new InMemoryErasureScheduleRepository(), new StubUserContext(userId));

        // Act
        SessionSummary lockedSummary = await handler.HandleAsync(new ReadSessionQuery(locked.Id));
        SessionSummary fullSummary = await handler.HandleAsync(new ReadSessionQuery(full.Id));

        // Assert
        await Assert.That((lockedSummary.Kind, lockedSummary.ExpiresAtUtc))
            .IsEqualTo((SessionKind.Locked, LockedExpiresAtUtc));
        await Assert.That((fullSummary.Kind, fullSummary.ExpiresAtUtc))
            .IsEqualTo((SessionKind.Full, FullExpiresAtUtc));
    }

    [Test]
    public async Task HandleAsync_WhenTheAccountHoldsASchedule_AnswersItsInstant()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        InMemorySessionRepository sessions = new();
        Session locked = Session.Establish(FederatedCredential(userId), CreatedAtUtc, LockedExpiresAtUtc);
        await sessions.AddAsync(locked);
        InMemoryErasureScheduleRepository schedules = new();
        DateTime filedAt = CreatedAtUtc.AddHours(-36);
        schedules.Seed(ErasureSchedule.Request(userId, filedAt, TimeSpan.FromDays(7)));
        ReadSessionHandler handler = new(sessions, schedules, new StubUserContext(userId));

        // Act
        SessionSummary summary = await handler.HandleAsync(new ReadSessionQuery(locked.Id));

        // Assert — the stored instant, and the read filed nothing.
        await Assert.That(summary.ErasureTakesEffectAtUtc).IsEqualTo(filedAt.AddDays(7));
        await Assert.That(schedules.AddCalls).IsEqualTo(0);
    }

    /// <summary>
    /// A full session reads the account's schedule too: the erasure belongs to the account, not to the
    /// locked session that filed it.
    /// </summary>
    [Test]
    public async Task HandleAsync_ForAFullSessionOnAnAccountHoldingASchedule_AnswersItsInstant()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        InMemorySessionRepository sessions = new();
        Session full = Session.Establish(PasskeyCredential(userId), CreatedAtUtc, FullExpiresAtUtc);
        await sessions.AddAsync(full);
        InMemoryErasureScheduleRepository schedules = new();
        DateTime filedAt = CreatedAtUtc.AddDays(-2);
        schedules.Seed(ErasureSchedule.Request(userId, filedAt, TimeSpan.FromDays(7)));
        ReadSessionHandler handler = new(sessions, schedules, new StubUserContext(userId));

        // Act
        SessionSummary summary = await handler.HandleAsync(new ReadSessionQuery(full.Id));

        // Assert
        await Assert.That(summary.Kind).IsEqualTo(SessionKind.Full);
        await Assert.That(summary.ErasureTakesEffectAtUtc).IsEqualTo(filedAt.AddDays(7));
    }

    /// <summary>
    /// The only stored schedule is a stranger's; this account reads none.
    /// </summary>
    [Test]
    public async Task HandleAsync_DoesNotAnswerAnotherAccountsSchedule()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        Guid strangerId = Guid.CreateVersion7();
        InMemorySessionRepository sessions = new();
        Session locked = Session.Establish(FederatedCredential(userId), CreatedAtUtc, LockedExpiresAtUtc);
        await sessions.AddAsync(locked);
        InMemoryErasureScheduleRepository schedules = new();
        schedules.Seed(ErasureSchedule.Request(strangerId, CreatedAtUtc.AddDays(-3), TimeSpan.FromDays(7)));
        ReadSessionHandler handler = new(sessions, schedules, new StubUserContext(userId));

        // Act
        SessionSummary summary = await handler.HandleAsync(new ReadSessionQuery(locked.Id));

        // Assert
        await Assert.That(summary.ErasureTakesEffectAtUtc).IsNull();
    }

    /// <summary>
    /// A session id that answers to no row the request can see is a fault, not a summary.
    /// </summary>
    /// <remarks>
    /// Unreachable through the route — the cookie handler has just read that very row — so this is the
    /// shape <c>GetSignedInUserHandler</c> gives its missing user row: a deliberate throw, the exact type
    /// asserted, rather than a summary invented for a session nobody holds.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheNamedSessionIsNotFound_Throws()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        ReadSessionHandler handler = new(
            new InMemorySessionRepository(),
            new InMemoryErasureScheduleRepository(),
            new StubUserContext(userId));

        // Act
        InvalidOperationException? exception = null;
        try
        {
            await handler.HandleAsync(new ReadSessionQuery(Guid.CreateVersion7()));
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        // Assert — the exact type: ObjectDisposedException and other framework refusals derive from it.
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.GetType()).IsEqualTo(typeof(InvalidOperationException));
    }

    private static readonly DateTime CreatedAtUtc = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    private static readonly DateTime LockedExpiresAtUtc = CreatedAtUtc.AddMinutes(47);

    private static readonly DateTime FullExpiresAtUtc = CreatedAtUtc.AddMinutes(83);

    private static Credential FederatedCredential(Guid userId) => Credential.CreateFederated(
        userId, Credential.GoogleProvider, "google-read-session-subject", CreatedAtUtc);

    private static Credential PasskeyCredential(Guid userId) => Credential.CreatePasskey(userId, CreatedAtUtc);
}
