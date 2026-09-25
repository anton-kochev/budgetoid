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
/// <param name="Manifest">
/// The account's factor manifest as it stands <em>after</em> this passkey leaves the set: every
/// remaining factor's public key, <em>sealed under</em> the account's content key, as one base64url
/// envelope of the AEAD framing, judged by
/// <see cref="Application.Passkeys.FactorManifestEnvelope.TryDecode"/>. <b>A factor change unaccompanied
/// by one is refused</b>, the same rule <c>CompleteRegistrationCommand</c> and
/// <c>GenerateRecoveryCodesCommand</c> state for a factor joining. It reads harder on this path
/// than on either of those: the revoked passkey's <c>wrapped_account_keys</c> row leaves with its
/// credential by the database's own cascade, so a manifest left behind names a factor that no longer
/// holds a copy of the account's keys — and since the manifest is the sole carrier of every factor's
/// public key, the next rotation would encapsulate those keys to an authenticator the person has just
/// removed, which is very often an authenticator they removed because somebody else has it. Nothing on
/// this side can check that the blob names the remaining factors: it is sealed under a key this server
/// has never held, so what is enforced is presence, framing and the epoch below.
/// </param>
/// <param name="RotationEpoch">
/// The generation the manifest above is written under, which the client sets to one greater than the
/// epoch the server last reported and binds into the manifest's associated data. <b>The server stores
/// the client's number and never one it computes</b>; its job is to refuse anything that is not the
/// stored generation plus one, which is <see cref="Domain.Users.FactorManifest.Promote"/>'s arithmetic
/// against the loaded row.
/// <para>
/// <b>An <see cref="int"/> where every other member of this request is a <see cref="string"/>, and the
/// difference is the wire rather than a departure.</b> Those are text standing for bytes, and a typed
/// member there would let the framework widen the format or refuse it in front of the
/// re-authentication gate; a generation is a JSON number with one spelling and nothing for a parse to
/// be lenient about. Not <c>required</c>, like every other member: an omitted one binds to <c>0</c>,
/// which <see cref="Domain.Users.FactorManifest.MinimumRotationEpoch"/> keeps free to mean <em>no
/// manifest row</em>, so it reaches <see cref="Domain.Users.FactorManifest.Promote"/> past the gate and
/// is refused there.
/// </para>
/// </param>
/// <param name="Assertion">The fresh re-authentication the removal is authorized by.</param>
public sealed record RevokePasskeyCommand(
    Guid CredentialId,
    string Manifest,
    int RotationEpoch,
    ReauthenticationAssertion Assertion);
