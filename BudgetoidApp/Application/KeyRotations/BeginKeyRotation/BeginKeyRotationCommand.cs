using Application.Passkeys.Reauthentication;

namespace Application.KeyRotations.BeginKeyRotation;

/// <summary>
/// Asks for a content-key rotation to be opened on the signed-in account, presenting the fresh WebAuthn
/// assertion that authorizes it and the next generation's manifest of factor public keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here</b>, the rule <c>RevokePasskeyCommand</c> and
/// <c>EraseAccountCommand</c> both state: the only identity the handler may act on is
/// <c>IUserContext.UserId</c>, because a user id declared on a command is an account a caller can
/// choose.
/// </para>
/// <para>
/// <b>No <em>single</em> factor is named, and that is the shape of the change rather than a member
/// somebody dropped.</b> Under a key-encryption key there was exactly one factor a run could be begun
/// under, because re-wrapping the account's keys needed the secret that factor derives — so the command
/// carried a <c>factorId</c> and the handler compared it against the account's live passkey factors.
/// Encapsulating to a factor's <em>public</em> half needs no secret at all, so a run produces one value
/// per surviving factor — those are <see cref="Seals"/>, and the factor each one names is a property of
/// that value rather than of the run. "The factor this rotation was performed under" has stopped being a
/// question with an answer. The credential the run is filed against is now the one the assertion below
/// was verified with, which the handler receives from the gate rather than looking up — strictly
/// stronger than an identifier a client could choose, and one fewer id space on the wire.
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
/// <b>Nothing here is an unwrapped key, a private key, a key-encryption key or a PRF output</b>, and
/// nothing may be added that is. The manifest is the <em>public</em> halves sealed under the next
/// generation's content key and every seal is ciphertext <em>encapsulated to</em> one of them, and the
/// server may hold both because it holds nothing that opens either; a member carrying a private one
/// would put the account's whole plaintext within reach of the operator without reddening a test,
/// because there is no test that can notice a value the design says never arrives.
/// </para>
/// </remarks>
/// <param name="Assertion">The fresh re-authentication the begin is authorized by.</param>
/// <param name="RotationId">The client-minted identifier of this run.</param>
/// <param name="StagedManifest">
/// The next generation's manifest of factor public keys, not yet in force. Opaque here: it is
/// authenticated as a set by a key the client holds, so nothing on this side reads into it, and a
/// structural reading would be a second unverifiable grammar sitting where the client's is
/// authoritative.
/// </param>
/// <param name="StagedRotationEpoch">The generation the manifest above will be filed at.</param>
/// <param name="Seals">
/// <para>
/// One copy of the next generation's account keys per factor the account holds, each encapsulated to
/// that factor's public key. The handler judges this set against the account's live factors in both
/// directions before anything is written.
/// </para>
/// <para>
/// <b>A list rather than a dictionary keyed on the factor, and the weaker-looking shape is the one that
/// can be refused.</b> A dictionary makes a duplicate factor id unconstructible, which reads like a
/// guarantee and is the opposite of one on the wire: <c>POST /api/me/key-rotation</c> builds this
/// member from a JSON body, and JSON deserialisation into a dictionary silently drops a repeat — last
/// wins — so a request naming eleven seals for ten factors would arrive as ten and <em>nothing anywhere
/// would say so</em>. A client
/// whose randomness is not what it claims would have that fact absorbed by the binder. Kept as a list,
/// the duplicate survives into the handler, which counts distinct factor ids against the number of
/// seals and refuses first. It is the same argument
/// <c>RecoveryCodeSetValidation</c> makes about the ten factor identifiers on a card, in the same
/// direction.
/// </para>
/// </param>
public sealed record BeginKeyRotationCommand(
    ReauthenticationAssertion Assertion,
    Guid RotationId,
    ReadOnlyMemory<byte> StagedManifest,
    int StagedRotationEpoch,
    IReadOnlyList<RotationSeal> Seals);
