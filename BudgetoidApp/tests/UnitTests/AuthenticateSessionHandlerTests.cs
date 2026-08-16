using Application.Sessions.AuthenticateSession;
using Application.Users.EnsureUser;
using Domain.Budgets;
using Domain.Sessions;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The order <see cref="AuthenticateSessionHandler" /> does its two reads and its two publications in,
/// and what it refuses to publish when a handle names a sign-in that has ended.
/// </summary>
/// <remarks>
/// <para>
/// <b>Successor in spirit to <c>UserProvisioningWriterTests</c>, and it asserts on a collaboration for
/// that file's reason.</b> The state this handler leaves behind is correct under every ordering a
/// reader might write, because the values it publishes do not depend on the order it publishes them
/// in. The <em>order</em> is the rule: <c>CurrentUserWriter.ResolveUser</c> clears the ambient budget,
/// so a budget published before an identity is a budget the rest of the request does not have, and
/// <c>sessions</c> is policed by <c>user_isolation</c>, so a read issued before the identity meets
/// <c>''::uuid</c> and raises <c>22P02</c>. Neither failure is visible in the resulting values, so
/// nothing but a test watching the sequence can tell the two designs apart.
/// </para>
/// <para>
/// <b>Why here rather than beside <c>SessionCookieAuthenticationTests</c>.</b> That suite drives the
/// whole ordering through PostgreSQL and is the proof that the rule is real — a swap there fails
/// <c>22P02</c> on the live role, which no fake can produce. What it cannot do is fail for the
/// <em>right reason</em> when the ordering is only half wrong: an identity published before the exempt
/// discovery read breaks nothing in the database, changes no answer today, and is exactly the edit that
/// makes the exemption on <c>session_tokens</c> pointless. These tests read the sequence directly, so
/// each half of ADR 0019 has its own assertion.
/// </para>
/// <para>
/// <b>The publication that survives a refusal is asserted rather than corrected.</b> An unknown handle
/// publishes nothing; a handle naming a session that has ended publishes the identity and stops there.
/// That residue is deliberate — the handler's own remarks argue it — so it is pinned in both
/// directions: a change that starts publishing on the unknown path, or one that starts publishing a
/// tenant beside an ended session, reddens here rather than in whichever route notices first.
/// </para>
/// </remarks>
public sealed class AuthenticateSessionHandlerTests
{
    [Test]
    public async Task HandleAsync_WithALiveHandle_PublishesTheAccountAndItsBudget()
    {
        // Arrange — one account, one budget, one live session, and the handle that names it.
        var userId = Guid.CreateVersion7();
        Session session = LiveSessionFor(userId);
        byte[] token = TokenBytes(0x11);
        Budget budget = Budget.CreateDefault(userId, UtcNow());
        var writer = new RecordingUserContextWriter();
        AuthenticateSessionHandler handler = HandlerFor(writer, [session], [SessionToken.For(session, token)], [budget]);

        // Act
        AuthenticatedSession? authenticated =
            await handler.HandleAsync(new AuthenticateSessionCommand(SessionToken.HashOf(token)));

        // Assert — the answer names the sign-in the handle was filed against, not merely some session
        // on the account: the session id is what lets a request end its own session without naming one,
        // and a handler answering with the account's first session would sign a laptop out from a phone.
        await Assert.That(authenticated).IsNotNull();
        await Assert.That(authenticated!.UserId).IsEqualTo(userId);
        await Assert.That(authenticated.SessionId).IsEqualTo(session.Id);
        await Assert.That(authenticated.IsLive).IsTrue();

        // Derived from the credential that opened the session and never chosen here. A passkey opens a
        // Full session; asserting it is what keeps a handler that hard-coded a kind from passing.
        await Assert.That(authenticated.Kind).IsEqualTo(SessionKind.Full);

        // Both publications happened, once each, and named this account and its budget.
        await Assert.That(writer.Published.Count).IsEqualTo(1);
        await Assert.That(writer.Published[0]).IsEqualTo(userId);
        await Assert.That(writer.PublishedBudgets.Count).IsEqualTo(1);
        await Assert.That(writer.PublishedBudgets[0]).IsEqualTo(budget.Id);
    }

