using Domain.Sessions;

namespace Application.RecoveryCodes.GenerateRecoveryCodes;

/// <summary>
/// The session a replacement opened over the account's new set of recovery codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own record, in this namespace, rather than the passkey path's <c>EstablishedSession</c>.</b>
/// The two carry the same two members today and mean different things: that one is what a completed
/// assertion opened, this one is what a <em>replacement</em> re-opened for somebody the sweep may have
/// just signed out. Sharing the type would make a later widening of either — the assertion leg gaining
/// a member for a client that runs a WebAuthn ceremony, this one gaining a fact about the sweep —
/// silently change the other route's wire shape.
/// </para>
/// <para>
/// <b><see cref="Kind" /> stays even though it is always <see cref="SessionKind.Full" /> here.</b>
/// <c>Session.KindFor</c> enumerates every <c>CredentialType</c> by name precisely so that a changed
/// derivation is somebody's decision rather than a default, and a response that reported the kind it
/// assumed rather than the kind the row carries would be the one place that decision failed to reach.
/// It also keeps this record the same shape as <c>RedeemedRecoveryCode</c>, which is what a client
/// meets first on the very journey that leads here.
/// </para>
/// <para>
/// <b>No session id</b>, for the reason the assertion and redemption responses both give: an id would
/// hand the client a stable handle to a session, and the likeliest way this design is broken later is
/// somebody deciding that handle is close enough to a token to start accepting it.
/// </para>
/// </remarks>
/// <param name="Kind">How much of the account this sign-in reaches.</param>
/// <param name="ExpiresAtUtc">When the session stops being live.</param>
public sealed record ReestablishedSession(SessionKind Kind, DateTime ExpiresAtUtc);
