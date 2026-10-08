namespace Application.Erasure.ScheduleErasure;

/// <summary>
/// When the account's scheduled erasure takes effect — the stored instant, the same on every repeat.
/// </summary>
/// <param name="TakesEffectAtUtc">The instant the erasure takes effect, in UTC.</param>
public sealed record ScheduledErasure(DateTime TakesEffectAtUtc);
