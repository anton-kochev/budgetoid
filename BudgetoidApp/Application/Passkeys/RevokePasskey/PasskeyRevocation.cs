namespace Application.Passkeys.RevokePasskey;

/// <summary>
/// What a completed revocation has to say for itself: the passkey is gone, and this many sessions
/// went with it.
/// </summary>
/// <remarks>
/// <para>
/// A result type rather than a bare <see langword="int" /> so the number reaching the wire is named
/// where it is produced. The removal itself is not reported — a caller that got a 200 from a route
/// whose whole purpose is the removal already knows.
/// </para>
/// <para>
/// It names no credential and no account. The caller supplied the id it asked about, so echoing one
/// back adds nothing, and an id in a response body is an id in a client log.
/// </para>
/// </remarks>
/// <param name="SessionsEnded">
/// How many sessions the revoked passkey had opened and no longer holds.
/// </param>
public sealed record PasskeyRevocation(int SessionsEnded);
