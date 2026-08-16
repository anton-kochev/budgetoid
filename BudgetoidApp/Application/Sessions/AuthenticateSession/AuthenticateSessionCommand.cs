namespace Application.Sessions.AuthenticateSession;

/// <summary>
/// Asks which account and which session, if any, a presented handle names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The digest, never the token, and that is a boundary rather than a preference.</b> The raw handle
/// is decoded and hashed where it is read off the wire and stops there; from here down the system holds
/// a value that names a row and opens nothing. So no port, no command and no log statement below this
/// line has a member a live token could travel through — the property
/// <c>ISessionTokenRepository.FindByTokenHashAsync</c> states from the other end.
/// </para>
/// <para>
/// A <see cref="byte"/> array rather than a <see cref="ReadOnlyMemory{T}"/>, because the one thing done
/// with it is binding a <c>bytea</c> parameter, which is what
/// <c>Domain.Sessions.SessionToken.HashOf</c> already hands back.
/// </para>
/// </remarks>
public sealed record AuthenticateSessionCommand(byte[] TokenHash);
