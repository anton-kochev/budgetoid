using Application.Sessions.EstablishLockedSession;
using Application.Users;
using Domain.Erasure;
using Domain.Sessions;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The locked sign-in: a provider subject the bearer handler has already vouched for, turned into a
/// <see cref="SessionKind.Locked" /> session over the account's federated credential — or into nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the property, as it is on every establishing path.</b> <c>credentials</c> is exempt,
/// so the discovery lookup runs with nobody published; <c>sessions</c>, <c>session_tokens</c> and
/// <c>erasure_schedules</c> are policed, so everything after it needs the identity first, or it meets
/// <c>''::uuid</c> and dies with <c>22P02</c>. These tests pin that order over an ordered log of calls;
/// the real-role backstop is <c>LockedSignInEndpointTests</c>, where a reordered handler answers 500.
/// </para>
/// <para>
/// <b>The account here always holds a passkey as well as its federated credential, filed first.</b> A
/// handler that opened the session over "the account's first credential" — or looked the account up and
/// then went looking for a credential to hang the session on — passes on an account holding one
/// credential and is caught only by an account holding two. The passkey would also open a
/// <see cref="SessionKind.Full" /> session, which is the whole of what FR-109 refuses.
/// </para>
/// <para>
/// <b>A stranger's account sits beside it in the same store</b>, for the reason the data-isolation
/// chapter gives about a single seeded account: with one row in the table, "this subject's row" and
/// "a row" are the same set.
/// </para>
/// </remarks>
public sealed class EstablishLockedSessionHandlerTests
{
    private static readonly DateTime UtcNow = new(2026, 10, 2, 11, 12, 13, DateTimeKind.Utc);
    private static readonly DateTime IssuedEarlier = UtcNow.AddDays(-40);

    /// <summary>How long a session lasts, restated rather than read off the handler.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    private const string Subject = "google-locked-sign-in";
    private const string StrangerSubject = "google-locked-sign-in-stranger";

    [Test]
    public async Task HandleAsync_ForARegisteredSubject_EstablishesALockedSessionOverTheFederatedCredential()
    {
        // Arrange
        Fixture fixture = Fixture.Create();

        // Act
        LockedSignInOutcome outcome = await fixture.Handler.HandleAsync(new EstablishLockedSessionCommand(Subject));

        // Assert
        await Assert.That(outcome).IsTypeOf<LockedSignInOutcome.Established>();
        Session session = fixture.Sessions.Sessions.Single();
        await Assert.That(session.CredentialId).IsEqualTo(fixture.Federated.Id);
        await Assert.That(session.CredentialId).IsNotEqualTo(fixture.Passkey.Id);
        await Assert.That(session.UserId).IsEqualTo(fixture.UserId);
        await Assert.That(session.CredentialType).IsEqualTo(CredentialType.Federated);
        await Assert.That(session.Kind).IsEqualTo(SessionKind.Locked);
    }

    /// <summary>
    /// The account is the one the subject names, not "an account with a federated credential".
    /// </summary>
    /// <remarks>
    /// The stranger is filed <b>first</b>, so a lookup that dropped its subject clause and took the first
    /// federated row would land on the stranger — which is a locked session on somebody else's account,
    /// opened by a provider sign-in that never named it.
    /// </remarks>
    [Test]
    public async Task HandleAsync_OpensTheSessionOnTheAccountTheSubjectNames_NotAStrangersFiledBeforeIt()
    {
        // Arrange
        Fixture fixture = Fixture.Create();

        // Act
        LockedSignInOutcome outcome = await fixture.Handler.HandleAsync(new EstablishLockedSessionCommand(Subject));

        // Assert
        await Assert.That(outcome).IsTypeOf<LockedSignInOutcome.Established>();
        await Assert.That(fixture.Sessions.Sessions.Single().UserId).IsEqualTo(fixture.UserId);
        await Assert.That(fixture.Writer.Published.Single()).IsEqualTo(fixture.UserId);
        await Assert.That(fixture.Users.CredentialLookupSubjects.Single()).IsEqualTo(Subject);
    }

