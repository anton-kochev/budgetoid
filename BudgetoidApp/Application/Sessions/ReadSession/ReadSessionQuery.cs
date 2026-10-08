namespace Application.Sessions.ReadSession;

/// <summary>
/// Asks what the session the request authenticated with is: its kind, its expiry, and the account's own
/// pending erasure.
/// </summary>
/// <param name="SessionId">The session to describe — filled from the request's own <c>session_id</c>
/// claim, exactly as <see cref="RevokeSession.RevokeSessionCommand" /> is, and never from the body or the
/// route.</param>
/// <remarks>
/// <b>No user id, and none may be added.</b> The schedule read is the account the request is
/// authenticated as, off <see cref="Abstractions.IUserContext.UserId" />; a member here would be an
/// account a caller could choose.
/// </remarks>
public sealed record ReadSessionQuery(Guid SessionId);
