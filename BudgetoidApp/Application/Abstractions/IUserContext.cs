namespace Application.Abstractions;

/// <summary>
/// The person the current request is being served for. Read-only on purpose: the capability to say
/// who that is belongs to the one collaborator that resolves it, not to everyone that needs to know.
/// </summary>
public interface IUserContext
{
    /// <summary>
    /// The signed-in user, or <see langword="null"/> when the current request has not resolved one —
    /// discovery runs before any identity exists, and infrastructure scopes such as health checks
    /// never have a request principal at all. This is the accessor for callers that must tolerate
    /// that state; <see cref="UserId"/> stays the strict one.
    /// </summary>
    Guid? ResolvedUserId { get; }

    /// <summary>
    /// The signed-in user, throwing when the current request has not resolved one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The signed-in user has not been resolved.</exception>
    // Implemented here rather than left to implementers, for the same reason IBudgetContext gives:
    // this form is definitionally ResolvedUserId with null rejected, so two hand-written accessors
    // could name different users and nothing would fail.
    Guid UserId => ResolvedUserId
        ?? throw new InvalidOperationException("The signed-in user for the current request has not been resolved.");
}