    /// <summary>
    /// That the exempt read runs before anybody is published and both policed reads run after — the
    /// whole of ADR 0019, checked one statement at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three assertions because the ordering has three ways to be wrong and no two of them fail
    /// alike.</b> Publishing above the discovery read is harmless today and retires the reason
    /// <c>session_tokens</c> is exempt at all. Publishing below the <c>sessions</c> read is
    /// <c>22P02</c> on every authenticated request in the product. Publishing between them, but reading
    /// the budget first, is <c>22P02</c> on that one statement. A single "the order was right"
    /// assertion would report all three as the same failure.
    /// </para>
    /// <para>
    /// <see cref="Guid.Empty" /> is what an unpublished identity reaches a policy as — <c>''::uuid</c> —
    /// so it is the expected value on the exempt read and the failure on both policed ones. The two
    /// readings are opposite on purpose and are argued on each fake's own member.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_ReadsTheExemptTableAnonymouslyAndThePolicedOnesAfterPublishing()
    {
        // Arrange — every read armed to snapshot the identity it would have run under.
        var userId = Guid.CreateVersion7();
        Session session = LiveSessionFor(userId);
        byte[] token = TokenBytes(0x11);
        var writer = new RecordingUserContextWriter();
        var tokens = new InMemorySessionTokenRepository();
        var sessions = new InMemorySessionRepository();
        var budgets = new InMemoryBudgetRepository();
        tokens.Seed(SessionToken.For(session, token));
        await sessions.AddAsync(session);
        budgets.Seed(Budget.CreateDefault(userId, UtcNow()));
        tokens.ObservePublicationsDuring(writer);
        sessions.ObservePublicationsDuring(writer);
        budgets.ObservePublicationsDuring(writer);
        var handler = new AuthenticateSessionHandler(tokens, sessions, budgets, writer, FixedClock());

        // Act
        AuthenticatedSession? authenticated =
            await handler.HandleAsync(new AuthenticateSessionCommand(SessionToken.HashOf(token)));

        // Assert — that the path ran to the end first. A handler that refused halfway would leave the
        // later reads unmade, and "the read never happened" satisfies "the read happened after the
        // publication" vacuously.
        await Assert.That(authenticated).IsNotNull();

        // The discovery read, on the exempt table, naming nobody: the request arrives holding a cookie
        // and there is no account to scope by until this has answered.
        await Assert.That(tokens.IdentityWhenFindByTokenHashWasEntered.Count).IsEqualTo(1);
        await Assert.That(tokens.IdentityWhenFindByTokenHashWasEntered[0]).IsEqualTo(Guid.Empty);

        // The policed reads, both under the identity the discovery read produced.
        await Assert.That(sessions.IdentityWhenFindByIdWasEntered.Count).IsEqualTo(1);
        await Assert.That(sessions.IdentityWhenFindByIdWasEntered[0]).IsEqualTo(userId);
        await Assert.That(budgets.IdentityWhenFindFirstWasEntered.Count).IsEqualTo(1);
        await Assert.That(budgets.IdentityWhenFindFirstWasEntered[0]).IsEqualTo(userId);
    }

    /// <summary>
    /// That the identity is published before the ambient budget, whatever else moves.
    /// </summary>
    /// <remarks>
    /// The test above cannot say this. It watches the reads, and both orderings of the two
    /// <em>publications</em> leave every read under the same identity — so a handler that named the
    /// budget first and the user second would satisfy it exactly. What refuses that is the sequence
    /// itself: <c>CurrentUserWriter.ResolveUser</c> clears the ambient budget as its first act, placed
    /// there so the next person to publish an identity cannot forget it, which means a budget published
    /// before one is silently thrown away and every budget-scoped statement afterwards meets an
    /// unresolved budget.
    /// </remarks>
    [Test]
    public async Task HandleAsync_PublishesTheIdentityBeforeTheAmbientBudget()
    {
        // Arrange — the recording writer underneath, so the ids are still kept where every other test
        // here reads them; the sequence log is the only thing this wrapper adds.
        var userId = Guid.CreateVersion7();
        Session session = LiveSessionFor(userId);
        byte[] token = TokenBytes(0x11);
        var recorder = new RecordingUserContextWriter();
        var writer = new SequencingUserContextWriter(recorder);
        AuthenticateSessionHandler handler = HandlerFor(
            writer,
            [session],
            [SessionToken.For(session, token)],
            [Budget.CreateDefault(userId, UtcNow())]);

        // Act
        AuthenticatedSession? authenticated =
            await handler.HandleAsync(new AuthenticateSessionCommand(SessionToken.HashOf(token)));

        // Assert — the whole sequence rather than its tail, because this handler publishes exactly
        // twice: unlike the provisioning path, there is no conflict arm here that publishes again, so
        // a third publication is a change somebody has to explain rather than noise to skip past.
        await Assert.That(authenticated).IsNotNull();
        await Assert.That(string.Join(", ", writer.Sequence)).IsEqualTo("ResolveUser, ResolveBudget");
    }