    /// <summary>
    /// The identity is published after the credential is found and before anything policed is touched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three orderings, read off one log so they are relative to each other rather than to a count: the
    /// lookup runs with nothing published, because the identity it produces is the one it would need;
    /// the session is written with that identity already published, because <c>sessions</c> is policed;
    /// and the schedule is read with it published, for the same reason on <c>erasure_schedules</c>.
    /// </para>
    /// <para>
    /// The identity a statement saw is recorded when the call is <em>entered</em>, as
    /// <see cref="InMemorySessionRepository.IdentityWhenFindByIdWasEntered" /> records it, and
    /// <see cref="Guid.Empty" /> stands for the <c>22P02</c> a real connection would raise.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_PublishesTheIdentityAfterTheLookupAndBeforeAnyPolicedStatement()
    {
        // Arrange
        Fixture fixture = Fixture.Create();
        List<string> log = [];
        EstablishLockedSessionHandler handler = new(
            new ObservingUserRepository(fixture.Users, fixture.Writer, log),
            new ObservingSessionRepository(fixture.Sessions, fixture.Writer, log),
            new ObservingScheduleRepository(fixture.Schedules, fixture.Writer, log),
            new LoggingUserContextWriter(fixture.Writer, log),
            fixture.Clock);

        // Act
        _ = await handler.HandleAsync(new EstablishLockedSessionCommand(Subject));

        // Assert — the lookup saw nobody, every policed statement saw the account.
        string account = fixture.UserId.ToString("D");
        await Assert.That(log).Contains($"lookup as {Guid.Empty:D}");
        await Assert.That(log).Contains($"session add as {account}");
        await Assert.That(log).Contains($"schedule read as {account}");

        int lookup = log.IndexOf($"lookup as {Guid.Empty:D}");
        int publish = log.IndexOf($"publish {account}");
        int add = log.IndexOf($"session add as {account}");
        int schedule = log.IndexOf($"schedule read as {account}");
        await Assert.That(lookup).IsLessThan(publish);
        await Assert.That(publish).IsLessThan(add);
        await Assert.That(publish).IsLessThan(schedule);
    }

    /// <summary>
    /// An unknown subject is an answer, not a fault, and it leaves nothing behind.
    /// </summary>
    /// <remarks>
    /// Nothing published is half the claim, and it is the half that is easy to lose: a published identity
    /// on a request that then answers 404 is the residue <c>sessions.md</c> warns the next anonymous-ish
    /// route is where it starts to matter. Nothing written is the other half — no session, no handle, no
    /// schedule, and no account either, since this route creates none.
    /// </remarks>
    [Test]
    public async Task HandleAsync_ForAnUnknownSubject_AnswersNoAccount_PublishingAndWritingNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Create();

        // Act
        LockedSignInOutcome outcome =
            await fixture.Handler.HandleAsync(new EstablishLockedSessionCommand("google-nobody-registered"));

