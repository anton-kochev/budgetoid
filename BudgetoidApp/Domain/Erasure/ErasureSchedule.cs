namespace Domain.Erasure;

/// <summary>
/// An account's pending erasure: whose account it is, and the instant the erasure takes effect.
/// </summary>
/// <remarks>
/// <para>
/// <b>A schedule is not an erasure, and nothing about the account changes when one is filed.</b> It
/// is the one act a locked session may perform — a person who lost every passkey and every recovery
/// code releasing the account and the address behind it — and it is filed with a delay rather than
/// carried out at once. The delay is the window an owner would need if the request came from somebody
/// holding a stolen provider account instead. Withdrawing one takes a full session and a fresh passkey
/// assertion, and somebody holding only the provider account can produce neither.
/// </para>
/// <para>
/// <b><see cref="UserId"/> is the primary key, and that is the rule a repeat request rests on.</b> An
/// account holds at most one schedule, so a second request reads back the first one's instant
/// instead of filing a later date beside it, and two racing requests collide on the key rather than
/// on a "check, then insert" with a window between its statements — the lowest layer that can hold
/// the rule <em>declaratively</em>, which is what
/// <see href="../../../docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md">ADR 0002</see>
/// asks for.
/// </para>
/// <para>
/// <b>Two columns, and the absences are decisions.</b> There is no requested-at instant, because the
/// instant that binds is when the account goes, and a second timestamp is a second thing an export, a
/// log or the erasure itself would have to answer for. There is no status and no cancelled-at stamp,
/// because a stamped row is a remnant on an account that asked to be forgotten. A row leaves in one of
/// two ways, and neither leaves anything behind: a withdrawal deletes it, and an erasure carries it away
/// by the cascade from <c>users</c>.
/// </para>
/// <para>
/// <b>The length of the delay is not this type's business.</b> The factory refuses a delay that is
/// not a delay at all — zero is an immediate erasure wearing a schedule's name, and a negative one is
/// overdue the moment it is filed — and nothing more. How long the window is belongs to product
/// policy above the domain, which is the cheaper layer to change.
/// </para>
/// </remarks>
public sealed class ErasureSchedule
{
    private ErasureSchedule()
    {
    }

    /// <summary>The account the erasure is scheduled for, and the primary key: one schedule per account.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The instant the erasure takes effect, in UTC.</summary>
    public DateTime TakesEffectAtUtc { get; private set; }

    /// <summary>
    /// Files an erasure of <paramref name="userId"/>'s account to take effect
    /// <paramref name="delay"/> after <paramref name="requestedAtUtc"/>.
    /// </summary>
    /// <param name="userId">The account whose erasure is requested, as the session resolved it.</param>
    /// <param name="requestedAtUtc">The instant of the request, read from the clock.</param>
    /// <param name="delay">How long the erasure waits; must be greater than zero.</param>
    /// <remarks>
    /// <para>
    /// <b>The refusals are <see cref="ArgumentException"/>s, never a validation error.</b> Nothing
    /// reaching this factory was typed by a person: the account comes from the session, the instant
    /// from the clock and the delay from policy, so a bad value is a defect in a caller and not a 400
    /// to explain.
    /// </para>
    /// <para>
    /// <b>No <see cref="DateTimeKind"/> check</b>, matching <c>KeyRotation.Begin</c> and the rest of
    /// the Domain, which names <see cref="DateTimeKind"/> nowhere. The <c>timestamptz</c> column
    /// underneath refuses a non-UTC <see cref="DateTime"/>, and that is the lower layer and already
    /// declarative. Adding a delay keeps the kind it was given.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="delay"/> is not positive, or the instant it names lies past the last one a
    /// <see cref="DateTime"/> can represent.
    /// </exception>
    public static ErasureSchedule Request(Guid userId, DateTime requestedAtUtc, TimeSpan delay)
    {
        // All-zeros is what an unset member carries, and the one value two accounts reach alike.
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("An erasure must name the account it erases.", nameof(userId));
        }

        // Greater than zero and nothing more: zero would skip the window the delay exists to give the
        // account's owner, and a negative delay would file a schedule already overdue.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);

        // The framework's own overflow refusal, left to throw: an instant past DateTime.MaxValue does
        // not exist, and wrapping or clamping it would tell a person a date nobody chose.
        DateTime takesEffectAtUtc = requestedAtUtc + delay;

        return new ErasureSchedule
        {
            UserId = userId,
            TakesEffectAtUtc = takesEffectAtUtc,
        };
    }
}
