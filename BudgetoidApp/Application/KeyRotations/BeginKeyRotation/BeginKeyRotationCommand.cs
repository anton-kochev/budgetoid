using Application.Passkeys.Reauthentication;

namespace Application.KeyRotations.BeginKeyRotation;

/// <summary>
/// Asks for a content-key rotation to be opened on the signed-in account, presenting the fresh WebAuthn
/// assertion that authorizes it and the next generation of the account's two wrapped keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here</b>, the rule <c>RevokePasskeyCommand</c> and
/// <c>EraseAccountCommand</c> both state: the only identity the handler may act on is
/// <c>IUserContext.UserId</c>, because a user id declared on a command is an account a caller can
/// choose. <see cref="FactorId" /> is not a relaxation of it — it names one of the caller's <em>own</em>
/// factors, and a factor identifier belonging to anyone else is filed under another account and
/// answers nothing when the handler looks it up.
/// </para>
/// <para>
/// <b>Two id spaces meet here and are never compared.</b> <see cref="FactorId" /> is a
/// <c>wrapped_account_keys.factor_id</c> — the associated data both envelopes were sealed with;
/// <c>Assertion.CredentialId</c> is the WebAuthn handle of the authenticator that signed the proof. A
/// person legitimately proves with the very passkey the envelopes are wrapped under, so a handler
/// checking one against the other would be refusing a correct request — and the check it looks like it
/// is making is already made, twice, by the gate's owner-scoped key lookup and by the factor listing.
/// </para>
/// <para>
/// <b>The assertion is a member of the command rather than a separate call from the endpoint</b>, so
/// that a begin without proof is unreachable rather than merely uncustomary: one command, one handler,
/// the gate inside it.
/// </para>
/// <para>
/// <b><see cref="RotationId" /> is minted by the client and is not a secret.</b> It is the value every
/// later chunk quotes to say which run it is continuing and the value each rewritten row carries in its
/// own <c>rotation_id</c> column. Deriving it on the server would mean handing it back and hoping the
/// client kept it; minted by the client, a begin re-sent after a network timeout carries the same
/// identifier and converges on the same staged row.
/// </para>
/// <para>
/// <b>Nothing here is an unwrapped key, a key-encryption key or a PRF output</b>, and nothing may be
/// added that is. Both envelopes were sealed in the browser under a key the server never sees; a member
/// carrying the value that opens them would put the account's whole plaintext within reach of the
/// operator without reddening a test, because there is no test that can notice a value the design says
/// never arrives.
/// </para>
/// </remarks>
/// <param name="Assertion">The fresh re-authentication the begin is authorized by.</param>
/// <param name="FactorId">
/// The factor the two envelopes below were wrapped under. Singular because an account holds exactly one
/// passkey today; <b>the rule the handler applies is set equality against the account's live passkey
/// factors</b>, so the day a second passkey becomes registrable this member becomes a set and every
/// begin has to carry both. See <c>BeginKeyRotationHandler</c> for what the weaker reading costs.
/// </param>
/// <param name="RotationId">The client-minted identifier of this run.</param>
/// <param name="WrappedContentKey">The next generation's wrapped content key, not yet in force.</param>
/// <param name="WrappedIndexKey">The next generation's wrapped index key, not yet in force.</param>
public sealed record BeginKeyRotationCommand(
    ReauthenticationAssertion Assertion,
    Guid FactorId,
    Guid RotationId,
    ReadOnlyMemory<byte> WrappedContentKey,
    ReadOnlyMemory<byte> WrappedIndexKey);
