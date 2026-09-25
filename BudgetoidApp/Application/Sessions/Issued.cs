namespace Application.Sessions;

/// <summary>
/// What a path that may establish a session returns: what the caller is told, and — when a session was
/// opened — the handle it is presented by, side by side and never folded together.
/// </summary>
/// <typeparam name="TResult">
/// What the path has to say for itself. It is the type the endpoint maps onto the wire, and it is
/// deliberately unaware that a handle exists.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>Beside the result and never inside it.</b> <c>EstablishedSession</c>,
/// <c>RedeemedRecoveryCode</c> and <c>ReestablishedSession</c> describe what the response says, and
/// each of them is one member away from being serialized whole by an endpoint that stopped mapping —
/// <c>GET /api/me/recovery-codes</c> already answers with an application record directly. A handle
/// inside one of those would then be readable by every script on the page and would authenticate
/// exactly as well as the cookie, so nothing downstream would fail and no row would look wrong.
/// </para>
/// <para>
/// <b>Nothing serializes this type, and the endpoints destructure it.</b> That is a rule kept by hand
/// here — no route delegate returns it, so <c>SessionTokenSecrecyTests</c> never walks it — and
/// <see cref="SessionHandoff"/>'s member name is what makes the day somebody does return one loud
/// rather than silent.
/// </para>
/// <para>
/// <b><see cref="Handoff"/> is <see langword="null"/> exactly when the path established no session.</b>
/// Today that is one branch of one route: a <em>first</em> issue of recovery codes replaces nothing, so
/// it sweeps no session and re-establishes none. A cookie there would be a sign-in somebody never
/// made, at onboarding, indistinguishable from a compromise — and revoking it does not undo having
/// been told it. The two sign-in paths never produce a null; each of their endpoints writes the cookie
/// through the same pattern anyway, so a handler that stopped minting is a missing cookie the tests
/// name rather than an exception in a route.
/// </para>
/// <para>
/// One generic type rather than three pairs, because the argument above is one argument. The three
/// results it wraps stay separate types for the reason <c>ReestablishedSession</c> gives about itself:
/// they mean different things and must be able to widen apart.
/// </para>
/// <para>
/// <b><see cref="Value"/> is not called <c>Result</c>, and the reason is not taste.</b> Every path that
/// produces this type is asynchronous, so the member is read as <c>(await handler.HandleAsync(…)).Result</c>
/// at every call site there is. That reads exactly like <see cref="System.Threading.Tasks.Task{TResult}.Result"/> —
/// the blocking sync-over-async call this codebase never makes — so a reader stumbles on each one, an
/// analyzer rule such as VSTHRD002 would flag them the day it is switched on, and the reader who tries to
/// "fix" the pattern reaches for <c>GetAwaiter().GetResult()</c> and turns the resemblance into the real
/// thing. Renaming it back re-creates all three.
/// </para>
/// </remarks>
/// <param name="Value">What the endpoint answers with.</param>
/// <param name="Handoff">The handle and its expiry, or <see langword="null"/> when none was opened.</param>
public sealed record Issued<TResult>(TResult Value, SessionHandoff? Handoff);
