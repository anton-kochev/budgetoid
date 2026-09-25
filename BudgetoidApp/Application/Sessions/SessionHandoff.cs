namespace Application.Sessions;

/// <summary>
/// What an established session hands to the client: the live handle, and the instant the session it
/// names stops being live.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Token"/> is the handle itself and not a digest</b> — the one value in this system
/// that authenticates a request outright. It exists so that an endpoint can write it into the session
/// cookie, and it must reach nothing else: no response record, no log line, no command, no repository.
/// The cookie is <c>HttpOnly</c> precisely so the handle is unreadable by script, and a second copy of
/// it anywhere a client can read works exactly as well as the cookie — so nothing downstream ever
/// fails, and the defect is invisible from every response and every row.
/// </para>
/// <para>
/// <b>The member is called <c>Token</c> on purpose, and the name is a tripwire.</b>
/// <c>SessionTokenSecrecyTests</c> is a census over what route delegates return and it refuses that
/// word anywhere on that surface. So the day somebody returns this type — or something carrying it —
/// from an endpoint, the census names the member rather than shrugging at a value it cannot judge.
/// A gentler name would buy nothing but silence.
/// </para>
/// <para>
/// <b>The expiry travels beside the handle rather than being read separately at the call site.</b>
/// <see cref="SessionHandle.IssuedFor"/> takes both off the session row, so a cookie whose lifetime
/// disagrees with the row it names is unwritable rather than merely unlikely — a browser presenting a
/// handle the server stopped honouring, or dropping one that still works, are both silent.
/// </para>
/// </remarks>
/// <param name="Token">The handle, in the unpadded base64url a request presents it in.</param>
/// <param name="ExpiresAtUtc">The session row's own expiry, never an interval computed anywhere.</param>
public sealed record SessionHandoff(string Token, DateTime ExpiresAtUtc);
