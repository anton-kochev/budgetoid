using Application.Passkeys.Reauthentication;

namespace Application.Users.EraseAccount;

/// <summary>
/// Asks for the signed-in account, and everything owned beneath it, to be erased, presenting the
/// fresh WebAuthn assertion that authorizes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here, and that rule is unchanged.</b> The only identity the handler may
/// act on is <see cref="Application.Abstractions.IUserContext.UserId" />; a user id declared on this
/// command would be an account a caller could choose, and the endpoint would be one deserialized field
/// away from erasing somebody else's.
/// </para>
/// <para>
/// This command carrying members is not a relaxation of that rule. The members name a credential
/// <em>handle</em>, and <c>PasskeyReauthentication</c> resolves it through an owner-scoped lookup
/// keyed on the request's own account — so a handle belonging to anyone else answers to nothing rather
/// than selecting them. The rule is "no account may be named", not "no members".
/// </para>
/// <para>
/// The assertion is a member of the command rather than a separate call from the endpoint so that
/// erasure without proof is unreachable rather than merely uncustomary: one command, one handler, the
/// gate inside it.
/// </para>
/// </remarks>
public sealed record EraseAccountCommand(ReauthenticationAssertion Assertion);
