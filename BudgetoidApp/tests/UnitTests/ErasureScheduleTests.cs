using Domain.Erasure;

namespace UnitTests;

/// <summary>
/// Covers what <see cref="ErasureSchedule.Request" /> decides on its own: whose account the schedule
/// belongs to, and the instant it takes effect. Whether an account already holds one is the
/// database's rule — <c>ErasureScheduleSchemaTests</c> — because a factory cannot know whether a row
/// exists.
/// </summary>
/// <remarks>
/// Every refusal here is an <see cref="ArgumentException" />, never a validation error: nothing
/// reaching this factory was typed by a person. The account comes from the session, the instant from
/// the clock and the delay from policy, so a bad value is a defect in a caller and not a 400 to
/// explain.
/// </remarks>
public sealed class ErasureScheduleTests
{
    [Test]
    public async Task Request_WithAValidRequest_TakesEffectTheDelayAfterTheRequest()
    {
        // Arrange — a delay that is not the product's seven days, so a factory that ignored its
        // argument and added a constant of its own cannot pass.
        Guid userId = Guid.CreateVersion7();
        TimeSpan delay = TimeSpan.FromHours(3) + TimeSpan.FromMinutes(17);

        // Act
        ErasureSchedule schedule = ErasureSchedule.Request(userId, RequestedAt(), delay);

        // Assert
        await Assert.That(schedule.UserId).IsEqualTo(userId);
        await Assert.That(schedule.TakesEffectAtUtc).IsEqualTo(new DateTime(2026, 10, 2, 12, 17, 15, DateTimeKind.Utc));
    }

    [Test]
    public async Task Request_WithAValidRequest_KeepsTheInstantInUtc()
    {
        // Arrange — the column is timestamptz, which refuses a DateTime of any other kind, so an
        // instant that lost its kind on the way through would be a 500 at the save.
        Guid userId = Guid.CreateVersion7();

        // Act
        ErasureSchedule schedule = ErasureSchedule.Request(userId, RequestedAt(), TimeSpan.FromDays(7));

        // Assert
        await Assert.That(schedule.TakesEffectAtUtc.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    public async Task Request_WithTheSmallestPositiveDelay_IsAccepted()
    {
        // Arrange — the boundary of "greater than zero". The domain owns that a delay is a delay and
        // nothing about how long it must be: the seven days are product policy and live above it.
        Guid userId = Guid.CreateVersion7();

        // Act
        ErasureSchedule schedule = ErasureSchedule.Request(userId, RequestedAt(), TimeSpan.FromTicks(1));

        // Assert
        await Assert.That(schedule.TakesEffectAtUtc).IsEqualTo(RequestedAt().AddTicks(1));
    }

    [Test]
    public async Task Request_WithAnEmptyUserId_Throws()
    {
        // Act — all-zeros is what an unset member carries, and the one value two accounts reach alike.
        Exception refusal = Refusal(
            () => ErasureSchedule.Request(Guid.Empty, RequestedAt(), TimeSpan.FromDays(7)));

        // Assert
        await Assert.That(refusal).IsAssignableTo<ArgumentException>();
        await Assert.That(((ArgumentException)refusal).ParamName).IsEqualTo("userId");
    }

    [Test]
    [Arguments(0L)]
    [Arguments(-1L)]
    [Arguments(-6_048_000_000_000L)]
    public async Task Request_WithADelayThatIsNotPositive_Throws(long delayTicks)
    {
        // Arrange — zero is an immediate erasure wearing a schedule's name, and a negative delay a
        // schedule already overdue the moment it is filed. Both would skip the window the delay exists
        // to give the account's owner. The last value is minus seven days: the policy constant with
        // its sign flipped.
        TimeSpan delay = TimeSpan.FromTicks(delayTicks);

        // Act
        Exception refusal = Refusal(
            () => ErasureSchedule.Request(Guid.CreateVersion7(), RequestedAt(), delay));

        // Assert
        await Assert.That(refusal).IsAssignableTo<ArgumentException>();
        await Assert.That(((ArgumentException)refusal).ParamName).IsEqualTo("delay");
    }

    [Test]
    public async Task Request_WithADelayPastTheLastRepresentableInstant_Throws()
    {
        // Arrange — one day short of the end of DateTime, then a seven-day delay. The instant cannot
        // exist, so the factory must refuse rather than wrap or clamp. The parameter name is the
        // framework's, so only the type is pinned.
        DateTime requestedAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).AddDays(-1);

        // Act
        Exception refusal = Refusal(
            () => ErasureSchedule.Request(Guid.CreateVersion7(), requestedAt, TimeSpan.FromDays(7)));

        // Assert
        await Assert.That(refusal).IsTypeOf<ArgumentOutOfRangeException>();
    }

    private static DateTime RequestedAt() => new(2026, 10, 2, 9, 0, 15, DateTimeKind.Utc);

    private static Exception Refusal(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected a refusal.");
    }
}
