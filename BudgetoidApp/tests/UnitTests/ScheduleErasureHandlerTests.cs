using Application.Erasure.ScheduleErasure;
using Domain.Erasure;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// What the handler behind <c>POST /api/me/erasure/schedule</c> files, for whom, and what a repeat
/// answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The delay is asserted as an observable instant against a fixed clock, and never read off
/// <c>ErasurePolicy.Delay</c></b>, for the reason <see cref="EstablishedSessionLifetimeTests" /> gives
/// about the session lifetime: a test taking its expectation from the type under test agrees with
/// whatever that type later decides. Seven days is ASM-010 — the backup retention window — so "the
/// account is gone" and "the last copy is gone" are one window apart rather than two.
/// </para>
/// <para>
/// <b>The identity comes from <c>IUserContext</c> and from nowhere else</b>, and
/// <see cref="ScheduleErasureCommand" /> carries no member to name an account in — the rule
/// <c>EraseAccountCommand</c> states for the immediate erasure. These cases cannot prove the absence of
/// a member, only that the account the context resolves is the one filed and the one read back.
/// </para>
/// <para>
/// The concurrent-insert half of "a repeat never moves the date" — two requests both finding nothing and
/// both adding — is the adapter's, and is pinned by <c>ErasureScheduleRepositoryTests</c> against
/// PostgreSQL. The fake here models only its outcome.
/// </para>
/// </remarks>
public sealed class ScheduleErasureHandlerTests
{
    [Test]
    public async Task HandleAsync_WhenNoScheduleIsStored_FilesOneSevenDaysAfterTheRequest()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        InMemoryErasureScheduleRepository schedules = new();
        ScheduleErasureHandler handler = new(
            schedules, new StubUserContext(userId), new FakeTimeProvider(RequestInstant));

        // Act
        ScheduledErasure scheduled = await handler.HandleAsync(new ScheduleErasureCommand());

        // Assert — the answer, and the row it claims to report, both seven days out.
        DateTime expected = RequestInstant.UtcDateTime.AddDays(7);
        await Assert.That(scheduled.TakesEffectAtUtc).IsEqualTo(expected);
        await Assert.That(schedules.AddCalls).IsEqualTo(1);
        await Assert.That(schedules.Stored.Keys.ToArray()).IsEquivalentTo(new[] { userId });
        await Assert.That(schedules.Stored[userId].TakesEffectAtUtc).IsEqualTo(expected);
    }

    /// <summary>
    /// A repeat answers the stored instant and files nothing, however far the clock has moved.
    /// </summary>
    /// <remarks>
    /// The stored instant is deliberately <b>not</b> "now plus seven days" for the clock this handler
    /// reads: it was filed a day and a half earlier. A handler that recomputed the date and happened to
    /// return it would otherwise be indistinguishable from one that returned the stored row — and a date
    /// that slides forward on every visit is a window that never closes, which is exactly what somebody
    /// holding a stolen provider account would want.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenAScheduleIsStored_ReturnsItUnchangedAndFilesNothing()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        InMemoryErasureScheduleRepository schedules = new();
        DateTime filedAt = RequestInstant.UtcDateTime.AddHours(-36);
        schedules.Seed(ErasureSchedule.Request(userId, filedAt, TimeSpan.FromDays(7)));
        ScheduleErasureHandler handler = new(
            schedules, new StubUserContext(userId), new FakeTimeProvider(RequestInstant));

        // Act
        ScheduledErasure scheduled = await handler.HandleAsync(new ScheduleErasureCommand());

        // Assert
        await Assert.That(scheduled.TakesEffectAtUtc).IsEqualTo(filedAt.AddDays(7));
        await Assert.That(schedules.AddCalls).IsEqualTo(0);
        await Assert.That(schedules.Stored[userId].TakesEffectAtUtc).IsEqualTo(filedAt.AddDays(7));
    }

    /// <summary>
    /// The schedule is filed under the account the context resolves, and answered for it.
    /// </summary>
    /// <remarks>
    /// No stranger's schedule is seeded: the port is keyed only by its owner and the fake filters by that
    /// key, so a stranger's row here would measure the fake. That half is held under the real policies by
    /// <c>ErasureScheduleEndpointTests.ScheduleErasure_LeavesAnotherAccountUntouched</c>.
    /// </remarks>
    [Test]
    public async Task HandleAsync_FilesForTheResolvedAccount()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        InMemoryErasureScheduleRepository schedules = new();
        ScheduleErasureHandler handler = new(
            schedules, new StubUserContext(userId), new FakeTimeProvider(RequestInstant));

        // Act
        ScheduledErasure scheduled = await handler.HandleAsync(new ScheduleErasureCommand());

        // Assert — this account's own date, filed under this account.
        await Assert.That(scheduled.TakesEffectAtUtc).IsEqualTo(RequestInstant.UtcDateTime.AddDays(7));
        await Assert.That(schedules.Stored[userId].TakesEffectAtUtc)
            .IsEqualTo(RequestInstant.UtcDateTime.AddDays(7));
    }

    /// <summary>
    /// When the add loses a race, the answer is the instant the winner stored, not the one this request
    /// computed.
    /// </summary>
    /// <remarks>
    /// Staged by a repository that reports nothing on the find and holds the winner's row on the add —
    /// the order two concurrent first requests reach the two calls in. A handler that answered from the
    /// schedule it built rather than from what the add returned would tell this caller a date the account
    /// does not hold.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAddMeetsARowFiledInBetween_AnswersTheStoredInstant()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        DateTime winnerFiledAt = RequestInstant.UtcDateTime.AddSeconds(-1);
        ErasureSchedule winner = ErasureSchedule.Request(userId, winnerFiledAt, TimeSpan.FromDays(7));
        RaceLosingRepository schedules = new(winner);
        ScheduleErasureHandler handler = new(
            schedules, new StubUserContext(userId), new FakeTimeProvider(RequestInstant));

        // Act
        ScheduledErasure scheduled = await handler.HandleAsync(new ScheduleErasureCommand());

        // Assert
        await Assert.That(scheduled.TakesEffectAtUtc).IsEqualTo(winnerFiledAt.AddDays(7));
        await Assert.That(schedules.AddCalls).IsEqualTo(1);
    }

    /// <summary>
    /// A fixed request instant, whole seconds so nothing here depends on sub-microsecond arithmetic.
    /// </summary>
    private static readonly DateTimeOffset RequestInstant = new(2026, 10, 2, 9, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// A repository in which another request's insert lands between this request's find and its add:
    /// the find answers nothing, the add answers the winner's row.
    /// </summary>
    private sealed class RaceLosingRepository(ErasureSchedule winner) : IErasureScheduleRepository
    {
        public int AddCalls { get; private set; }

        public Task<ErasureSchedule?> FindAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ErasureSchedule?>(null);

        public Task<ErasureSchedule> AddAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default)
        {
            AddCalls++;
            return Task.FromResult(winner);
        }
    }
}