        // Assert
        await Assert.That(outcome).IsTypeOf<LockedSignInOutcome.NoAccount>();
        await Assert.That(fixture.Writer.Published).IsEmpty();
        await Assert.That(fixture.Writer.PublishedBudgets).IsEmpty();
        await Assert.That(fixture.Sessions.Sessions).IsEmpty();
        await Assert.That(fixture.Sessions.Tokens).IsEmpty();
        await Assert.That(fixture.Schedules.AddCalls).IsEqualTo(0);
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(2);
    }

    /// <summary>
    /// What the caller is told is what was stored: the kind, the expiry, and a handle that opens the row.
    /// </summary>
    /// <remarks>
    /// The handle is checked by recomputing its digest and comparing it with the stored one — a handoff
    /// carrying a different value from the one filed would set a cookie that authenticates nothing, which
    /// every status code on the route would report as success.
    /// </remarks>
    [Test]
    public async Task HandleAsync_HandsBackTheStoredSessionsKindExpiryAndAHandleThatOpensIt()
    {
        // Arrange
        Fixture fixture = Fixture.Create();

        // Act
        LockedSignInOutcome outcome = await fixture.Handler.HandleAsync(new EstablishLockedSessionCommand(Subject));

        // Assert
        LockedSignInOutcome.Established established = (LockedSignInOutcome.Established)outcome;
        Session stored = fixture.Sessions.Sessions.Single();
        SessionToken filed = fixture.Sessions.Tokens.Single();

        await Assert.That(established.Session.Kind).IsEqualTo(SessionKind.Locked);
        await Assert.That(established.Session.ExpiresAtUtc).IsEqualTo(stored.ExpiresAtUtc);
        await Assert.That(established.Handoff.ExpiresAtUtc).IsEqualTo(stored.ExpiresAtUtc);
        await Assert.That(filed.SessionId).IsEqualTo(stored.Id);

        byte[] presented = Base64UrlText.Decode(established.Handoff.Token);
        await Assert.That(SessionToken.HashOf(presented).SequenceEqual(filed.TokenHash.ToArray())).IsTrue();
    }

    /// <summary>
    /// The session begins at the instant the handler read and lasts the product's one session lifetime.
    /// </summary>
    /// <remarks>
    /// An equality against a fixed clock, for <see cref="EstablishedSessionLifetimeTests" />' reason: a
    /// lifetime of forty years is also later than now. That file puts this path beside the other three.
    /// </remarks>
    [Test]
    public async Task HandleAsync_StampsTheSessionFromTheClock_ExpiringFourteenDaysLater()
    {
        // Arrange
        Fixture fixture = Fixture.Create();

        // Act
        _ = await fixture.Handler.HandleAsync(new EstablishLockedSessionCommand(Subject));

        // Assert
        Session stored = fixture.Sessions.Sessions.Single();
        await Assert.That(stored.CreatedAtUtc).IsEqualTo(UtcNow);
        await Assert.That(stored.ExpiresAtUtc).IsEqualTo(UtcNow + SessionLifetime);
    }

    /// <summary>
    /// A clock read finer than a microsecond is cut to the microsecond before the session is built, so
    /// what the caller is told is what a <c>timestamptz</c> keeps.
    /// </summary>
    /// <remarks>
    /// <c>sessions.created_at_utc</c> and <c>expires_at_utc</c> keep microseconds; a <see cref="DateTime" />
    /// keeps 100 ns ticks, and EF never refreshes a tracked value from what the database stored. Left whole,
    /// the sign-in answers an expiry that <c>GET /api/me/session</c> later answers a fraction differently.
    /// The clock ends in <c>…7</c> ticks so truncation and rounding disagree: <c>.1234567</c> must become
    /// <c>.1234560</c>. <c>BeginKeyRotationHandler</c> cuts its instant the same way.
    /// </remarks>
    [Test]
    public async Task HandleAsync_AtASubMicrosecondInstant_StampsAndAnswersTheSessionCutToTheMicrosecond()
    {
        // Arrange
        Fixture fixture = Fixture.Create();
        fixture.Clock.SetUtcNow(new DateTimeOffset(UtcNow.AddTicks(1_234_567)));

        // Act
        LockedSignInOutcome outcome = await fixture.Handler.HandleAsync(new EstablishLockedSessionCommand(Subject));

        // Assert — truncated, not rounded: …1234567 ticks is …1234560. Compared in ticks because a
        // DateTime failure message prints whole seconds and would show two equal values.
        long created = UtcNow.AddTicks(1_234_560).Ticks;
        long expires = (UtcNow.AddTicks(1_234_560) + SessionLifetime).Ticks;
        LockedSignInOutcome.Established established = (LockedSignInOutcome.Established)outcome;
        Session stored = fixture.Sessions.Sessions.Single();
        await Assert.That(stored.CreatedAtUtc.Ticks).IsEqualTo(created);
        await Assert.That(stored.ExpiresAtUtc.Ticks).IsEqualTo(expires);
        await Assert.That(established.Session.ExpiresAtUtc.Ticks).IsEqualTo(expires);
        await Assert.That(established.Handoff.ExpiresAtUtc.Ticks).IsEqualTo(expires);
    }

    /// <summary>
    /// With no schedule filed, the answer says so rather than inventing one.
    /// </summary>
    [Test]
    public async Task HandleAsync_ForAnAccountWithNoSchedule_AnswersNoErasureInstant()
    {
        // Arrange
        Fixture fixture = Fixture.Create();

        // Act
        LockedSignInOutcome outcome = await fixture.Handler.HandleAsync(new EstablishLockedSessionCommand(Subject));

        // Assert
        await Assert.That(((LockedSignInOutcome.Established)outcome).Session.ErasureTakesEffectAtUtc).IsNull();
    }

    /// <summary>
    /// With a schedule filed, the answer carries its instant.
    /// </summary>
    /// <remarks>
    /// No stranger's schedule is seeded: the port is keyed only by its owner and the fake filters by that
    /// key, so a stranger's row here would measure the fake. That half is held under the real policies by
    /// <c>LockedSignInEndpointTests</c>, which files both accounts' schedules a day apart.
    /// </remarks>
    [Test]
    public async Task HandleAsync_ForAnAccountWithASchedule_AnswersItsInstant()
    {
        // Arrange
        Fixture fixture = Fixture.Create();
        ErasureSchedule own = ErasureSchedule.Request(fixture.UserId, UtcNow.AddDays(-2), TimeSpan.FromDays(7));
        fixture.Schedules.Seed(own);

        // Act
        LockedSignInOutcome outcome = await fixture.Handler.HandleAsync(new EstablishLockedSessionCommand(Subject));

        // Assert
        await Assert.That(((LockedSignInOutcome.Established)outcome).Session.ErasureTakesEffectAtUtc)
            .IsEqualTo(own.TakesEffectAtUtc);
        await Assert.That(fixture.Schedules.AddCalls).IsEqualTo(0);
    }

    /// <summary>
    /// The handler's collaborators over one account holding a passkey and a federated credential, and a
    /// stranger holding a federated credential of their own filed before both.
    /// </summary>
    private sealed record Fixture(
        EstablishLockedSessionHandler Handler,
        InMemoryUserRepository Users,
        InMemorySessionRepository Sessions,
        InMemoryErasureScheduleRepository Schedules,
        RecordingUserContextWriter Writer,
        FakeTimeProvider Clock,
        Guid UserId,
        Credential Passkey,
        Credential Federated)
    {
        public static Fixture Create()
        {
            InMemoryUserRepository users = new(new InMemoryTransactionRepository());

            Guid strangerId = Guid.CreateVersion7();
            users.Seed(
                User.CreateWithId(strangerId, "stranger@example.com", IssuedEarlier),
                Credential.CreateFederated(strangerId, Credential.GoogleProvider, StrangerSubject, IssuedEarlier));

            Guid userId = Guid.CreateVersion7();
            Credential passkey = Credential.CreatePasskey(userId, IssuedEarlier);
            Credential federated =
                Credential.CreateFederated(userId, Credential.GoogleProvider, Subject, IssuedEarlier);
            users.Seed(User.CreateWithId(userId, "locked@example.com", IssuedEarlier), passkey);
            users.SeedCredential(federated);

            InMemorySessionRepository sessions = new();
            InMemoryErasureScheduleRepository schedules = new();
            RecordingUserContextWriter writer = new();
            FakeTimeProvider clock = new(new DateTimeOffset(UtcNow));

            return new Fixture(
                new EstablishLockedSessionHandler(users, sessions, schedules, writer, clock),
                users,
                sessions,
                schedules,
                writer,
                clock,
                userId,
                passkey,
                federated);
        }
    }

    /// <summary>The identity last published, or <see cref="Guid.Empty" /> for nobody.</summary>
    private static string IdentityOf(RecordingUserContextWriter writer) =>
        (writer.Published.Count > 0 ? writer.Published[^1] : Guid.Empty).ToString("D");

    /// <summary>Forwards to the fake, logging each publication in the order it happened.</summary>
    private sealed class LoggingUserContextWriter(RecordingUserContextWriter inner, List<string> log)
        : IUserContextWriter
    {
        public void ResolveUser(Guid userId)
        {
            log.Add($"publish {userId:D}");
            inner.ResolveUser(userId);
        }

        public void ResolveBudget(Guid budgetId)
        {
            log.Add($"publish budget {budgetId:D}");
            inner.ResolveBudget(budgetId);
        }
    }

    /// <summary>Forwards to the fake, logging the identity each discovery lookup was entered under.</summary>
    private sealed class ObservingUserRepository(
        InMemoryUserRepository inner,
        RecordingUserContextWriter writer,
        List<string> log) : IUserRepository
    {
        public Task<Guid?> FindUserIdByFederatedCredentialAsync(
            string provider,
            string subject,
            CancellationToken cancellationToken = default)
        {
            log.Add($"id lookup as {IdentityOf(writer)}");
            return inner.FindUserIdByFederatedCredentialAsync(provider, subject, cancellationToken);
        }

        public Task<Credential?> FindFederatedCredentialBySubjectAsync(
            string provider,
            string subject,
            CancellationToken cancellationToken = default)
        {
            log.Add($"lookup as {IdentityOf(writer)}");
            return inner.FindFederatedCredentialBySubjectAsync(provider, subject, cancellationToken);
        }

        public Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A sign-in deletes no account.");
    }

    /// <summary>Forwards to the fake, logging the identity each write was entered under.</summary>
    private sealed class ObservingSessionRepository(
        InMemorySessionRepository inner,
        RecordingUserContextWriter writer,
        List<string> log) : ISessionRepository
    {
        public Task AddAsync(Session session, SessionToken token, CancellationToken cancellationToken = default)
        {
            log.Add($"session add as {IdentityOf(writer)}");
            return inner.AddAsync(session, token, cancellationToken);
        }

        public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            log.Add($"session read as {IdentityOf(writer)}");
            return inner.FindByIdAsync(sessionId, cancellationToken);
        }

        public Task<int> RevokeForCredentialAsync(
            Guid credentialId,
            DateTime revokedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A sign-in revokes nothing.");

        public Task<bool> RevokeAsync(
            Guid sessionId,
            DateTime revokedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A sign-in revokes nothing.");

        // The handler under test establishes; displacing the overwritten cookie's session is the
        // Api-side writer's job, in a scope of its own. A handler reaching for this would be deleting a
        // session under the identity it just published.
        public Task<bool> RemoveAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A sign-in handler displaces nothing.");
    }

    /// <summary>Forwards to the fake, logging the identity each schedule read was entered under.</summary>
    private sealed class ObservingScheduleRepository(
        InMemoryErasureScheduleRepository inner,
        RecordingUserContextWriter writer,
        List<string> log) : IErasureScheduleRepository
    {
        public Task<ErasureSchedule?> FindAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            log.Add($"schedule read as {IdentityOf(writer)}");
            return inner.FindAsync(userId, cancellationToken);
        }

        public Task<ErasureSchedule> AddAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A sign-in files no schedule.");

        public Task<ErasureSchedule?> FindTrackedAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A sign-in reads no tracked schedule.");

        public Task<ScheduleRemoval> RemoveAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A sign-in removes no schedule.");
    }
}
