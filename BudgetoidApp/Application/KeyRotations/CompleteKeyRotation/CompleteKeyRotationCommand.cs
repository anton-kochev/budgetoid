namespace Application.KeyRotations.CompleteKeyRotation;

/// <summary>
/// The last step of a content-key rotation: promote the staged generation into the account's live rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here</b>, the rule <c>BeginKeyRotationCommand</c>,
/// <c>ResealRowsCommand</c>, <c>RevokePasskeyCommand</c> and <c>EraseAccountCommand</c> all state: the
/// only identity the handler may act on is <c>IUserContext.UserId</c>, because a user id declared on a
/// command is an account a caller can choose. It matters more on this command than on any of them —
/// the act it asks for is the one write in the product that nothing can put back.
/// </para>
/// <para>
/// <b>One member, and every other candidate is refused for the same reason.</b> The staged manifest,
/// the epoch it is filed at and the encapsulated value each factor adopts were all fixed by the begin
/// and are already on file; carried again here they would be a second statement of the same values,
/// able to disagree with the staged one at the one moment a disagreement cannot be undone. So this
/// request says which run it is finishing and nothing else, and the handler takes every value from what
/// that run staged.
/// </para>
/// <para>
/// <b><see cref="RotationId" /> is not proof of anything.</b> It is the client-minted identifier the
/// begin staged, so it is a fact a client remembers rather than a capability —
/// <c>CompleteKeyRotationHandler</c> checks it against the rotation the account actually has staged and
/// then uses the <em>staged</em> one for everything downstream, because the completeness gate takes an
/// identifier and cannot tell which run an account has in flight.
/// </para>
/// <para>
/// <b>There is no re-authentication member, and that is a decision.</b> A completion spends no nonce and
/// verifies no assertion: the begin is the act a passkey is proved for, and a run in flight is already
/// the account's own. What guards this request is the identity on the session and the ordered refusals
/// in the handler.
/// </para>
/// </remarks>
/// <param name="RotationId">The run being completed, as <c>KeyRotation.RotationId</c> spells it.</param>
public sealed record CompleteKeyRotationCommand(Guid RotationId);
