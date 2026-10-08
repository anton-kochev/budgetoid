namespace Application.Abstractions;

/// <summary>
/// Reads the clock at the precision the database keeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>A <c>timestamptz</c> keeps microseconds; a <see cref="DateTime"/> keeps ticks, ten to the
/// microsecond.</b> A handler that answers an instant it also stores, computed from a whole clock read,
/// answers a seventh fractional digit the stored row does not carry — up to 0.9µs away from what every
/// later read returns for the same row — and EF never refreshes a tracked value from what the database
/// kept. Only a clock finer than a microsecond shows it: .NET's clock on Linux is, macOS's is not, so the
/// disagreement is invisible on a Mac and real in the container.
/// </para>
/// <para>
/// <b>Truncated, never rounded, and before the value is used.</b> The value handed to the save is then
/// one the column holds exactly, so what the handler answers is what was stored whichever way the
/// provider would have rounded on the write. It is a whole-tick subtraction, so the
/// <see cref="DateTime.Kind"/> stays <see cref="DateTimeKind.Utc"/>.
/// </para>
/// </remarks>
internal static class TimeProviderExtensions
{
    /// <summary>
    /// The current UTC instant, cut to the microsecond.
    /// </summary>
    public static DateTime GetUtcNowToTheMicrosecond(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        DateTime clock = timeProvider.GetUtcNow().UtcDateTime;
        return clock.AddTicks(-(clock.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
