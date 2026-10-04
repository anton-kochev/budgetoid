using Application.Passkeys.Reauthentication;

namespace Application.Erasure.CancelScheduledErasure;

/// <summary>
/// Asks for the signed-in account's scheduled erasure to be withdrawn, presenting the fresh WebAuthn
/// assertion that authorizes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here</b>, for the rule <see cref="Users.EraseAccount.EraseAccountCommand"/>
/// states: the schedule withdrawn is whichever one the request is authenticated as, read off
/// <see cref="Abstractions.IUserContext.UserId"/>. The assertion names a credential <em>handle</em>, and
/// the gate's owner-scoped lookup makes it incapable of selecting anybody else.
/// </para>
/// <para>
/// The assertion is a member rather than a separate call from the endpoint for the immediate erasure's
/// reason: a withdrawal without proof is unreachable rather than merely uncustomary.
/// </para>
/// </remarks>
public sealed record CancelScheduledErasureCommand(ReauthenticationAssertion Assertion);
