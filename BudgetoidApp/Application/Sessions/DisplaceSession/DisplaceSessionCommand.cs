namespace Application.Sessions.DisplaceSession;

/// <summary>
/// The digest of the handle a browser presented on the request that has just established a new session.
/// </summary>
/// <param name="TokenHash">
/// SHA-256 of the presented handle — never the handle itself, for the reason
/// <c>AuthenticateSessionCommand</c> carries a digest too.
/// </param>
public sealed record DisplaceSessionCommand(byte[] TokenHash);
