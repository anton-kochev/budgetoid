using Application.Passkeys.Reauthentication;

namespace Application.Passkeys.RevokePasskey;

/// <summary>
/// Asks for one passkey of the signed-in account to be revoked, presenting the fresh WebAuthn
/// assertion that authorizes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here</b>, which is the same rule
/// <see cref="Application.Users.EraseAccount.EraseAccountCommand" /> states: the only identity the
/// handler may act on is <see cref="Application.Abstractions.IUserContext.UserId" />, because a user
/// id declared on a command is an account a caller can choose.
/// </para>
/// <para>
/// <see cref="CredentialId" /> is not a relaxation of it. It names a credential <em>handle</em> — a
/// <c>credentials.id</c> — that the handler resolves through
/// <c>IPasskeyRepository.FindPasskeyCredentialAsync</c>, whose predicate carries the request's own
/// user id. A handle belonging to anyone else selects nothing rather than selecting them, so what the
/// caller may choose is which of <em>its own</em> passkeys goes. The rule is "no account may be
/// named", not "no members".
/// </para>
/// <para>
/// <b>Two id spaces meet on this command and are never compared.</b> <see cref="CredentialId" /> is
/// the primary key of the row being removed; <c>Assertion.CredentialId</c> is the WebAuthn credential
/// handle of the authenticator that signed the proof. A person may legitimately prove with the very
/// passkey they are removing, so a handler that checked one against the other would be refusing a
/// correct request.
/// </para>
/// <para>
/// The assertion is a member of the command rather than a separate call from the endpoint so that
/// revocation without proof is unreachable rather than merely uncustomary: one command, one handler,
/// the gate inside it.
/// </para>
/// </remarks>
/// <param name="CredentialId">The <c>credentials.id</c> of the passkey to remove.</param>
/// <param name="Assertion">The fresh re-authentication the removal is authorized by.</param>
public sealed record RevokePasskeyCommand(Guid CredentialId, ReauthenticationAssertion Assertion);
