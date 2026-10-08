using Application.Passkeys.Reauthentication;

namespace Application.Users.ChangeEmail;

/// <summary>
/// Asks for the signed-in account to be moved to the Google identity and address the provider just
/// asserted, presenting the fresh WebAuthn assertion that authorizes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here</b>, the rule
/// <see cref="Application.Users.EraseAccount.EraseAccountCommand" /> states: the handler acts on
/// <see cref="Application.Abstractions.IUserContext.UserId" /> and nothing else.
/// </para>
/// <para>
/// <see cref="Subject" /> and <see cref="Email" /> are the provider's claims, read off a token the
/// endpoint has already validated; nothing here judges them as a caller's choice.
/// </para>
/// </remarks>
/// <param name="Subject">The provider's <c>sub</c> claim for the Google account chosen.</param>
/// <param name="Email">The address the provider asserts for that Google account.</param>
/// <param name="Assertion">The fresh re-authentication the change is authorized by.</param>
public sealed record ChangeEmailCommand(string Subject, string Email, ReauthenticationAssertion Assertion);
