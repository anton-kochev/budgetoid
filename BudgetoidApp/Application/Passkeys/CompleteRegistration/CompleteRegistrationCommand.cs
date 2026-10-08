using Domain.Users;

namespace Application.Passkeys.CompleteRegistration;

/// <summary>
/// The response an authenticator produced for a registration ceremony, and the key material the factor
/// it stands for is to hold: that factor's wrapped private key and the account's keys encapsulated to
/// its public half.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three key-custody members are declared non-nullable and are deliberately not
/// <c>required</c></b>, matching every other member here and the argument
/// <c>RecoveryCodeGenerationRequest</c> already makes about its own: an absent member binds to
/// <see langword="null"/> despite the declaration and reaches
/// <see cref="CompleteRegistrationHandler"/>'s own refusal, which is a sentence this ceremony worded
/// for the person holding the device. Marking them required would buy a framework 400 instead, raised
/// before the handler has judged anything the authenticator signed — so a device that genuinely cannot
/// do PRF would be told its <em>payload</em> was malformed, which is the wrong thing to send somebody
/// off to debug.
/// </para>
/// <para>
/// <see cref="FactorId"/> is <see cref="string"/> rather than <see cref="Guid"/> for the same reason,
/// and for one more: the framework parses more spellings of a uuid than this contract accepts, so a
/// <see cref="Guid"/> member would silently widen the wire format past what the handler's own rule
/// allows.
/// </para>
/// </remarks>
/// <param name="ClientDataJson">
/// <c>response.clientDataJSON</c>, base64url. It is decoded and parsed where it lands and never
/// re-encoded — the bytes are what the ceremony was bound to.
/// </param>
/// <param name="AttestationObject">
/// <c>response.attestationObject</c>, base64url. Under <c>attestation: "none"</c> this carries the
/// authenticator data and an empty statement.
/// </param>
/// <param name="ClientExtensionResults">
/// <c>getClientExtensionResults()</c>, or null when the client reported none.
/// </param>
/// <param name="FactorId">
/// The client-minted identifier of the factor this passkey stands for, and the associated data of the
/// wrapped private key below — see <see cref="WrappedAccountKeys.FactorId"/> for why it is
/// deliberately not <c>credentials.id</c>. One uuid in one spelling: the <b>lower-case</b> 36-character
/// hyphenated form with no surrounding whitespace, which is what a <see cref="Guid"/> renders as and
/// therefore what every later read hands back. This contract is cross-client, and a value the browser
/// cannot recognise as the bytes it bound is a factor whose private key never unwraps. Enforced by
/// <see cref="Application.Security.CanonicalIdentifier.TryParse"/>, which compares the text against what
/// the parsed value renders as — <see cref="Guid.TryParseExact(string, string, out Guid)"/> under
/// <c>"D"</c> admits upper-case and mixed-case hex and trims whitespace before it reads the format at
/// all, so the format alone does not pin a spelling.
/// </param>
/// <param name="WrappedPrivateKey">
/// This factor's ECDH P-256 private key, <em>wrapped under</em> the key-encryption key the PRF output
/// of this ceremony yields: one base64url envelope of the AEAD framing, judged by
/// <see cref="WrappedPrivateKeyEnvelope.TryDecode"/>.
/// </param>
/// <param name="EncapsulatedAccountKeys">
/// The account's content key and index key as one 64-byte plaintext, <em>encapsulated to</em> the public
/// half of that key pair: one base64url value of the encapsulation framing, judged by
/// <see cref="EncapsulatedAccountKeysEnvelope.TryDecode"/>. <b>Not the same shape and not judged by the
/// same rule as the member above</b> — a different suite, a different floor and a different width.
/// </param>
/// <param name="Manifest">
/// The account's factor manifest as it stands <em>after</em> this passkey joins the set: every factor's
/// public key, <em>sealed under</em> the account's content key, as one base64url envelope of the AEAD
/// framing, judged by <see cref="FactorManifestEnvelope.TryDecode"/>. <b>A factor change unaccompanied
/// by one is refused</b>, because <see cref="FactorManifest"/> is the sole carrier of every factor's
/// public key and a rotation encapsulates to what it names: a passkey registered while the manifest
/// still described the previous set would be a factor no client could learn exists. Nothing on this side
/// can check that the blob names this passkey — it is sealed under a key this server has never held —
/// so what is enforced is presence, framing and the epoch below.
/// </param>
/// <param name="RotationEpoch">
/// The generation the manifest above is being written under, which the client sets to one greater than
/// the epoch the server last reported and binds into the manifest's associated data.
/// <b>The server stores the client's number and never one it computes.</b> Its own job is to refuse
/// anything that is not the stored generation plus one, which is
/// <see cref="FactorManifest.Promote"/>'s arithmetic and is checked against the loaded row.
/// <para>
/// <b>An <see cref="int"/> where every other member here is a <see cref="string"/>, and the difference
/// is the wire and not a style.</b> The string members are text standing for bytes or for one chosen
/// spelling of a uuid, which the framework would widen or refuse in front of the prf gate; a generation
/// is a JSON number with one spelling and nothing for a parse to be lenient about. Not <c>required</c>,
/// like every member here: an omitted one binds to <c>0</c>, which is the value
/// <see cref="FactorManifest.MinimumRotationEpoch"/> keeps free to mean <em>no manifest row</em>, so it
/// reaches <see cref="FactorManifest.Promote"/> and is refused there with a sentence naming the
/// generation the account is actually at.
/// </para>
/// </param>
public sealed record CompleteRegistrationCommand(
    string ClientDataJson,
    string AttestationObject,
    PasskeyClientExtensionResults? ClientExtensionResults,
    string FactorId,
    string WrappedPrivateKey,
    string EncapsulatedAccountKeys,
    string Manifest,
    int RotationEpoch);

/// <summary>The client extension results a registration response may carry.</summary>
public sealed record PasskeyClientExtensionResults(PasskeyPrfResults? Prf);

/// <summary>
/// What the client says the <c>prf</c> extension did.
/// </summary>
/// <remarks>
/// Asserted by the client, covered by no signature, and not derivable from anything the server can
/// see. It is stored nowhere and believed of nothing — yet registration only completes when it is
/// present and says <c>true</c>, because a device that cannot derive the account's keys is worth
/// turning away while the person is still holding it. Absent, explicitly null, and <c>false</c> are
/// alike refused, and the type is nullable so that all three reach that refusal: a non-nullable
/// <c>bool</c> would make an omitted member mean <c>false</c> by a language default nobody chose,
/// and an explicit <c>null</c> fail deserialization, answering with the framework's generic problem
/// document instead of the sentence this ceremony words for the person holding the device. See
/// <see cref="CompleteRegistrationHandler"/> for why that gate is a product rule rather than an
/// enforced one, and for what a later rule relying on PRF must key on instead.
/// </remarks>
public sealed record PasskeyPrfResults(bool? Enabled);
