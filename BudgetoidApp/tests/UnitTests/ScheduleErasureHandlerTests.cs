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
    /// A clock read finer than a microsecond is cut to the microsecond before the date is computed, and
    /// the answer and the filed row agree on it.
    /// </summary>
    /// <remarks>
    /// <c>erasure_schedules.takes_effect_at_utc</c> is a <c>timestamptz</c> and keeps microseconds; a
    /// <see cref="DateTime" /> keeps 100 ns ticks. Left whole, the first request answers a seventh
    /// fractional digit the stored row does not hold, so a repeat — or the loser of a race, answered with
    /// the winner's row read back — reports a different instant for the same schedule. The clock here ends
    /// in <c>…7</c> ticks so that truncation and rounding disagree: <c>.1234567</c> must become
    /// <c>.1234560</c>, never <c>.1234570</c>. <c>BeginKeyRotationHandler</c> cuts its instant the same way.
    /// </remarks>
    [Test]
    public async Task HandleAsync_AtASubMicrosecondInstant_AnswersAndFilesItCutToTheMicrosecond()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        InMemoryErasureScheduleRepository schedules = new();
        DateTimeOffset subMicrosecond = RequestInstant.AddTicks(1_234_567);
        ScheduleErasureHandler handler = new(
            schedules, new StubUserContext(userId), new FakeTimeProvider(subMicrosecond));

        // Act
        ScheduledErasure scheduled = await handler.HandleAsync(new ScheduleErasureCommand());

        // Assert — truncated, not rounded: …1234567 ticks is …1234560, then seven days out. Compared in
        // ticks because a DateTime failure message prints whole seconds and would show two equal values.
        long expected = RequestInstant.UtcDateTime.AddTicks(1_234_560).AddDays(7).Ticks;
        await Assert.That(scheduled.TakesEffectAtUtc.Ticks).IsEqualTo(expected);
        await Assert.That(schedules.Stored[userId].TakesEffectAtUtc.Ticks).IsEqualTo(expected);
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
    /// A fixed request instant in whole seconds; only the sub-microsecond case adds ticks to it.
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

        public Task<ErasureSchedule?> FindTrackedAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Scheduling reads no tracked row.");

        public Task<ScheduleRemoval> RemoveAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Scheduling removes nothing.");
    }
}
