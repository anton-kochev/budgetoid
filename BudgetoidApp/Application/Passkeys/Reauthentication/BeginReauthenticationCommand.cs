namespace Application.Passkeys.Reauthentication;

/// <summary>
/// Asks for the request options that open a re-authentication ceremony.
/// </summary>
/// <remarks>
/// <para>
/// Parameterless, and the empty member list is the rule rather than an absence of anything worth
/// putting here. The ceremony is never a request member: the handler hardcodes
/// <see cref="Application.Abstractions.WebAuthnCeremony.Reauthentication" />, so no caller can ask
/// for a nonce from a pool. A <c>ceremony</c> field on this command — or on the anonymous sign-in
/// leg's own — would let anybody mint the nonce that authorizes destroying an account, and the whole
/// separation between the three pools would dissolve into a string somebody typed.
/// </para>
/// <para>
/// It names no account for the same reason the erasure command does not: the account is whichever one
/// the request is authenticated as.
/// </para>
/// </remarks>
public sealed record BeginReauthenticationCommand;
