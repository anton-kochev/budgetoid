using Domain.Erasure;

namespace Application.Erasure;

/// <summary>
/// The product's policy about erasures it defers, in one place so the path that files one and the
/// paths that later read or act on it agree.
/// </summary>
public static class ErasurePolicy
{
    /// <summary>
    /// How long a scheduled erasure waits before it takes effect — ASM-010's seven days.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Seven days because it is the backup retention window</b>, so "the account is gone" and "the
    /// last copy is gone" are one window apart rather than two. Change one without the other and that
    /// sentence stops being true without anything going red here.
    /// </para>
    /// <para>
    /// <b>Here rather than in Domain</b>, for <see cref="Sessions.SessionPolicy.Lifetime"/>'s reason:
    /// <see cref="ErasureSchedule.Request"/> refuses a delay that is not a delay and nothing more,
    /// because the length of the window is product policy and ADR 0002 keeps policy above the
    /// invariants, where it is cheaper to change.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Delay = TimeSpan.FromDays(7);
}
