using Domain.Sessions;

namespace Application.RecoveryCodes.RedeemRecoveryCode;

/// <summary>
/// The session one redeemed recovery code opened, and how many codes the card has left.
/// </summary>
/// <remarks>
/// <para>
/// <b>The session's id is deliberately absent</b>, for the reason <c>EstablishedSession</c> gives:
/// returning it would hand the client a stable handle to a session, and the likeliest way this design
/// is broken later is somebody deciding that handle is close enough to a token to start accepting it.
/// </para>
/// <para>
/// <b><see cref="Remaining"/> is what makes this record more than the assertion leg's.</b> Redeeming
/// spends a code, so the screen the person lands on has to be able to say how many ways back in they
/// still hold — and that number has just changed as a direct result of this request, which is the one
/// moment it cannot be read from anywhere else. It is the count <em>after</em> the consume, because the
/// count before it describes a card that no longer exists.
/// </para>
/// <para>
/// No hash, no verifier, no credential id and no account id. A stored hash is the value a redemption is
/// matched against, and an id in a response body is an id in a client log.
/// </para>
/// </remarks>
/// <param name="Kind">How much of the account this sign-in reaches.</param>
/// <param name="ExpiresAtUtc">When the session stops being live.</param>
/// <param name="Remaining">How many unredeemed codes the account holds now.</param>
public sealed record RedeemedRecoveryCode(SessionKind Kind, DateTime ExpiresAtUtc, int Remaining);
