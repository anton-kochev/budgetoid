using Application.Passkeys.Reauthentication;

namespace Application.RecoveryCodes.GenerateRecoveryCodes;

/// <summary>
/// Asks for the signed-in account's set of recovery codes to be issued, presenting the verifiers the
/// client derived and the fresh WebAuthn assertion that authorizes the issue.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here</b>, the same rule <c>EraseAccountCommand</c> and
/// <c>RevokePasskeyCommand</c> state: the only identity the handler may act on is
/// <see cref="Application.Abstractions.IUserContext.UserId"/>, because a user id declared on a command
/// is an account a caller can choose. Issuing <em>replaces</em>, so a chooseable account here would be
/// a way to destroy a stranger's recovery codes.
/// </para>
/// <para>
/// <b>Verifiers, never codes.</b> The browser mints each code, derives <c>V = HKDF(code, …)</c> and
/// sends only <c>V</c>; the account's key-encryption key comes off the same code on an independent
/// HKDF branch, so a code arriving here would hand the operator that key. There is deliberately no
/// member a code could travel in.
/// </para>
/// <para>
/// They arrive as base64url <b>text</b>, which is how every binary member of this exchange crosses
/// JSON, and the handler decodes them. A byte array on the wire would be base64 <em>anyway</em>, in a
/// different alphabet from the one the passkey members already use.
/// </para>
/// <para>
/// The assertion is a member of the command rather than a separate call from the endpoint so that
/// issuing without proof is unreachable rather than merely uncustomary: one command, one handler, the
/// gate inside it.
/// </para>
/// </remarks>
/// <param name="Verifiers">The set's verifiers, each base64url text.</param>
/// <param name="Assertion">The fresh re-authentication the issue is authorized by.</param>
public sealed record GenerateRecoveryCodesCommand(
    IReadOnlyList<string> Verifiers,
    ReauthenticationAssertion Assertion);