    [Test]
    public async Task HandleAsync_WithAnUnknownHandle_PublishesNothing()
    {
        // Arrange — a real sign-in exists, so "published nothing" is a verdict on the handle that was
        // presented rather than on a store with nobody in it.
        var userId = Guid.CreateVersion7();
        Session session = LiveSessionFor(userId);
        byte[] stored = TokenBytes(0x11);
        var writer = new RecordingUserContextWriter();
        var tokens = new InMemorySessionTokenRepository();
        var sessions = new InMemorySessionRepository();
        var budgets = new InMemoryBudgetRepository();
        tokens.Seed(SessionToken.For(session, stored));
        await sessions.AddAsync(session);
        budgets.Seed(Budget.CreateDefault(userId, UtcNow()));
        sessions.ObservePublicationsDuring(writer);
        var handler = new AuthenticateSessionHandler(tokens, sessions, budgets, writer, FixedClock());

        // Act — a well-formed digest of a token nobody was ever issued.
        AuthenticatedSession? authenticated =
            await handler.HandleAsync(new AuthenticateSessionCommand(SessionToken.HashOf(TokenBytes(0x99))));

        // Assert — nothing came back, and nothing was named. The identity is the value every policed
        // statement for the rest of the request is decided by, so publishing one on the strength of a
        // handle that matched no row would hand an unauthenticated caller a tenant to be refused from.
        await Assert.That(authenticated).IsNull();
        await Assert.That(writer.Published.Count).IsEqualTo(0);
        await Assert.That(writer.PublishedBudgets.Count).IsEqualTo(0);

        // And the policed read was never issued at all — which in production is the difference between
        // a refusal and a 22P02 on a connection naming nobody.
        await Assert.That(sessions.IdentityWhenFindByIdWasEntered.Count).IsEqualTo(0);
        await Assert.That(budgets.FindFirstCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithARevokedSession_PublishesNoAmbientBudget()
    {
        // Arrange — one account, one session, revoked a minute before the fixed clock reads.
        var userId = Guid.CreateVersion7();
        Session session = LiveSessionFor(userId);
        session.Revoke(UtcNow().AddMinutes(-1));
        byte[] token = TokenBytes(0x11);
        var writer = new RecordingUserContextWriter();
        AuthenticateSessionHandler handler = HandlerFor(
            writer,
            [session],
            [SessionToken.For(session, token)],
            [Budget.CreateDefault(userId, UtcNow())]);

        // Act
        AuthenticatedSession? authenticated =
            await handler.HandleAsync(new AuthenticateSessionCommand(SessionToken.HashOf(token)));

        // Assert — an ended session is answered, not hidden: null here would make signing out twice
        // indistinguishable from presenting a handle nobody ever issued, and the second sign-out would
        // be 401 with a dead cookie left on the client forever.
        await Assert.That(authenticated).IsNotNull();
        await Assert.That(authenticated!.IsLive).IsFalse();
        await Assert.That(authenticated.SessionId).IsEqualTo(session.Id);

        // The identity, and no tenant. That absence is what makes a route carrying
        // AcceptsEndedSessionAttribute structurally unable to reach budget content: the first
        // budget-scoped statement under it meets an unresolved budget and throws, rather than being
        // scoped to a stranger and quietly matching nothing.
        await Assert.That(writer.Published.Count).IsEqualTo(1);
        await Assert.That(writer.Published[0]).IsEqualTo(userId);
        await Assert.That(writer.PublishedBudgets.Count).IsEqualTo(0);
    }

    /// <summary>
    /// That expiry ends a session on its own, with nobody having revoked anything.
    /// </summary>
    /// <remarks>
    /// Separate from the revoked case rather than folded into it, because a handler consulting only
    /// <c>RevokedAtUtc</c> passes that one and reports a session that lapsed months ago as live. The
    /// two are the two halves of <see cref="Session.IsActiveAt" /> and neither covers the other.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAnExpiredSession_PublishesNoAmbientBudget()
    {
        // Arrange — never revoked, and expired an hour before the fixed clock reads.
        var userId = Guid.CreateVersion7();
        Credential credential = Credential.CreatePasskey(userId, UtcNow().AddHours(-3));
        Session session = Session.Establish(credential, UtcNow().AddHours(-2), UtcNow().AddHours(-1));
        byte[] token = TokenBytes(0x11);
        var writer = new RecordingUserContextWriter();
        AuthenticateSessionHandler handler = HandlerFor(
            writer,
            [session],
            [SessionToken.For(session, token)],
            [Budget.CreateDefault(userId, UtcNow())]);

        // Act
        AuthenticatedSession? authenticated =
            await handler.HandleAsync(new AuthenticateSessionCommand(SessionToken.HashOf(token)));

        // Assert
        await Assert.That(authenticated).IsNotNull();
        await Assert.That(session.RevokedAtUtc).IsNull();
        await Assert.That(authenticated!.IsLive).IsFalse();
        await Assert.That(writer.Published.Count).IsEqualTo(1);
        await Assert.That(writer.PublishedBudgets.Count).IsEqualTo(0);
    }

    /// <summary>
    /// That a handle naming a session this request cannot see is refused, and refused after the
    /// identity it named has already been published.
    /// </summary>
    /// <remarks>
    /// Nearly unreachable and not dead code, for the reason the handler states: the composite foreign
    /// key makes a token naming another account's session unstorable, so what is left is the race — an
    /// erasure, or a revocation of the establishing credential, committing between the two reads. The
    /// honest answer is that the handle now names nothing, and that is also the answer which fails
    /// closed if a later path finds a second way here.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAHandleNamingASessionThatIsGone_PublishesNoAmbientBudget()
    {
        // Arrange — the token is filed, its session is not, and a DIFFERENT account's session is
        // present. The bystander is the arrangement: with an empty store, a lookup that ignored the id
        // and answered with whatever it held would return null too, and this test would pass over the
        // defect it is written for.
        var userId = Guid.CreateVersion7();
        var strangerId = Guid.CreateVersion7();
        Session gone = LiveSessionFor(userId);
        Session bystander = LiveSessionFor(strangerId);
        byte[] token = TokenBytes(0x11);
        var writer = new RecordingUserContextWriter();
        AuthenticateSessionHandler handler = HandlerFor(
            writer,
            [bystander],
            [SessionToken.For(gone, token)],
            [Budget.CreateDefault(userId, UtcNow()), Budget.CreateDefault(strangerId, UtcNow())]);

        // Act
        AuthenticatedSession? authenticated =
            await handler.HandleAsync(new AuthenticateSessionCommand(SessionToken.HashOf(token)));

        // Assert — no answer, and no tenant. The identity is published and stays published, which the
        // handler argues for explicitly: it names the account whose handle really did match, on a
        // request that goes on to be refused.
        await Assert.That(authenticated).IsNull();
        await Assert.That(writer.Published.Count).IsEqualTo(1);
        await Assert.That(writer.Published[0]).IsEqualTo(userId);
        await Assert.That(writer.PublishedBudgets.Count).IsEqualTo(0);
    }

    /// <summary>
    /// That a session naming an account with no budget fails loudly rather than answering with no
    /// tenant.
    /// </summary>
    /// <remarks>
    /// An account and its default budget land in one <c>SaveChanges</c>, so an account without one is a
    /// state nothing produces; meeting it here means the invariant broke. What this refuses is the
    /// recovery. Carrying on with no ambient budget would answer the request against an unresolved
    /// tenant, and inventing one — the first budget in the table, say — would answer it with somebody
    /// else's rows, which is the failure on this whole path that nobody would notice.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAnAccountHoldingNoBudget_ThrowsRatherThanPublishingATenant()
    {
        // Arrange — a live session on an account holding no budget, and a bystander who does hold one,
        // so that "answered with somebody else's tenant" has something to be answered with.
        var userId = Guid.CreateVersion7();
        var strangerId = Guid.CreateVersion7();
        Session session = LiveSessionFor(userId);
        byte[] token = TokenBytes(0x11);
        var writer = new RecordingUserContextWriter();
        AuthenticateSessionHandler handler = HandlerFor(
            writer,
            [session],
            [SessionToken.For(session, token)],
            [Budget.CreateDefault(strangerId, UtcNow())]);

        // Act
        InvalidOperationException exception = await ThrowsInvalidOperationExceptionAsync(
            () => handler.HandleAsync(new AuthenticateSessionCommand(SessionToken.HashOf(token))));

        // Assert — it threw, and it published no tenant on the way out. The second half is the one that
        // matters: a handler that published the bystander's budget and then threw would leave the
        // ambient budget naming a stranger for whatever catches the exception.
        await Assert.That(exception.Message).Contains("budget");
        await Assert.That(writer.PublishedBudgets.Count).IsEqualTo(0);
    }

    /// <summary>The instant the clock is fixed at; every session here is arranged around it.</summary>
    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static FakeTimeProvider FixedClock() => new(new DateTimeOffset(UtcNow()));

    /// <summary>
    /// A session opened a minute ago by a passkey and live for the next hour.
    /// </summary>
    /// <remarks>
    /// A <b>passkey</b> credential rather than a federated one, because <see cref="Session.Establish" />
    /// derives the kind from the credential's type: a federated sign-in is
    /// <see cref="SessionKind.Locked" />, and a test asserting the kind against a fixture that could
    /// only ever produce one value would be asserting the fixture.
    /// </remarks>
    private static Session LiveSessionFor(Guid userId) => Session.Establish(
        Credential.CreatePasskey(userId, UtcNow().AddHours(-1)),
        UtcNow().AddMinutes(-1),
        UtcNow().AddHours(1));

    /// <summary>
    /// A token of <see cref="SessionToken.TokenLength" /> bytes, every one of them
    /// <paramref name="fill" />.
    /// </summary>
    /// <remarks>
    /// The fill byte is required rather than defaulted: the digest is the identity of a stored handle,
    /// so two identical tokens would be one row and a test holding two handles would have nothing to
    /// choose wrongly between.
    /// </remarks>
    private static byte[] TokenBytes(byte fill) => [.. Enumerable.Repeat(fill, SessionToken.TokenLength)];

    /// <summary>
    /// The handler with everything seeded, for the tests that do not read the ordering off the fakes.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> arm <c>ObservePublicationsDuring</c>. The ordering tests arrange
    /// their own fakes line by line, so a reader of one of those never has to leave it to find out what
    /// is being watched — and the tests that are about published values are not paying for a snapshot
    /// nothing reads.
    /// </remarks>
    private static AuthenticateSessionHandler HandlerFor(
        IUserContextWriter writer,
        IEnumerable<Session> sessions,
        IEnumerable<SessionToken> tokens,
        IEnumerable<Budget> budgets)
    {
        var sessionRepository = new InMemorySessionRepository();
        var tokenRepository = new InMemorySessionTokenRepository();
        var budgetRepository = new InMemoryBudgetRepository();

        foreach (Session session in sessions)
        {
            // The fake's AddAsync completes synchronously, so nothing is being fired and forgotten; the
            // port is async and this is its one shape.
            sessionRepository.AddAsync(session).GetAwaiter().GetResult();
        }

        foreach (SessionToken token in tokens)
        {
            tokenRepository.Seed(token);
        }

        foreach (Budget budget in budgets)
        {
            budgetRepository.Seed(budget);
        }

        return new AuthenticateSessionHandler(
            tokenRepository,
            sessionRepository,
            budgetRepository,
            writer,
            FixedClock());
    }

    /// <summary>
    /// Records which member of <see cref="IUserContextWriter" /> was called, in order, and passes each
    /// call straight on to the recorder underneath.
    /// </summary>
    /// <remarks>
    /// <c>RecordingUserContextWriter</c> keeps the ids in two lists, one per member, which is what its
    /// callers need and what makes the relative order of two <em>different</em> members unrecoverable
    /// from it. Rather than widen a fake five other test classes depend on, this wraps it — the shape
    /// <c>UserProvisioningWriterTests.SpyingUserContextWriter</c> already uses over the real writer.
    /// </remarks>
    private sealed class SequencingUserContextWriter(RecordingUserContextWriter inner) : IUserContextWriter
    {
        private readonly List<string> _sequence = [];

        /// <summary>Every publication this request made, oldest first, by member name.</summary>
        public IReadOnlyList<string> Sequence => _sequence;

        public void ResolveUser(Guid userId)
        {
            _sequence.Add(nameof(ResolveUser));
            inner.ResolveUser(userId);
        }

        public void ResolveBudget(Guid budgetId)
        {
            _sequence.Add(nameof(ResolveBudget));
            inner.ResolveBudget(budgetId);
        }
    }

    /// <summary>
    /// Runs <paramref name="action" /> and returns the refusal it threw, failing loudly when it threw
    /// nothing. Shaped like <c>ExportDataHandlerTests.ThrowsInvalidOperationExceptionAsync</c>, which is
    /// how this suite asserts a thrown exception.
    /// </summary>
    private static async Task<InvalidOperationException> ThrowsInvalidOperationExceptionAsync(
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected InvalidOperationException.");
    }
}
